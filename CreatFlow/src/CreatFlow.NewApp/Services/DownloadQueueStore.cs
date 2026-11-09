using CreatFlow.Platforms.Download;

namespace CreatFlow.NewApp.Services;

/// <summary>下载任务状态（字符串枚举落盘；改枚举项不会错位）</summary>
public enum DownloadTaskStatus
{
    Ready,        // 已解析，待用户确认（预览态）
    Queued,       // 已入队，等待下载
    Downloading,  // 下载中
    Merging,      // 合并中（分离流）
    Done,         // 已完成
    Failed,       // 失败（原因在 StatusMessage）
    Cancelled,    // 已取消（可重试）
    Interrupted,  // 上次运行中断（进程退出时还在跑；可重试）
}

/// <summary>一条下载任务的存档单元 —— 只存输入快照与结果定位，派生显示走 UI 侧。</summary>
public sealed record DownloadTaskData
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public string Platform { get; init; } = "";
    public string VideoId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Author { get; init; }
    public string? CoverUrl { get; init; }
    public int DurationSeconds { get; init; }
    public long? EstimatedBytes { get; init; }

    /// <summary>解析时的候选流快照（可能带时效签名；下载前会按 TTL 重新解析）</summary>
    public List<MediaFormat> Formats { get; init; } = [];
    public DateTime ExtractedAt { get; init; }

    public FormatStrategy Strategy { get; init; } = FormatStrategy.BestMerged;
    public DownloadTaskStatus Status { get; init; } = DownloadTaskStatus.Ready;
    public string StatusMessage { get; init; } = "";
    public int ProgressPercent { get; init; }
    public long ReceivedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public int RetryCount { get; init; }
    public string? ErrorKind { get; init; }
    public string? OutputPath { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
}

/// <summary>下载队列的整份存档（暂停态与路径设置也住这里 —— 会员级）。</summary>
public sealed record DownloadQueueState
{
    public bool IsPaused { get; init; }
    public string RootDirectory { get; init; } = "";
    public string PathTemplate { get; init; } = OutputPathResolver.DefaultTemplate;
    public FormatStrategy Strategy { get; init; } = FormatStrategy.BestMerged;
    public List<DownloadTaskData> Tasks { get; init; } = [];
}

/// <summary>
/// 下载队列持久化 —— accounts/{memberId:N}/download_queue.json（本机任务，不经服务端）。
/// 与合成 / 配音队列同构：任务属会员（快照里带平台账号 Cookie 的解析结果），
/// 机器级共享会让另一会员看到 / 跑这份队列。冒烟模式下 App 层换 TEMP 根。
/// </summary>
public sealed class DownloadQueueStore
{
    private const string FileName = "download_queue.json";

    private readonly LocalJsonFile _file;
    private readonly Func<Guid> _memberId;

    public DownloadQueueStore(string? root = null, Func<Guid>? memberId = null)
    {
        _file = new LocalJsonFile(root ?? LocalJsonFile.DefaultRoot, "DownloadQueue");
        _memberId = memberId ?? (() => Guid.Empty);
    }

    public Guid CurrentMemberId => _memberId();

    public string GetConfigPath() => MemberPath(CurrentMemberId);
    public string GetLegacyPath() => _file.PathOf(FileName);

    private string MemberPath(Guid memberId) => _file.PathOf(LocalJsonFile.MemberRelative(memberId, FileName));

    public async Task<DownloadQueueState> LoadAsync(Guid? memberId = null, CancellationToken ct = default)
    {
        var member = memberId ?? _memberId();
        return await _file.ReadMemberOrAdoptAsync<DownloadQueueState>(
            MemberPath(member), GetLegacyPath(), member,
            state => state.Tasks.Count > 0 || state.IsPaused, "DownloadQueue", ct)
            .ConfigureAwait(false) ?? new DownloadQueueState();
    }

    public async Task SaveAsync(DownloadQueueState state, Guid? memberId = null, CancellationToken ct = default) =>
        await _file.WriteAsync(MemberPath(memberId ?? _memberId()), state, ct).ConfigureAwait(false);

    /// <summary>
    /// 重启恢复：下载中 / 合并中的标「已中断」可重试（半截文件保留，续传由 .part 决定）；
    /// 排队中的原样续跑；预览态（Ready）保持不动。
    /// </summary>
    public static DownloadQueueState ApplyRestoreRules(DownloadQueueState state) => state with
    {
        Tasks = [.. state.Tasks.Select(t => t.Status is DownloadTaskStatus.Downloading or DownloadTaskStatus.Merging
            ? t with { Status = DownloadTaskStatus.Interrupted, StatusMessage = "上次下载中断，可重试（已下载的部分会续传）" }
            : t)],
    };
}
