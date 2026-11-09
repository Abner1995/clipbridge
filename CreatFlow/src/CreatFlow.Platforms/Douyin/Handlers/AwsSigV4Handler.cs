using System.Security.Cryptography;

namespace CreatFlow.Platforms.Douyin.Handlers;

/// <summary>
/// AWS Signature V4 签名 Handler。
///
/// 用于抖音 ImageX / TOS（视频/图片上传）服务的请求签名。
/// 对标 Python fangai/douyin/signer.py 和 examples/douyin/collection/createcollection.py。
///
/// 签名流程：
///   1. 构建 CanonicalRequest
///   2. 构建 StringToSign
///   3. 计算 SigningKey
///   4. 计算 Signature
///   5. 组装 Authorization 头
/// </summary>
public sealed class AwsSigV4Handler : DelegatingHandler
{
    private readonly string _accessKeyId;
    private readonly string _secretAccessKey;
    private readonly string _region;
    private readonly string _service;

    public AwsSigV4Handler(
        string accessKeyId,
        string secretAccessKey,
        string region = "cn-north-1",
        string service = "imagex")
    {
        _accessKeyId = accessKeyId;
        _secretAccessKey = secretAccessKey;
        _region = region;
        _service = service;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // 优先使用已存在的 x-amz-date（由上游 Handler 如 StsAuthHandler 设置），
        // 确保签名与请求头中的时间戳一致
        string amzDate, dateStamp;
        if (request.Headers.TryGetValues("x-amz-date", out var existingDates))
        {
            amzDate = existingDates.First();
            dateStamp = amzDate[..8]; // yyyyMMdd 从 yyyyMMddTHHmmssZ 截取
        }
        else
        {
            var now = DateTime.UtcNow;
            amzDate = now.ToString("yyyyMMddTHHmmssZ");
            dateStamp = now.ToString("yyyyMMdd");
            request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        }

        // 计算 payload hash
        var payloadHash = await ComputePayloadHash(request);

        // 1. 构建 Canonical Request
        //
        // 字节跳动 VOD/ImageX 只签 x-amz-date / x-amz-security-token / x-amz-content-sha256，
        // 不签 host 和 User-Agent 等业务头（与标准 AWS SigV4 不同）。
        // 对标 Python fangai/douyin/signer.py sign_request() 中 sign_headers 的构建逻辑。
        var canonicalHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var h in request.Headers)
        {
            var key = h.Key.ToLowerInvariant();
            if (key is "x-amz-date" or "x-amz-security-token" or "x-amz-content-sha256")
            {
                canonicalHeaders[key] = string.Join(",", h.Value);
            }
        }
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
            {
                var key = h.Key.ToLowerInvariant();
                // POST 请求时签 content-type（PUT 上传分片的 Content-Type 也需签名）
                if (key is "content-type")
                {
                    canonicalHeaders[key] = string.Join(",", h.Value);
                }
            }
        }
        var signedHeaders = string.Join(";", canonicalHeaders.Keys);
        var canonicalHeadersStr = string.Join("\n",
            canonicalHeaders.Select(kv => $"{kv.Key}:{kv.Value}")) + "\n";

        var canonicalQuery = string.Join("&",
            request.RequestUri!.Query.TrimStart('?')
                .Split('&')
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Split('=', 2))
                .Select(p => new { Key = Uri.EscapeDataString(p[0]), Val = p.Length > 1 ? Uri.EscapeDataString(p[1]) : "" })
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Key}={x.Val}"));

        var canonicalRequest = string.Join("\n",
            request.Method.Method,
            request.RequestUri.AbsolutePath,
            canonicalQuery,
            canonicalHeadersStr,
            signedHeaders,
            payloadHash);

        // DEBUG: dump canonical request for comparison with Python signer.py
        // Console.WriteLine($"[AwsSigV4] Canonical Request:");
        // Console.WriteLine(canonicalRequest);
        // Console.WriteLine($"[AwsSigV4] --- end canonical request ---");
        // Console.WriteLine($"[AwsSigV4] amzDate={amzDate}, dateStamp={dateStamp}, region={_region}, service={_service}");

        // 2. StringToSign
        var algorithm = "AWS4-HMAC-SHA256";
        var credentialScope = $"{dateStamp}/{_region}/{_service}/aws4_request";
        var stringToSign = string.Join("\n",
            algorithm,
            amzDate,
            credentialScope,
            HashSha256(canonicalRequest));

        // Console.WriteLine($"[AwsSigV4] StringToSign:");
        // Console.WriteLine(stringToSign);
        // Console.WriteLine($"[AwsSigV4] --- end string to sign ---");

        // 3. Signing Key
        var signingKey = GetSigningKey(dateStamp);

        // 4. Signature
        var signature = HmacSha256Hex(signingKey, stringToSign);

        // 5. Authorization 头
        var auth = $"{algorithm} Credential={_accessKeyId}/{credentialScope}, " +
                   $"SignedHeaders={signedHeaders}, Signature={signature}";

        request.Headers.TryAddWithoutValidation("Authorization", auth);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        // Console.WriteLine($"[AwsSigV4] Authorization: {auth}");
        // Console.WriteLine($"[AwsSigV4] x-amz-date: {amzDate}");
        // var stToken = request.Headers.TryGetValues("x-amz-security-token", out var st) ? st.FirstOrDefault() : null;
        // Console.WriteLine($"[AwsSigV4] x-amz-security-token (first 50): {stToken?[..Math.Min(50, stToken.Length)]}...");

        return await base.SendAsync(request, ct);
    }

    // ═══ Helper ═══

    private byte[] GetSigningKey(string dateStamp)
    {
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + _secretAccessKey);
        var kDate = HmacSha256(kSecret, dateStamp);
        var kRegion = HmacSha256(kDate, _region);
        var kService = HmacSha256(kRegion, _service);
        return HmacSha256(kService, "aws4_request");
    }

    private static async Task<string> ComputePayloadHash(HttpRequestMessage request)
    {
        if (request.Content is null)
            return HashSha256("");

        var body = await request.Content.ReadAsByteArrayAsync();
        // 重新填充 Content 以便后续 handler/网络层可读取
        var newContent = new ByteArrayContent(body);
        if (request.Content.Headers.ContentType is not null)
            newContent.Headers.ContentType = request.Content.Headers.ContentType;
        request.Content = newContent;

        return HashSha256(body);
    }

    private static byte[] HmacSha256(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string HmacSha256Hex(byte[] key, string data) =>
        Convert.ToHexStringLower(HmacSha256(key, data));

    private static string HashSha256(string data) =>
        HashSha256(Encoding.UTF8.GetBytes(data));

    private static string HashSha256(byte[] data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));
}
