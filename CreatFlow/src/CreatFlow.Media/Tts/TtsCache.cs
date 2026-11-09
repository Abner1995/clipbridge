using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace CreatFlow.Media.Tts;

/// <summary>
/// TTS 结果缓存 — 按（引擎+文本+音色+语速+音调）哈希缓存 mp3 + 字幕。
/// 目的：同一文案重复合成/改字幕重合成时命中缓存，减少 Edge 免费端点调用次数（降限流风险）。
/// 位置：%LocalAppData%/CreatFlow/tts_cache/；上限 500 条，超出删最旧。
/// </summary>
public static class TtsCache
{
    private const int MaxEntries = 500;

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CreatFlow", "tts_cache");

    private sealed class Meta
    {
        [JsonPropertyName("subtitles")] public List<SubtitleItem> Subtitles { get; set; } = [];
    }

    public static string? Get(ITtsProvider provider, TtsRequest request)
    {
        var key = Key(provider, request);
        var mp3 = Path.Combine(CacheDir, key + ".mp3");
        if (!File.Exists(mp3)) return null;
        // 缓存命中：读 meta（字幕）文件可能缺失（旧缓存/清理中断）→ 视为未命中
        var metaFile = Path.Combine(CacheDir, key + ".json");
        return File.Exists(metaFile) ? mp3 : null;
    }

    /// <summary>读取缓存的完整结果（Get 命中后调用）</summary>
    public static TtsResult Load(string mp3Path)
    {
        var key = Path.GetFileNameWithoutExtension(mp3Path);
        var metaFile = Path.Combine(CacheDir, key + ".json");
        var subtitles = new List<SubtitleItem>();
        try
        {
            if (File.Exists(metaFile))
                subtitles = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaFile))?.Subtitles ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning("[TtsCache] 字幕读取失败（降级为空）: {Msg}", ex.Message);
        }
        return new TtsResult(File.ReadAllBytes(mp3Path), subtitles);
    }

    public static void Put(ITtsProvider provider, TtsRequest request, TtsResult result)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var key = Key(provider, request);
            File.WriteAllBytes(Path.Combine(CacheDir, key + ".mp3"), result.Audio);
            File.WriteAllText(Path.Combine(CacheDir, key + ".json"),
                JsonSerializer.Serialize(new Meta { Subtitles = result.Subtitles.ToList() }));
            Trim();
        }
        catch (Exception ex)
        {
            Log.Warning("[TtsCache] 写入失败: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// 合成管线版本：文本清洗 / 规则 FST / 切句合成等任何会改变同一段文本产出的变更都要 bump ——
    /// 改版本号即整体失效旧缓存（无需用户清目录；旧文件由 LRU 自然淘汰）。
    /// v2 = 2026-10-10 中文数字/单位修复（旧管线阿拉伯数字与「m2」会被静默丢掉）。
    /// v3 = 2026-10-11 Kokoro 逐句合成 + 字幕按句滚动（旧缓存里是「整段一条」字幕，
    /// 不 bump 的话重合成同文案仍命中旧结果，修复同样"看不见"）。
    /// </summary>
    private const string PipelineVersion = "v3";

    private static string Key(ITtsProvider provider, TtsRequest request)
    {
        // subs=… 进 key（2026-10-11）：带字幕（合成烧录）与不带字幕（试听 / 配音队列）的结果不能互相顶替
        // —— 否则先试听一次（空字幕）入库，之后合成同文案会取出**没有字幕**的缓存。
        var raw = $"{PipelineVersion}|{provider.Name}|{request.Text}|{request.Voice}|{request.Speed:0.###}|{request.Pitch:0.###}|subs={request.IncludeSubtitles}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32].ToLowerInvariant();
    }

    /// <summary>超上限删最旧（按修改时间）</summary>
    private static void Trim()
    {
        try
        {
            var files = Directory.GetFiles(CacheDir, "*.mp3")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .ToList();
            foreach (var f in files.Skip(MaxEntries))
            {
                File.Delete(f);
                var json = Path.ChangeExtension(f, ".json");
                if (File.Exists(json)) File.Delete(json);
            }
        }
        catch { /* 清理失败不影响 */ }
    }
}
