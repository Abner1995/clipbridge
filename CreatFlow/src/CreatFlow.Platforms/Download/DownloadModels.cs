namespace CreatFlow.Platforms.Download;

/// <summary>
/// 下载引擎的错误分类 —— 队列重试策略按它分流（网络可重试、404/私密不重试、403 走重新解析）。
/// 分类口径来自 docs/视频下载引擎.md 4.6，改枚举项要同步队列页文案映射。
/// </summary>
public enum DownloadErrorKind
{
    Unknown,
    Unsupported,     // 链接暂不支持（无 extractor）
    NotFound,        // 作品不存在 / 已删除
    Private,         // 私密 / 仅作者可见
    GeoBlocked,      // 地区限制
    NeedsLogin,      // 需要登录态（缺 Cookie 或已失效）
    RiskControl,     // 风控 / 签名失效（可换时机重试）
    DrmProtected,    // DRM / 加密流（不重试）
    UrlExpired,      // 播放地址过期（必须重新解析，不能原样重试）
    Network,         // 超时 / 连接失败 / 5xx
    Http,            // 其它 HTTP 错误
    DiskFull,
    AccessDenied,
    Cancelled,
}

/// <summary>下载引擎的统一异常。调用方按 <see cref="Kind"/> 决定重试、重新解析或直接失败。</summary>
public sealed class DownloadException : Exception
{
    public DownloadException(DownloadErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    public DownloadErrorKind Kind { get; }

    /// <summary>可原样重试（网络抖动 / 风控退避）；UrlExpired 不算 —— 它必须先重新解析。</summary>
    public bool IsRetryable => Kind is DownloadErrorKind.Network or DownloadErrorKind.Http
        or DownloadErrorKind.RiskControl;

    /// <summary>需要重新解析拿新地址（播放地址带签名/时效）。</summary>
    public bool NeedsReExtract => Kind is DownloadErrorKind.UrlExpired;
}

/// <summary>流类型 —— 决定用哪个下载器。</summary>
public enum StreamKind
{
    Direct,
    Hls,
    Dash,
}

/// <summary>
/// 一条可下载的媒体流。**带请求头**：防盗链要求 UA/Referer/Cookie 与解析时一致，
/// 由 extractor 原样填进来，下载器不得自行猜测。
/// </summary>
public sealed record MediaFormat
{
    public required string Url { get; init; }
    public StreamKind Kind { get; init; } = StreamKind.Direct;
    public string Container { get; init; } = "mp4";
    public string FormatId { get; init; } = "";

    public int Width { get; init; }
    public int Height { get; init; }
    public long? SizeBytes { get; init; }

    /// <summary>该流是否带音频轨（决定要不要再下一个音频再合并）</summary>
    public bool HasAudio { get; init; } = true;

    public bool IsAudioOnly { get; init; }
    public bool HasDrm { get; init; }

    /// <summary>播放地址过期时间（平台签名 URL；未知为 null）</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    // ── 下载时必须原样带上的头 ──
    public string? UserAgent { get; init; }
    public string? Referer { get; init; }
    public string? Cookie { get; init; }

    /// <summary>分辨率文案（"1080p" / "720p" / "audio"）—— 路径模板与 UI 用</summary>
    public string ResolutionText => IsAudioOnly ? "audio"
        : Height > 0 ? $"{Height}p"
        : Width > 0 ? $"{Width}w"
        : "unknown";

    public override string ToString() =>
        $"{FormatId}({ResolutionText}, {Kind}, {(SizeBytes is > 0 ? $"{SizeBytes / 1024 / 1024}MB" : "size?")})";
}

/// <summary>解析结果（元数据 + 候选流）。解析阶段不产生任何字节。</summary>
public sealed record VideoInfo
{
    public required string Platform { get; init; }
    public required string VideoId { get; init; }
    public required string SourceUrl { get; init; }

    public string Title { get; init; } = "";
    public string? Author { get; init; }
    public string? CoverUrl { get; init; }
    public int DurationSeconds { get; init; }

    /// <summary>图文作品（抖音 aweme_type=68 / 小红书图集）—— P0 只取视频，图文标 false 走不支持提示</summary>
    public bool IsImagePost { get; init; }

    public IReadOnlyList<MediaFormat> Formats { get; init; } = [];

    public override string ToString() => $"{Platform}/{VideoId} {Title}";
}

/// <summary>解析上下文 —— 登录态与本机指纹。缺少 Cookie 时仍尝试游客可访问的路径。</summary>
public sealed record ExtractContext
{
    public string? Cookie { get; init; }
    public string? UserAgent { get; init; }
    public string? Referer { get; init; }
    public PlatformEnvironmentSnapshot? Environment { get; init; }

    /// <summary>
    /// 浏览器兜底（抖音必需）：在**已登录的 WebView2 账号页**里打开作品页并抓取数据。
    /// 抖音 `aweme/detail` 受 Argus + uifid + secsdk 签名保护，纯 HTTP 拿不到（2026-10-21 实测
    /// `403 Blocked by ArgusSecurityPlugin Uifid Not Found`），只有页面自己的请求能过。
    /// 参数 = 作品 URL；返回 = 实现方定义的页面数据 JSON；null = 宿主不可用。
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? BrowserExtract { get; init; }
}

/// <summary>下载进度（UI 节流后消费；速度由下载器用滑动窗口算）</summary>
public sealed record DownloadProgress(
    long BytesReceived,
    long? TotalBytes,
    double SpeedBytesPerSec,
    TimeSpan? Eta,
    string Stage = "下载中")
{
    public double? Percent => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100.0 / TotalBytes.Value, 0, 100)
        : null;
}

/// <summary>下载产物</summary>
public sealed record DownloadOutcome(
    string FilePath,
    long FileSize,
    string Sha256,
    TimeSpan Elapsed);

/// <summary>格式选择策略 —— 不做 yt-dlp 的 DSL，够用即可</summary>
public enum FormatStrategy
{
    /// <summary>优先音视频合一（最快最稳），没有才合并</summary>
    BestMerged,
    /// <summary>视频 / 音频分别取最高再合并</summary>
    BestQuality,
    /// <summary>不高于指定分辨率</summary>
    MaxHeight,
    /// <summary>只要音频</summary>
    AudioOnly,
    /// <summary>体积最小</summary>
    Smallest,
}

/// <summary>选流结果：视频 + 可选音频（分离流才需要合并）</summary>
public sealed record FormatSelection(MediaFormat Video, MediaFormat? Audio)
{
    public bool NeedsMerge => Audio is not null;
}
