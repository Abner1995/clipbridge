namespace CreatFlow.Domain.Entities;

/// <summary>
/// 一条异步任务的子项 —— **一个 视频/合集 × 平台 × 账号**。
///
/// 粒度为什么是三元组而不是「一条视频」：删除要按 (平台, 会话) 分组限流
/// （同账号并发会被平台掐），所以每一格都得能独立认领、独立回报、独立重试。
///
/// 对标 x_async_task_item 表。
/// </summary>
public class AsyncTaskItem : Aggregate<AsyncTaskItemId>
{
    /// <summary>父任务</summary>
    public AsyncTaskId TaskId { get; private set; } = default!;

    /// <summary>要删的那条本地记录 ID（x_publish_platforms.id 或 x_collection_platforms.id）</summary>
    public Guid TargetId { get; private set; }

    /// <summary>TargetId 指向哪张表（决定成功时该清哪个记录）</summary>
    public AsyncTaskTargetKind TargetKind { get; private set; }

    /// <summary>
    /// 同组键。视频删除 = x_publish_tasks.id（一个发布任务 = 一条视频 × N 账号）；
    /// 合集删除 = x_collections.id。服务端靠它判「这一组是不是全清完了」。
    /// </summary>
    public Guid GroupKey { get; private set; }

    public string PlatformKey { get; private set; } = default!;

    public Guid? PlatformSessionId { get; private set; }

    /// <summary>
    /// 账号名**快照**。x_publish_platforms 解绑即失名（没有快照列），
    /// 不快照的话任务页上只会剩一句「账号已解绑」，用户认不出是哪一条。
    /// </summary>
    public string? SessionLabel { get; private set; }

    /// <summary>
    /// 平台侧对象 ID（作品 ID / 合集 ID）的**快照**。
    /// 必须入队时取 —— 本地记录可能先被删掉，那时再去读就晚了。
    /// </summary>
    public string? PlatformObjectId { get; private set; }

    /// <summary>
    /// 合集内容类型快照（0=视频 1=文章，2026-11-05）。
    /// 头条文章合集与视频合集走两套删除接口，入队时必须快照；
    /// 视频删除子项恒为 0（不参与判定）。
    /// </summary>
    public int ContentType { get; private set; }

    /// <summary>0=Pending 1=Running 2=Deleted 3=AlreadyGone 4=UnboundCleaned 5=NeedLogin 6=PlatformFailed 7=RecordCleanupFailed</summary>
    public AsyncTaskItemStatus Status { get; private set; }

    public string? ErrorMsg { get; private set; }

    public int RetryCount { get; private set; }

    /// <summary>进终态的时刻（Pending/Running 为 null）</summary>
    public DateTime? FinishedAt { get; private set; }

    private AsyncTaskItem() { }

    // ═══ Factory ═══

    public static AsyncTaskItem Create(
        AsyncTaskId taskId,
        AsyncTaskItemId id,
        Guid targetId,
        AsyncTaskTargetKind targetKind,
        Guid groupKey,
        string platformKey,
        Guid? platformSessionId = null,
        string? sessionLabel = null,
        string? platformObjectId = null,
        int contentType = 0)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(platformKey);
        if (targetId == Guid.Empty)
            throw new DomainException("TargetId cannot be empty.");

