using CreatFlow.NewApp.Models;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 删除任务的**消费者** —— 把「用户点了删除」和「平台侧真的删掉了」这段拉长到后台。
///
/// 为什么要有它：多账号把删除变成 N 倍操作（8 账号 × 4 平台 = 32 条），
/// 前台逐条 await 就是几十秒的白屏。有了它，用户点完就能走，
/// 页面把行乐观摘掉、服务端存任务、这里慢慢跑。
///
/// 分工（与 <see cref="UploadQueueViewModel"/> 的消费循环同款）：
///   · **服务端状态权威**：待执行子项存在服务端，这里只轮询认领 + 回报。
///     App 关掉再开，没跑完的接着跑（认领不改状态，所以崩溃也自愈）。
///   · **本地只管瞬时状态**：同账号冷却、组内节流这些服务端不知道的事。
///   · 生命周期 = 应用生命周期（DI 单例，壳一建就 Start）。
///
/// ⚠️ 与队列页同一道闸：<see cref="Start"/> 里 `if (App.SmokeMode) return;` ——
/// 冒烟巡检会真建壳，不挡的话它会真去删用户的作品。
/// </summary>
public sealed class DeleteTaskConsumer
{
    private readonly CollectionSyncService _collectionSync = new();

    private readonly Dictionary<string, DateTime> _lastRunAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _gapSeconds = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _pollTimer;
    private int _isRunning;      // 闸门：同一时刻只跑一轮
    private bool _started;

    /// <summary>同账号两条之间的冷却（秒）。老版上传实测结论，删除沿用；**待重新标定**</summary>
    private const int DefaultGapSeconds = 60;

    /// <summary>同一平台同时跑几条（不同账号并行；同平台全放出去容易被平台掐）</summary>
    private const int MaxPerPlatform = 2;

    /// <summary>认领轮询间隔</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 统计刷新的节拍：每 6 轮（约 30 秒）拉一次列表。
    /// 认领只看得到「待执行」，算不出失败数（失败的是终态、不再被认领）——
    /// 而「有东西没删掉」恰恰是用户最需要被提醒的事，所以另开这条慢路径。
    /// </summary>
    private const int StatsEveryNTicks = 6;

    private int _tickCount;

    /// <summary>未完成/失败计数变化时通知壳刷新徽章（与 UploadQueueViewModel 同款：字段不是 event）</summary>
    public Action? OnStatsChanged;

    /// <summary>侧栏徽章：未完成任务数 + 失败条数（0 就不显示）</summary>
    public int PendingBadge { get; private set; }

    // ═══════════════════════════════════════════════════════════════
    //  生命周期（App 层调用，与 UploadQueueViewModel 同一节奏）
    // ═══════════════════════════════════════════════════════════════

