namespace CreatFlow.Platforms.Douyin;

using System.Text.Json;

/// <summary>
/// 查表法 a_bogus 生成器 — 移植自 douyin-api abogus_pure.py。
///
/// 算法（不同于 SM3+RC4）：
///   1. 时间戳 → 查映射表获取 bb 字节
///   2. bb XOR 固定 keystream → 加密
///   3. 前加 4 字节随机头
///   4. S4 Base64 编码 → 192 字符 a_bogus
///
/// 数据文件：
///   time_mapping_sample.json — 时间戳 → bb 字节映射表
///   FIXED_keystream.json — XOR keystream
/// </summary>
public static class ABogusLookup
{
    // S4 自定义 Base64 编码表（与 SM3+RC4 相同）
    private const string S4Table = "Dkdpgh2ZmsQB80/MfvV36XI1R45-WUAlEixNLwoqYTOPuzKFjJnry79HbGcaStCe";

    private static Dictionary<int, byte[]>? _timeMap;
    private static int[]? _sortedTimes;
    private static byte[]? _keystream;
    private static readonly object _lock = new();
    private static bool _loaded;

    /// <summary>是否已加载映射数据。</summary>
    public static bool IsLoaded
    {
        get { lock (_lock) return _loaded; }
    }

    /// <summary>
    /// 加载映射数据（启动时调用一次）。
    /// 从 App 运行目录读取 JSON 文件。
    /// </summary>
    public static void Load(string appDir)
    {
        lock (_lock)
        {
            if (_loaded) return;

            try
            {
                // 优先完整映射表，不存在用示例
                var mappingFile = Path.Combine(appDir, "time_mapping_full.json");
                if (!File.Exists(mappingFile))
                    mappingFile = Path.Combine(appDir, "time_mapping_sample.json");

                if (File.Exists(mappingFile))
                {
                    var mapJson = File.ReadAllText(mappingFile);
                    var rawMap = JsonSerializer.Deserialize<Dictionary<string, List<byte>>>(mapJson);
                    if (rawMap != null)
                    {
                        _timeMap = new Dictionary<int, byte[]>(rawMap.Count);
                        foreach (var kv in rawMap)
                            _timeMap[int.Parse(kv.Key)] = kv.Value.ToArray();
                        _sortedTimes = _timeMap.Keys.OrderBy(k => k).ToArray();
                    }
                }

                var ksFile = Path.Combine(appDir, "FIXED_keystream.json");
                if (File.Exists(ksFile))
                {
                    var ksJson = File.ReadAllText(ksFile);
                    using var doc = JsonDocument.Parse(ksJson);
                    if (doc.RootElement.TryGetProperty("bb_keystream", out var ks))
                    {
                        _keystream = new byte[ks.GetArrayLength()];
                        int i = 0;
                        foreach (var el in ks.EnumerateArray())
                            _keystream[i++] = el.GetByte();
                    }
                }

                _loaded = _timeMap != null && _timeMap.Count > 0 && _keystream != null;
            }
            catch { /* mapping files may not exist yet */ }
        }
    }

    /// <summary>
    /// 生成 a_bogus（查表法）。
    /// 返回 192 字符的 a_bogus，失败返回 null。
    /// </summary>
    public static string? Generate(long timestampMs)
    {
        lock (_lock)
        {
            if (!_loaded || _timeMap == null || _sortedTimes == null || _keystream == null)
                return null;
        }

        // 1. 查表获取 bb
        var bb = LookupBb((int)(timestampMs % int.MaxValue)); // 取模防止溢出
        if (bb == null) return null;

        // 2. XOR keystream
        var encrypted = new byte[bb.Length];
        for (int i = 0; i < bb.Length; i++)
            encrypted[i] = (byte)(bb[i] ^ _keystream[i % _keystream.Length]);

        // 3. 4 字节随机头
        var randomHead = new byte[4];
        Random.Shared.NextBytes(randomHead);
        var final = new byte[4 + encrypted.Length];
        Buffer.BlockCopy(randomHead, 0, final, 0, 4);
        Buffer.BlockCopy(encrypted, 0, final, 4, encrypted.Length);

        // 4. S4 编码
        return S4Encode(final);
    }

    private static byte[]? LookupBb(int timestamp)
    {
        if (_timeMap == null || _sortedTimes == null) return null;

        lock (_lock)
        {
            if (_timeMap.TryGetValue(timestamp, out var bb))
                return bb;

            // 最近邻插值
            if (timestamp < _sortedTimes[0])
                return _timeMap[_sortedTimes[0]];
            if (timestamp > _sortedTimes[^1])
                return _timeMap[_sortedTimes[^1]];

            // 二分查找
            int left = 0, right = _sortedTimes.Length - 1;
            while (right - left > 1)
            {
                int mid = (left + right) / 2;
                if (_sortedTimes[mid] < timestamp)
                    left = mid;
                else
                    right = mid;
            }

            int t1 = _sortedTimes[left], t2 = _sortedTimes[right];
            int closer = Math.Abs(timestamp - t1) < Math.Abs(timestamp - t2) ? t1 : t2;
            return _timeMap[closer];
        }
    }

    private static string S4Encode(byte[] data)
    {
        var result = new char[(data.Length * 4 + 2) / 3];
        int ri = 0;

        for (int i = 0; i < data.Length; i += 3)
        {
            int remaining = data.Length - i;
            if (remaining >= 3)
            {
                int b1 = data[i], b2 = data[i + 1], b3 = data[i + 2];
                result[ri++] = S4Table[(b1 >> 2) & 0x3f];
                result[ri++] = S4Table[((b1 << 4) | (b2 >> 4)) & 0x3f];
                result[ri++] = S4Table[((b2 << 2) | (b3 >> 6)) & 0x3f];
                result[ri++] = S4Table[b3 & 0x3f];
            }
            else if (remaining == 2)
            {
                int b1 = data[i], b2 = data[i + 1];
                result[ri++] = S4Table[(b1 >> 2) & 0x3f];
                result[ri++] = S4Table[((b1 << 4) | (b2 >> 4)) & 0x3f];
                result[ri++] = S4Table[(b2 << 2) & 0x3f];
            }
            else
            {
                int b1 = data[i];
                result[ri++] = S4Table[(b1 >> 2) & 0x3f];
                result[ri++] = S4Table[(b1 << 4) & 0x3f];
            }
        }

        return new string(result, 0, ri);
    }
}
