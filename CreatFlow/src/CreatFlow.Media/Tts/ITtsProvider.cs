namespace CreatFlow.Media.Tts;

/// <summary>一次 TTS 合成请求。Voice 为 Provider 的音色标识（如 Edge/Azure 共用 "zh-CN-XiaoxiaoNeural"）。</summary>
public sealed record TtsRequest(
    string Text,                 // 要配音的文本
    string Voice,                // 音色 shortName（SSML voice name）
    string? Style,               // 说话风格（null/空 = 常规；Edge 免费端点已禁用 express-as）
    double Speed,                // 语速倍率（1.0 = 原速）
    double Pitch,                // 音调倍率（1.0 = 原调）
    bool IncludeSubtitles = false); // true = 额外返回词级对齐字幕（合成视频烧录用）

/// <summary>合成结果。Subtitles 仅在 IncludeSubtitles 时非空。</summary>
public sealed record TtsResult(byte[] Audio, IReadOnlyList<SubtitleItem> Subtitles);

/// <summary>引擎自带音色（Id = 引擎内标识，DisplayName = 名字，IsFemale = 性别）。空列表 = 使用默认音色表。</summary>
public sealed record TtsVoiceInfo(string Id, string DisplayName, bool IsFemale = true);

/// <summary>
/// TTS Provider 抽象 — 合成页/配音页只依赖此接口，不感知具体引擎。
/// 实现：EdgeTtsProvider（逆向免费）、KokoroTtsProvider（本地离线免费）。
/// </summary>
public interface ITtsProvider
{
    /// <summary>引擎显示名（合成页下拉展示，如 "Edge 免费"）</summary>
    string Name { get; }

    /// <summary>引擎自带音色列表（空 = App 用默认音色表，如 Edge 的 26 个中文音色）</summary>
    IReadOnlyList<TtsVoiceInfo> Voices => [];

    /// <summary>引擎是否支持音调参数（默认 true；不支持时 App 禁用音调控件，如 Kokoro 本地）</summary>
    bool SupportsPitch => true;

    /// <summary>
    /// 合成语音。长文本自动切片（Provider 内部处理）。
    /// </summary>
    /// <param name="request">合成参数</param>
    /// <param name="onProgress">切片进度回调（后台线程触发，调用方负责 UI 编组）</param>
    Task<TtsResult> SynthesizeAsync(
        TtsRequest request,
        Action<string>? onProgress = null,
        CancellationToken ct = default);
}
