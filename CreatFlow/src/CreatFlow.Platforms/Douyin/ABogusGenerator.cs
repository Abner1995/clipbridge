namespace CreatFlow.Platforms.Douyin;

using CreatFlow.Platforms.Douyin.Crypto;

/// <summary>
/// 纯 C# a_bogus 签名生成器。
///
/// 端口自 fangai/douyin/a_bogus.py，算法管线: SM3 → Modified RC4 → Custom Base64。
///
/// a_bogus 是抖音 Web API 的 URL query 签名参数（~192-200 chars），
/// 每次请求需要不同的值（因时间戳不同），重复使用会触发 403。
///
/// 用法:
///   var ab = ABogusGenerator.Generate(
///       urlParams: "aid=1128&support_h265=1&...",
///       userAgent: "Mozilla/5.0 ...",
///       body: "{...}"
///   );
/// </summary>
public static class ABogusGenerator
{
    // ═══ Custom Base64 Tables ═══

    private const string S3Table = "ckdp1h4ZKsUB80/Mfvw36XIgR25+WQAlEi7NLboqYTOPuzmFjJnryx9HVGDaStCe";
    private const string S4Table = "Dkdpgh2ZmsQB80/MfvV36XI1R45-WUAlEixNLwoqYTOPuzKFjJnry79HbGcaStCe";

    // ═══ Constants ═══

    private const string Salt = "dhzx";
    private static readonly byte[] RC4KeyUA = [0, 1, 14];
    private static readonly byte[] RC4KeyFinal = [121]; // 0x79 = 'y'

    // Environment fingerprint (matching Python DEFAULT_ENV)
    private const string DefaultEnv = "1536|747|1536|834|0|30|0|0|1536|834|1536|864|1525|747|24|24|Win32";

    // Reorder indices: 55-byte array → 50-byte fingerprint
    private static readonly int[] ReorderIndices =
    [
        9, 18, 30, 35, 47, 4, 44, 19, 10, 23, 12, 40, 25, 42,
        3, 22, 38, 21, 5, 45, 1, 29, 6, 43, 33, 14, 36, 37,
        2, 46, 15, 48, 31, 26, 16, 13, 8, 41, 27, 17, 39,
        20, 11, 0, 34, 7, 50, 51, 53, 54,
    ];

    // ═══ Public API ═══

    /// <summary>
    /// 生成 a_bogus 签名。
    /// </summary>
    /// <param name="urlParams">URL query string（不含 msToken 和 a_bogus）</param>
    /// <param name="userAgent">浏览器 User-Agent</param>
    /// <param name="body">POST body（GET 请求为空）</param>
    /// <param name="aid">应用 ID（douyin.com = 6383，creator.douyin.com = 1128 或 2906）</param>
    public static string Generate(
        string urlParams,
        string userAgent,
        string? body = null,
        int aid = 6383)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long startMs = nowMs;
        long endMs = startMs + Random.Shared.Next(1, 50);

        // 1. SM3 double hash of URL params
        var urlHash = SM3.DoubleHash(System.Text.Encoding.UTF8.GetBytes(urlParams + Salt));
        // 2. SM3 double hash of body
        var bodyStr = body ?? "";
        var bodyHash = SM3.DoubleHash(System.Text.Encoding.UTF8.GetBytes(bodyStr + Salt));
        // 3. Modified RC4 encrypt UA → S3 Base64 → SM3 hash
        var uaRc4 = ModifiedRC4(RC4KeyUA, System.Text.Encoding.UTF8.GetBytes(userAgent));
        var uaB64 = CustomBase64Encode(uaRc4, S3Table);
        var uaHash = SM3.Hash(System.Text.Encoding.UTF8.GetBytes(uaB64));

        // 4. Build 55-byte fingerprint
        var arr55 = Build55ByteArray(startMs, endMs, urlHash, bodyHash, uaHash, aid);
        // 5. Reorder to 50-byte fingerprint
        var fingerprint = new byte[50];
        for (int i = 0; i < 50; i++)
            fingerprint[i] = arr55[ReorderIndices[i]];

