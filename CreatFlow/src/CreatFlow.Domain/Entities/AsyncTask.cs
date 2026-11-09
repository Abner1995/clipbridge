namespace CreatFlow.Domain.Entities;

/// <summary>
/// 异步任务 —— 一次「批量操作」的聚合根（一批删除 = 一条任务）。
///
/// 为什么要有它：多账号把删除变成了 N 倍操作（8 账号 × 4 平台 = 32 条记录），
/// 前台逐条 await 就是几十秒的白屏。有了这张表，用户点完就能走，
/// 桌面端消费者慢慢跑，中途关掉 App 下次还能接着跑（服务端状态权威）。
///
/// **执行在桌面端**（要会话签名 + 要按账号限流，服务端没有签名能力），
/// 服务端只存状态、并按 <see cref="AsyncTaskItem.GroupKey"/> 判「这一组是不是全清完了」。
///
/// 对标 x_async_tasks 表。
/// </summary>
public class AsyncTask : Aggregate<AsyncTaskId>
{
    /// <summary>所属会员</summary>
    public Guid MemberId { get; private set; }

    /// <summary>任务类型（决定任务页怎么渲染、以及成功时清哪张表）</summary>
    public AsyncTaskKind Kind { get; private set; }

    /// <summary>任务页主行文案：`删除 12 个作品` / `删除合集「亲子日常」`</summary>
    public string Title { get; private set; } = default!;

    /// <summary>0=Pending 1=Running 2=Completed 3=PartialFailed 4=Failed 5=Cancelled</summary>
    public AsyncTaskStatus Status { get; private set; }

    /// <summary>子项总数（入队时定死，不再变）</summary>
    public int TotalCount { get; private set; }

    /// <summary>已进终态的条数（含成功与失败）—— 进度条用</summary>
    public int DoneCount { get; private set; }

    /// <summary>终态里失败的条数</summary>
    public int FailedCount { get; private set; }

    // ── 子实体 ──
    //
    // ⚠️ 这个导航**不被 EF 加载**（配置里 Ignore，与 PublishTask.Platforms 同款：
    // 子实体走独立的 Repository）。所以从库里读回来的聚合根，这个列表是空的 ——
    // 更新进度一律走 ApplyProgress(从子项仓储数出来之后传进来)，不要指望它。

    private readonly List<AsyncTaskItem> _items = [];
    public IReadOnlyList<AsyncTaskItem> Items => _items.AsReadOnly();

    private AsyncTask() { }

    // ═══ Factory ═══

    public static AsyncTask Create(AsyncTaskId id, Guid memberId, AsyncTaskKind kind, string title)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(title);
        if (memberId == Guid.Empty)
            throw new DomainException("MemberId cannot be empty.");

        var now = Clock.Now;
        return new AsyncTask
        {
            Id = id,
            MemberId = memberId,
            Kind = kind,
            Title = title,
            Status = AsyncTaskStatus.Pending,
            CreatedAt = now,
            LastModified = now,
        };
    }

    // ═══ Behaviors ═══

    /// <summary>挂一个子项（创建路径；子项落库由调用方负责）</summary>
    public AsyncTaskItem AddItem(
        Guid targetId,
        AsyncTaskTargetKind targetKind,
        Guid groupKey,
        string platformKey,
        Guid? platformSessionId = null,
        string? sessionLabel = null,
        string? platformObjectId = null,
        int contentType = 0)
    {
        var item = AsyncTaskItem.Create(
            Id, AsyncTaskItemId.New(), targetId, targetKind, groupKey,
            platformKey, platformSessionId, sessionLabel, platformObjectId, contentType);

        _items.Add(item);
        TotalCount = _items.Count;
        LastModified = Clock.Now;
        return item;
    }

    /// <summary>
    /// 用子项的统计结果刷新聚合根。**从库里读回来的聚合根必须走这里** ——
    /// <see cref="Items"/> 不被 EF 加载，靠它自己算不出任何东西。
    /// </summary>
    public void ApplyProgress(int doneCount, int failedCount)
    {
        DoneCount = Math.Clamp(doneCount, 0, TotalCount);
        FailedCount = Math.Clamp(failedCount, 0, TotalCount);

        // 本方法只在**有子项回报之后**被调用，所以「还没做完」一律是 Running ——
        // Pending 只由 Create 给（已开始认领的任务不该因为「还没完成的」而退回待执行）
        Status = DoneCount < TotalCount
            ? AsyncTaskStatus.Running
            : FailedCount == TotalCount && TotalCount > 0
                ? AsyncTaskStatus.Failed
                : FailedCount > 0
                    ? AsyncTaskStatus.PartialFailed
                    : AsyncTaskStatus.Completed;

        LastModified = Clock.Now;
    }

    /// <summary>这条任务是不是已经跑完了（终态）</summary>
    public bool IsSettled => Status is AsyncTaskStatus.Completed or AsyncTaskStatus.PartialFailed
        or AsyncTaskStatus.Failed or AsyncTaskStatus.Cancelled;
}

/// <summary>任务类型</summary>
public enum AsyncTaskKind
{
    /// <summary>从平台上删除已发布的视频作品（子项 = x_publish_platforms 的一行）</summary>
    VideoDelete = 1,

    /// <summary>从平台上删除合集（子项 = x_collection_platforms 的一行）</summary>
    CollectionDelete = 2,
}

/// <summary>
/// 任务整体状态 —
///   Pending       = 0 — 已入队，还没开始
///   Running       = 1 — 进行中
///   Completed     = 2 — 全部成功
///   PartialFailed = 3 — 部分成功
///   Failed        = 4 — 全部失败
///   Cancelled     = 5 — 用户取消
/// </summary>
public enum AsyncTaskStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    PartialFailed = 3,
    Failed = 4,
    Cancelled = 5,
}
