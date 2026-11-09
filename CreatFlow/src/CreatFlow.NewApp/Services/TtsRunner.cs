namespace CreatFlow.NewApp.Services;

/// <summary>一条配音任务的执行快照（配音队列消费者只把它交给 <see cref="TtsRunner"/>）</summary>
public sealed record TtsJob
{
    public required string Text { get; init; }
    public string? ProviderName { get; init; }
    public string? VoiceId { get; init; }
    public double Speed { get; init; } = 1.0;
    public double Pitch { get; init; } = 1.0;

    /// <summary>导出目录（提交时快照）</summary>
    public required string ExportDirectory { get; init; }

    /// <summary>建议文件名、不含扩展名（提交时定死）；落盘时同目录重名自动加 -2 兜底</summary>
    public required string SuggestedName { get; init; }
}

/// <summary>
/// 配音执行器 —— 单条「文本 → TTS → mp3 文件」抽成的核心：配音页批量提交的任务由队列消费者调它。
/// 配音页单条「生成配音」**不走这里**（结果留内存里供试听 / 保存）。
/// </summary>
public static class TtsRunner
{
    /// <returns>mp3 落定路径</returns>
    public static async Task<string> RunAsync(TtsJob job, Action<string>? onProgress, CancellationToken ct)
    {
        var provider = TtsProviderFactory.Create(job.ProviderName);
        if (TtsEngines.IsKokoro(provider) && !KokoroModels.IsReady)
            throw new InvalidOperationException("Kokoro 本地模型未下载：请到「AI 设置」页下载模型后再生成");
        var voice = ResolveVoiceId(provider, job.VoiceId);

        onProgress?.Invoke("正在合成语音…");
        var result = await provider.SynthesizeAsync(
            new TtsRequest(job.Text, voice, null, job.Speed, job.Pitch, IncludeSubtitles: false),
            msg => onProgress?.Invoke(msg), ct);
        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(job.ExportDirectory);
        var path = EnsureUniquePath(Path.Combine(job.ExportDirectory, job.SuggestedName + ".mp3"));
        await File.WriteAllBytesAsync(path, result.Audio, ct);
        return path;
    }

    /// <summary>解析音色 sid（对不上就回落引擎音色表第一个）—— 与 ComposeRunner 同一口径</summary>
    private static string ResolveVoiceId(ITtsProvider provider, string? voiceId)
    {
        if (!string.IsNullOrEmpty(voiceId) && provider.Voices.Any(v => v.Id == voiceId))
            return voiceId;
        return provider.Voices.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException("没有可用的配音音色：请在「AI 设置」页检查 Kokoro 模型");
    }

    /// <summary>同目录重名就加 -2、-3 …（提交只保证批内序号唯一；跨批次撞名在这里兜底）</summary>
    internal static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name}-{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>文案摘要（配音队列的任务标题用）：折成一行取前 n 字，超长加省略号（文件名走 SuggestName 的纯截断，不带省略号）</summary>
    internal static string Snippet(string? text, int max)
    {
        var flat = ComposeUnitWriter.OneLine(text ?? "");
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>建议文件名（不含扩展名）：「配音 + 提交时间戳 + 批内序号 + 文案摘要」，全走文件名清洗</summary>
    internal static string SuggestName(string text, string stamp, int index)
    {
        var fallback = $"配音{stamp}_{index:00}";
        // 摘要直接截断、不留「…」（不复用 Snippet）：文件名尾部挂省略号像「内容缺失」，会误导 —— 2026-10-11 用户反馈
        var flat = ComposeUnitWriter.OneLine(text ?? "");
        var snippet = flat.Length <= 12 ? flat : flat[..12];
        return snippet.Length == 0 ? fallback : ComposeUnitWriter.ToSafeName($"{fallback}_{snippet}", fallback);
    }
}
