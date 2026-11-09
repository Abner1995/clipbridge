namespace CreatFlow.Media.Video;

/// <summary>素材类型</summary>
public enum MediaKind
{
    Image,   // 静态图（走随机动效）
    Video,   // 视频片段（cover 裁剪 + 截取，不动效；原声忽略，软字幕轨自动丢弃）
}

/// <summary>合成素材条目（图片/视频混排，列表顺序即播放顺序）</summary>
public sealed record ComposeMediaItem(string Path, MediaKind Kind)
{
    /// <summary>列表图标（老版 UI 直接绑定；里面是 emoji，NewApp 零 emoji 惯例不用它）</summary>
    public string Icon => Kind == MediaKind.Image ? "🖼️" : "🎞️";

    /// <summary>类型文本（NewApp 合成页素材列表显示用；纯文字，无 emoji）</summary>
    public string KindText => Kind == MediaKind.Image ? "图片" : "视频";
}

/// <summary>
/// 分镜（AI 分镜模式）：单镜素材 + 该镜独立配音音频 + 该镜字幕。
/// 图片镜时长 = 该句配音时长（自动伸缩）；视频镜时长 = 视频完整时长（句长超视频则定格）。
/// </summary>
public sealed record ComposeShot(ComposeMediaItem Item, string AudioPath, IReadOnlyList<SubtitleItem>? Subtitles = null);

/// <summary>视频合成请求参数</summary>
public sealed record ComposeRequest(
    IReadOnlyList<ComposeMediaItem> Items,      // ≥1，图片/视频混排（顺序即轮播顺序）
    string AudioPath,                           // 配音 mp3（整段模式）；分镜模式下为占位（实际用 Shots 逐镜音频）
    string OutputPath,                          // 输出 mp4（-y 覆盖）
    int Width = 1080,                           // 默认 9:16 竖屏
    int Height = 1920,                          // 16:9 时传 1920,1080
    IReadOnlyList<SubtitleItem>? Subtitles = null, // 可选，来自 TTS Provider 的字幕（整段模式）
    string? TitleText = null,                   // 可选标题（首 3 秒叠加，V2.2 模板库前固定默认）
    string? BgmPath = null,                     // 可选背景音乐（循环混音，自动淡出）
    double BgmVolume = 0.25,                    // BGM 音量 0~1（默认 0.25，不盖配音）
    IReadOnlyList<ComposeShot>? Shots = null,   // 分镜模式：非 null 时逐镜合成（句数=素材数）
    WatermarkSpec? Watermark = null);           // 可选水印（null / 未启用 / 文字空 = 不烧）