        var now = Clock.Now;
        return new AsyncTaskItem
        {
            Id = id,
            TaskId = taskId,
            TargetId = targetId,
            TargetKind = targetKind,
            GroupKey = groupKey,
            PlatformKey = platformKey,
            PlatformSessionId = platformSessionId,
            SessionLabel = sessionLabel,
            PlatformObjectId = platformObjectId,
            ContentType = contentType,
            Status = AsyncTaskItemStatus.Pending,
            CreatedAt = now,
            LastModified = now,
        };
    }

    // ═══ Behaviors ═══

    /// <summary>回报结果（终态）。传 Pending 会被忽略（那不是结果）</summary>
    public void MarkFinished(AsyncTaskItemStatus status, string? errorMsg = null)
    {
        if (!AsyncTaskItemStatuses.IsTerminal(status)) return;

        Status = status;
        ErrorMsg = string.IsNullOrWhiteSpace(errorMsg) ? null : Truncate(errorMsg!, 500);
        FinishedAt = Clock.Now;
        LastModified = Clock.Now;
    }

    /// <summary>
    /// 回到待执行（重试失败项）。只对失败态有效 ——
    /// 成功态重试没有意义（平台侧已经删了，再删一次只会拿到「已不存在」）。
    /// </summary>
    public void ResetForRetry()
    {
        if (AsyncTaskItemStatuses.IsCleared(Status)) return;

        Status = AsyncTaskItemStatus.Pending;
        ErrorMsg = null;
        FinishedAt = null;
        RetryCount++;
        LastModified = Clock.Now;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// 子项状态。前三个是「平台侧已了结」（可以随本地记录一起消失），后三个要保留记录、可重试。
///
/// **没有 Running**：认领不改状态（见下方「为什么」），所以子项只有「待执行」和几个终态。
/// 客户端执行期间界面上的「N/M」由 DoneCount 推，不需要一条一条地问「谁在跑」。
///
/// 为什么认领不改状态：认领 = 服务端把 Pending 的挑出来给客户端。若认领即置 Running，
/// 客户端只会执行**每个账号的队首**（同账号并发会被平台掐），队列后面那些就会一直挂着
/// Running 却没人跑，得再配一个 reap 超时任务来救。不置 Running 的话，
/// 客户端崩了这些子项还是 Pending，下次轮询自然接着跑 —— **崩溃自愈，零额外机制**。
/// 代价是同一批可能被重复执行一次，而平台侧的删除本来就幂等（重删拿到「已不存在」）。
///
/// 与客户端 <c>DeleteOutcome</c> 是同一套词汇（客户端那边早就有
/// `RowRemoved`/`IsError`/`OutcomeText` 的判定，零学习成本）。
/// </summary>
public enum AsyncTaskItemStatus
{
    Pending = 0,

    /// <summary>平台侧已删除</summary>
    Deleted = 1,
    /// <summary>平台上本来就没有（用户自己删过）→ 脏记录，可以清</summary>
    AlreadyGone = 2,
    /// <summary>账号已解绑 → 平台侧无从下手（且永远下不了手），只能清本地记录</summary>
    UnboundCleaned = 3,

    /// <summary>平台登录过期 —— 记录保留，登录后可重试</summary>
    NeedLogin = 4,
    /// <summary>平台删除失败（可能已不存在）—— 记录保留，可重试</summary>
    PlatformFailed = 5,
    /// <summary>平台已删但本地记录没清掉 —— 可重试（重试会自愈）</summary>
    RecordCleanupFailed = 6,
}

/// <summary>子项状态的判定（集中一处，免得「哪些算成功」散在十几个 if 里）</summary>
public static class AsyncTaskItemStatuses
{
    /// <summary>平台侧已了结 —— 可以连本地记录一起清掉</summary>
    public static bool IsCleared(AsyncTaskItemStatus s) =>
        s is AsyncTaskItemStatus.Deleted or AsyncTaskItemStatus.AlreadyGone
          or AsyncTaskItemStatus.UnboundCleaned;

    /// <summary>终态（不会再自己动）</summary>
    public static bool IsTerminal(AsyncTaskItemStatus s) => s != AsyncTaskItemStatus.Pending;

    /// <summary>终态且失败 —— 保留记录、可重试的那些</summary>
    public static bool IsFailed(AsyncTaskItemStatus s) =>
        IsTerminal(s) && !IsCleared(s);
}

/// <summary>这个子项要清的是哪张表</summary>
public enum AsyncTaskTargetKind
{
    /// <summary>x_publish_platforms 里的一条平台发布记录</summary>
    PublishPlatform = 1,

    /// <summary>x_collection_platforms 里的一条平台合集记录</summary>
    CollectionPlatform = 2,
}