        // 6. Environment bytes
        var envBytes = System.Text.Encoding.ASCII.GetBytes(DefaultEnv);
        // 7. Timestamp bytes
        var tsBytes = TimestampBytes(startMs, endMs);
        // 8. XOR convergence
        byte xorByte = 0;
        foreach (var b in envBytes) xorByte ^= b;

        // 9. Concatenate: 50B fingerprint + env + ts + xor
        var payload95 = new byte[50 + envBytes.Length + tsBytes.Length + 1];
        fingerprint.CopyTo(payload95, 0);
        envBytes.CopyTo(payload95, 50);
        tsBytes.CopyTo(payload95, 50 + envBytes.Length);
        payload95[^1] = xorByte;

        // 10. Shuffle 95 → 132 bytes
        var payload132 = Shuffle95To132(payload95);
        // 11. 12-byte random prefix → 144 bytes
        var randomPrefix = new byte[12];
        Random.Shared.NextBytes(randomPrefix);
        var payload144 = new byte[144];
        randomPrefix.CopyTo(payload144, 0);
        payload132.CopyTo(payload144, 12);

        // 12. Standard RC4 encrypt with key 0x79
        var encrypted = StandardRC4(RC4KeyFinal, payload144);
        // 13. S4 Base64 encode → a_bogus
        return CustomBase64Encode(encrypted, S4Table);
    }

    // ═══ Modified RC4 ═══

    private static byte[] ModifiedRC4(byte[] key, byte[] data)
    {
        var sbox = new int[256];
        for (int i = 0; i < 256; i++)
            sbox[i] = 255 - i; // inverted S-box

        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j * sbox[i] + j + key[i % key.Length]) % 256;
            (sbox[i], sbox[j]) = (sbox[j], sbox[i]);
        }

        var result = new byte[data.Length];
        int a = 0, b = 0;
        for (int k = 0; k < data.Length; k++)
        {
            a = (a + 1) % 256;
            int sa = sbox[a];
            b = (b * sa + b + sa) % 256;
            (sbox[a], sbox[b]) = (sbox[b], sbox[a]);
            result[k] = (byte)(data[k] ^ sbox[(sbox[a] + sbox[b]) % 256]);
        }
        return result;
    }

    // ═══ Standard RC4 ═══

    private static byte[] StandardRC4(byte[] key, byte[] data)
    {
        var sbox = new int[256];
        for (int i = 0; i < 256; i++)
            sbox[i] = i;

        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + sbox[i] + key[i % key.Length]) % 256;
            (sbox[i], sbox[j]) = (sbox[j], sbox[i]);
        }

        var result = new byte[data.Length];
        int a = 0, b = 0;
        for (int k = 0; k < data.Length; k++)
        {
            a = (a + 1) % 256;
            b = (b + sbox[a]) % 256;
            (sbox[a], sbox[b]) = (sbox[b], sbox[a]);
            result[k] = (byte)(data[k] ^ sbox[(sbox[a] + sbox[b]) % 256]);
        }
        return result;
    }

    // ═══ Custom Base64 ═══

    private static string CustomBase64Encode(byte[] data, string alphabet)
    {
        var result = new System.Text.StringBuilder();
        int padding = (3 - data.Length % 3) % 3;
        var padded = new byte[data.Length + padding];
        data.CopyTo(padded, 0);

        for (int i = 0; i < padded.Length; i += 3)
        {
            int block = (padded[i] << 16) | (padded[i + 1] << 8) | padded[i + 2];
            result.Append(alphabet[(block >> 18) & 0x3F]);
            result.Append(alphabet[(block >> 12) & 0x3F]);
            if (i + 2 < data.Length)
            {
                result.Append(alphabet[(block >> 6) & 0x3F]);
                result.Append(alphabet[block & 0x3F]);
            }
            else if (i + 1 < data.Length)
            {
                result.Append(alphabet[(block >> 6) & 0x3F]);
                // omit last char (no padding char in custom base64)
            }
            // else: last full block
        }
        return result.ToString();
    }

    // ═══ 55-byte Fingerprint Builder ═══

    private static byte[] Build55ByteArray(
        long ts1, long ts2,
        byte[] urlHash, byte[] bodyHash, byte[] uaHash,
        int aid)
    {
        var arr = new byte[55];

        arr[0] = 41;
        arr[1] = (byte)(ts1 & 0xFF);
        arr[2] = 5;
        arr[3] = (byte)(ts2 & 0xFF);

        AssignTimestamp(arr, ts1, 4);

        arr[10] = (byte)(1 & 0xFF);
        arr[11] = (byte)((1 / 256) & 0xFF);

        arr[12] = (byte)((aid >> 8) & 0xFF);
        arr[13] = (byte)(aid & 0xFF);

        arr[14] = (byte)(ts1 & 0xFF);
        arr[15] = (byte)((ts1 >> 8) & 0xFF);
        arr[16] = 1;
        arr[17] = 0;

        arr[18] = 0; arr[19] = 0; // pageId

        arr[20] = (byte)(ts2 > 0 ? (int)(ts2 / 256 / 256 / 256) & 0xFF : 0);
        arr[21] = 0;
        arr[22] = (byte)((int)(ts1 / 256 / 256) & 0xFF);
        arr[23] = (byte)((int)(ts1 / 256 / 256 / 256 / 256) & 0xFF);

        arr[24] = 0;
        arr[25] = 28; // platform identifier
        arr[26] = 1;
        arr[27] = 0; arr[28] = 0; arr[29] = 0;

        arr[30] = (byte)((int)(ts1 / 256 / 256 / 256) & 0xFF);
        arr[31] = (byte)((int)(ts1 / 256 / 256 / 256 / 256) & 0xFF);

        arr[32] = 0; arr[33] = 0; arr[34] = 0; arr[35] = 0;
        arr[36] = 0; arr[37] = 0;

        // SM3 hash fragments
        if (urlHash.Length >= 23) { arr[38] = urlHash[21]; arr[39] = urlHash[22]; }
        if (bodyHash.Length >= 23) { arr[40] = bodyHash[21]; arr[41] = bodyHash[22]; }
        if (uaHash.Length >= 25) { arr[42] = uaHash[23]; arr[43] = uaHash[24]; }

        AssignTimestamp(arr, ts2, 44);

        arr[50] = 0; arr[51] = 0; arr[52] = 0; arr[53] = 0; arr[54] = 0;

        return arr;
    }

    private static void AssignTimestamp(byte[] arr, long ts, int offset)
    {
        arr[offset]     = (byte)(ts & 0xFF);
        arr[offset + 1] = (byte)((ts >> 8) & 0xFF);
        arr[offset + 2] = (byte)((ts >> 16) & 0xFF);
        arr[offset + 3] = (byte)((ts >> 24) & 0xFF);
        arr[offset + 4] = (byte)((ts / 256 / 256 / 256 / 256) & 0xFF);
        arr[offset + 5] = (byte)((ts / 256 / 256 / 256 / 256 / 256 / 256) & 0xFF);
    }

    // ═══ Timestamp Bytes ═══

    private static byte[] TimestampBytes(long ts1, long ts2)
    {
        var result = new byte[10];
        int i = 0;
        foreach (var ts in new[] { ts1, ts2 })
        {
            result[i++] = (byte)(ts & 0xFF);
            result[i++] = (byte)((ts >> 8) & 0xFF);
            result[i++] = (byte)((ts >> 16) & 0xFF);
            result[i++] = (byte)((ts >> 24) & 0xFF);
            result[i++] = (byte)((ts / 256 / 256 / 256 / 256) & 0xFF);
        }
        return result;
    }

    // ═══ 95 → 132 Shuffle ═══

    private static byte[] Shuffle95To132(byte[] data)
    {
        var result = new byte[132];
        for (int i = 0; i < Math.Min(95, 132); i++)
            result[i] = data[i];
        for (int i = 95; i < 132; i++)
        {
            int j = (i - 95) % data.Length;
            result[i] = (byte)(data[j] ^ ((i * 13 + 7) & 0xFF));
        }
        return result;
    }
}
