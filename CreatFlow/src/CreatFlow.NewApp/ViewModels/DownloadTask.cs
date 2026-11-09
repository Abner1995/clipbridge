using CommunityToolkit.Mvvm.ComponentModel;
using CreatFlow.Platforms.Download;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 下载任务行（UI 观察对象）。持久化契约走 <see cref="ToData"/> / <see cref="FromData"/>，
/// 派生显示（状态文案 / 进度文案 / 按钮可见性）全部在 UI 侧算。
/// </summary>
public sealed partial class DownloadTaskRow : ObservableObject
{
    // ── 不可变身份 ──
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Url { get; init; } = "";
    public string Platform { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    // ── 解析结果 ──
    [ObservableProperty] private string _videoId = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _author;
    [ObservableProperty] private int _durationSeconds;
    [ObservableProperty] private long? _estimatedBytes;
    [ObservableProperty] private List<MediaFormat> _formats = [];
    [ObservableProperty] private DateTime _extractedAt;
    [ObservableProperty] private FormatStrategy _strategy = FormatStrategy.BestMerged;

    // ── 运行状态 ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsRunning), nameof(CanCancel),
        nameof(CanRetry), nameof(CanRemove), nameof(HasOutput), nameof(IsIndeterminate))]
    private DownloadTaskStatus _status = DownloadTaskStatus.Ready;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _statusMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _progressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private long _receivedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private long? _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private string _speedText = "";

    [ObservableProperty] private int _retryCount;
    [ObservableProperty] private string? _errorKind;

    // HasOutput 必须跟着 OutputPath 一起播报：完成时是**先置 Done 再写 OutputPath**
    // （DownloadQueueViewModel 的收尾顺序），只挂 Status 的话「打开位置/播放」永远不出现
    //（2026-10-23 用户实测：已完成的行右边是空的）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string? _outputPath;
    [ObservableProperty] private DateTime? _finishedAt;

    /// <summary>退避到期时间（内存态，不落盘）：到期前 <see cref="DownloadQueueViewModel"/> 不领取。</summary>
    public DateTime? RetryAfter { get; set; }

    // ── 选择（批量操作） ──
    [ObservableProperty] private bool _isSelected;

    // ═══ 派生显示 ═══

    public string PlatformName => PlatformNames.GetDisplayName(Platform);

    public string StatusText => Status switch
    {
        DownloadTaskStatus.Ready => "待下载",
        DownloadTaskStatus.Queued => "排队中",
        DownloadTaskStatus.Downloading => "下载中",
        DownloadTaskStatus.Merging => "合并中",
        DownloadTaskStatus.Done => "已完成",
        DownloadTaskStatus.Failed => "失败",
        DownloadTaskStatus.Cancelled => "已取消",
        DownloadTaskStatus.Interrupted => "已中断",
        _ => "未知",
    };

    public bool IsRunning => Status is DownloadTaskStatus.Downloading or DownloadTaskStatus.Merging;
    public bool CanCancel => IsRunning || Status == DownloadTaskStatus.Queued;
    public bool CanRetry => Status is DownloadTaskStatus.Failed or DownloadTaskStatus.Cancelled
        or DownloadTaskStatus.Interrupted;
    public bool CanRemove => !IsRunning;
    public bool HasOutput => Status == DownloadTaskStatus.Done && !string.IsNullOrWhiteSpace(OutputPath);
    public bool HasMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool IsIndeterminate => Status == DownloadTaskStatus.Merging
        || (IsRunning && TotalBytes is null);

    public string ProgressText
    {
        get
        {
            var parts = new List<string>();
            if (TotalBytes is > 0)
                parts.Add($"{FormatBytes(ReceivedBytes)} / {FormatBytes(TotalBytes.Value)}");
            else if (ReceivedBytes > 0)
                parts.Add(FormatBytes(ReceivedBytes));
            if (!string.IsNullOrWhiteSpace(SpeedText)) parts.Add(SpeedText);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>副行：平台 · 作者 · 时长</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string> { PlatformName };
            if (!string.IsNullOrWhiteSpace(Author)) parts.Add(Author!);
            if (DurationSeconds > 0) parts.Add(FormatDuration(DurationSeconds));
            return string.Join(" · ", parts);
        }
    }

    // ═══ 状态更新 ═══

    public void ApplyProgress(DownloadProgress p)
    {
        ReceivedBytes = p.BytesReceived;
        TotalBytes = p.TotalBytes;
        ProgressPercent = (int)Math.Round(p.Percent ?? 0);
        SpeedText = p.SpeedBytesPerSec > 0 ? $"{FormatBytes((long)p.SpeedBytesPerSec)}/s" : "";
        StatusMessage = p.Stage;
        if (Status is DownloadTaskStatus.Downloading or DownloadTaskStatus.Queued)
            Status = p.Stage == "合并中" ? DownloadTaskStatus.Merging : DownloadTaskStatus.Downloading;
    }

    /// <summary>解析结果回填（首次解析 / 地址过期后重新解析都走这里）。</summary>
    public void ApplyInfo(VideoInfo info)
    {
        VideoId = info.VideoId;
        Title = string.IsNullOrWhiteSpace(info.Title) ? info.VideoId : info.Title;
        Author = info.Author;
        DurationSeconds = info.DurationSeconds;
        Formats = [.. info.Formats];
        ExtractedAt = DateTime.UtcNow;
        EstimatedBytes = LargestSize(info.Formats);
    }

    private static long? LargestSize(IReadOnlyList<MediaFormat> formats)
    {
        long? best = null;
        foreach (var f in formats)
            if (f.SizeBytes is > 0 && (best is null || f.SizeBytes > best)) best = f.SizeBytes;
        return best;
    }

    public VideoInfo ToVideoInfo() => new()
    {
        Platform = Platform,
        VideoId = VideoId,
        SourceUrl = Url,
        Title = Title,
        Author = Author,
        DurationSeconds = DurationSeconds,
        Formats = Formats,
    };

    // ═══ 持久化映射 ═══

    public DownloadTaskData ToData() => new()
    {
        Id = Id,
        Url = Url,
        Platform = Platform,
        VideoId = VideoId,
        Title = Title,
        Author = Author,
        DurationSeconds = DurationSeconds,
        EstimatedBytes = EstimatedBytes,
        Formats = [.. Formats],
        ExtractedAt = ExtractedAt,
        Strategy = Strategy,
        Status = Status,
        StatusMessage = StatusMessage,
        ProgressPercent = ProgressPercent,
        ReceivedBytes = ReceivedBytes,
        TotalBytes = TotalBytes,
        RetryCount = RetryCount,
        ErrorKind = ErrorKind,
        OutputPath = OutputPath,
        CreatedAt = CreatedAt,
        FinishedAt = FinishedAt,
    };

    public static DownloadTaskRow FromData(DownloadTaskData d) => new()
    {
        Id = d.Id,
        Url = d.Url,
        Platform = d.Platform,
        CreatedAt = d.CreatedAt,
        VideoId = d.VideoId,
        Title = d.Title,
        Author = d.Author,
        DurationSeconds = d.DurationSeconds,
        EstimatedBytes = d.EstimatedBytes,
        Formats = [.. d.Formats],
        ExtractedAt = d.ExtractedAt,
        Strategy = d.Strategy,
        Status = d.Status,
        StatusMessage = d.StatusMessage,
        ProgressPercent = d.ProgressPercent,
        ReceivedBytes = d.ReceivedBytes,
        TotalBytes = d.TotalBytes,
        RetryCount = d.RetryCount,
        ErrorKind = d.ErrorKind,
        OutputPath = d.OutputPath,
        FinishedAt = d.FinishedAt,
    };

    public static DownloadTaskRow FromInfo(VideoInfo info, FormatStrategy strategy) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Url = info.SourceUrl,
        Platform = info.Platform,
        VideoId = info.VideoId,
        Title = string.IsNullOrWhiteSpace(info.Title) ? info.VideoId : info.Title,
        Author = info.Author,
        DurationSeconds = info.DurationSeconds,
        EstimatedBytes = LargestSize(info.Formats),
        Formats = [.. info.Formats],
        ExtractedAt = DateTime.UtcNow,
        Strategy = strategy,
        Status = DownloadTaskStatus.Ready,
    };

    // ═══ 格式化 ═══

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.##}GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.#}MB",
        >= 1024 => $"{bytes / 1024.0:0.#}KB",
        _ => $"{bytes}B",
    };

    private static string FormatDuration(int seconds) =>
        seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
        : $"{seconds / 60}:{seconds % 60:00}";
}