    public void Start()
    {
        if (_started) return;

        // ⚠️ 别删这道闸：冒烟巡检会真建壳、真起消费循环，
        // 库里但凡有一条待删子项，它就会真去删用户平台上的作品。
        if (App.SmokeMode) return;

        _started = true;
        _pollTimer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(2), PollInterval);
        Log.Information("[DeleteTask] 消费者已启动（轮询 {Interval}s）", PollInterval.TotalSeconds);
    }

    public void Stop()
    {
        _started = false;
        _pollTimer?.Dispose();
        _pollTimer = null;
        // 单例跨会员复用：不清冷却表的话，换个人登录会继承上一个人的冷却时刻
        _lastRunAt.Clear();
        _gapSeconds.Clear();
        PendingBadge = 0;
        OnStatsChanged?.Invoke();
    }

    // ═══════════════════════════════════════════════════════════════
    //  一轮
    // ═══════════════════════════════════════════════════════════════

    private async Task TickAsync()
    {
        // 闸门：上一轮还没跑完就直接跳过（网络慢时轮询会叠起来）
        if (Interlocked.Exchange(ref _isRunning, 1) == 1) return;
        try
        {
            var pending = await AsyncTaskApi.ClaimAsync();

            // 失败数只有列表接口看得到（认领只看得到待执行），所以走慢节拍
            if (++_tickCount % StatsEveryNTicks == 0) await RefreshStatsAsync();

            if (pending.Count == 0) return;

            // 每个 (平台,会话) 只跑队首一条：同账号并发删会被平台掐（老版上传实测 403）
            var batch = PickReady(pending);
            if (batch.Count == 0) return;   // 全在冷却期里

            await RunBatchAsync(batch);
            await RefreshStatsAsync();      // 刚跑完，徽章当场跟着变
        }
        catch (Exception ex)
        {
            // 消费循环不能因为一轮失败就退出 —— 下一轮接着来
            Log.Warning(ex, "[DeleteTask] 轮询异常，稍后重试");
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    /// <summary>拉一次统计（未完成任务 + 失败条数）</summary>
    private async Task RefreshStatsAsync()
    {
        var list = await AsyncTaskApi.ListAsync(page: 1, pageSize: 1);
        if (list is null) return;

        var n = list.UnsettledCount + list.FailedCount;
        if (n == PendingBadge) return;
        PendingBadge = n;
        OnStatsChanged?.Invoke();
    }

    /// <summary>
    /// 每组取队首一条，跳过冷却期内的组。
    /// 分组键与视频页批量删除共用 <see cref="VideoArchiveFormat.GroupKey"/>（同账号跨平台是不同组）。
    /// </summary>
    private List<ServerAsyncTaskItem> PickReady(List<ServerAsyncTaskItem> pending)
    {
        var now = DateTime.Now;
        var ready = new List<ServerAsyncTaskItem>();

        foreach (var group in pending.GroupBy(i =>
                     VideoArchiveFormat.GroupKey(i.PlatformKey, i.PlatformSessionId)))
        {
            var head = group.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).First();

            if (_lastRunAt.TryGetValue(group.Key, out var last))
            {
                var gap = _gapSeconds.GetValueOrDefault(group.Key, DefaultGapSeconds);
                if (last.AddSeconds(gap) > now) continue;   // 冷却中
            }
            ready.Add(head);
        }

        // 同一平台最多放 MaxPerPlatform 条 —— 分组已经保证组内串行，这里再压一层平台级并发
        return ready
            .GroupBy(i => i.PlatformKey, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Take(MaxPerPlatform))
            .ToList();
    }

    private async Task RunBatchAsync(List<ServerAsyncTaskItem> batch)
    {
        foreach (var item in batch)
        {
            var key = VideoArchiveFormat.GroupKey(item.PlatformKey, item.PlatformSessionId);
            var outcome = await ExecuteAsync(item);

            // ⚠️ **只有真的碰过平台的才吃同账号冷却**。
            //
            // 冷却的本意是「别拿同一个 cookie 连着打平台」（老版上传实测 403）。
            // 而 UnboundCleaned（账号已解绑 / 没作品 ID）**根本没调平台** ——
            // 让它白等 60 秒只会把「一批 3 条脏记录」拖成 2 分钟以上，
            // 用户看到的就是「任务卡在 7/10 永远不动」（2026-09-21 实测：
            // 三条同 session 的失效记录，每条都排 60 秒队，App 开 6 秒当然跑不完）。
            if (outcome == TaskItemStatus.UnboundCleaned)
                _lastRunAt.Remove(key);      // 没碰平台 → 不设冷却，下一条下一轮就能上
            else
                _lastRunAt[key] = DateTime.Now;
        }
        OnStatsChanged?.Invoke();
    }

    /// <summary>
    /// 跑一条：平台侧删除 → 回报服务端。返回最终状态（调用方据此决定要不要记冷却）。
    ///
    /// **平台侧结果无论如何都要回给服务端** —— 回报失败只是让这条重新排上队，
    /// 漏回报则会让它永远停在 Pending（用户那边看不出任何异常，行却一直藏着）。
    /// </summary>
    private async Task<TaskItemStatus> ExecuteAsync(ServerAsyncTaskItem item)
    {
        TaskItemStatus status;
        string? error = null;

        try
        {
            if (item.TargetKind == AsyncTaskFormat.TargetCollectionPlatform)
            {
                // 没有平台侧合集 ID（从没在平台上建过）、或没有会话（账号已解绑 / 老数据行）
                // → 平台侧无从下手，按已清处理。
                // ⚠️ 会话这道守卫视频分支一直有（见 DeletePublishRecordAsync），合集分支此前
                // 只查了平台侧 ID —— 空 Guid 直送凭据接口，服务端 PlatformSessionId.Of 当场抛
                // DomainException（2026-10-14 用户实测：合集「全选删除」整批卡在失败）。
                if (!AsyncTaskFormat.CanDeleteOnPlatform(item.PlatformSessionId, item.PlatformObjectId))
                {
                    status = TaskItemStatus.UnboundCleaned;
                }
                else
                {
                    var report = await _collectionSync.DeleteOnPlatformAsync(
                        item.PlatformKey, item.PlatformSessionId!.Value,
                        item.PlatformObjectId!, item.PlatformDisplayName, item.SessionLabel ?? "",
                        isArticle: item.ContentType == (int)CollectionContentType.Article);
                    status = AsyncTaskFormat.StatusFor(report.Outcome);
                    error = report.Progress.ErrorMsg;
                }
            }
            else
            {
                status = await DeletePublishRecordAsync(item);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DeleteTask] 执行异常 itemId={Id} platform={Platform}",
                item.Id, item.PlatformKey);
            // 保守归 PlatformFailed：记录保留、可重试。宁可让用户看见失败，也别假装删掉了
            status = TaskItemStatus.PlatformFailed;
            error = ex.Message;
        }

        try
        {
            await AsyncTaskApi.ReportAsync(item.Id, status, error);
            if (TaskItemStatus.NeedLogin == status)
                Log.Information("[DeleteTask] {Platform} 登录过期，记录已保留待重试", item.PlatformKey);
        }
        catch (Exception ex)
        {
            // 回报失败：服务端那边这条还是 Pending → 下一轮会重新认领（重复执行一次，
            // 而平台侧删除本来就幂等，重删拿到「已不存在」）。比丢在中间态强。
            Log.Warning(ex, "[DeleteTask] 回报失败 itemId={Id}，下轮重试", item.Id);
        }

        return status;
    }

    /// <summary>
    /// 视频作品：会话解绑 / 没作品 ID / **平台没接删除链路**（B站要人机验证）
    /// 一律按「平台侧无从下手」算已清（与视频页同口径）。
    /// </summary>
    private static async Task<TaskItemStatus> DeletePublishRecordAsync(ServerAsyncTaskItem item)
    {
        if (!PlatformNames.SupportsVideoDeletion(item.PlatformKey)
            || !AsyncTaskFormat.CanDeleteOnPlatform(item.PlatformSessionId, item.PlatformObjectId))
            return TaskItemStatus.UnboundCleaned;

        var (coverKey, title) = await KuaishouDeleteHintAsync(item);
        var result = await VideoArchiveDeleteService.DeleteOnPlatformAsync(
            item.PlatformKey, item.PlatformSessionId!.Value, item.PlatformObjectId!,
            coverKey, title);
        return AsyncTaskFormat.StatusFor(result);
    }

    /// <summary>
    /// 取这条记录的快手删除提示（封面 Key + 标题；只有快手需要）。
    ///
    /// 为什么任务快照里没有：`platform_object_id` 只存了作品 ID，而早期发布时列表回查失败
    /// 存下的是 **mediaId**（26 位），两条删除端点都不认 —— 只能拿封面 Key 去作品列表
    /// 里按 `unPublishCoverKey` 精确匹配（标题只用来缩小 keyword，账号作品多时别漏）。
    /// 原记录还没删（删除成功才清），所以按 group_key（= 发布任务 ID）回查一次就能拿到。
    ///
    /// 查不到 / 非快手 / 接口失败一律返回 (null, null)，走删除主路径 ——
    /// 不能让附带查询把删除判死。
    /// </summary>
    private static async Task<(string? CoverKey, string? Title)> KuaishouDeleteHintAsync(
        ServerAsyncTaskItem item)
    {
        if (!string.Equals(item.PlatformKey, PlatformNames.Kuaishou, StringComparison.OrdinalIgnoreCase))
            return (null, null);

        try
        {
            var copies = await VideoArchiveApi.ByTaskAsync(item.GroupKey);
            var row = copies.FirstOrDefault(c => c.PlatformId == item.TargetId);
            return (VideoArchiveFormat.KuaishouCoverKey(row?.ExtraDataJson),
                    row?.Title ?? row?.CommonTitle);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DeleteTask] 取快手删除提示失败 targetId={TargetId}", item.TargetId);
            return (null, null);
        }
    }
}
