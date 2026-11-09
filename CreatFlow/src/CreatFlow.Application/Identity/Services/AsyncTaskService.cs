namespace CreatFlow.Application.Identity.Services;

/// <summary>
/// 异步任务服务 —— 批量删除的「存状态」这一半。
///
/// 分工（与整体架构一致）：**执行在桌面端**（要会话签名 + 要按账号限流，
/// 服务端没有签名能力），服务端只做三件事：
///   ① 入队时把客户端看得见的信息**快照**下来（账号名、平台侧 ID）——
///      本地记录随后会被删掉，事后再读就读不到了；
///   ② 客户端回报结果时清本地记录，并判「合集这一组是不是全清完了」；
///   ③ 给任务页提供列表与明细。
///
/// 认领**不改状态**：见 <see cref="AsyncTaskItemStatus"/> 的注释（改了就得配 reap 超时任务，
/// 不改则客户端崩了下次轮询自然接着跑）。
/// </summary>
public class AsyncTaskService
{
    private readonly IAsyncTaskRepository _taskRepo;
    private readonly IAsyncTaskItemRepository _itemRepo;
    private readonly PublishService _publishService;
    private readonly CollectionService _collectionService;

    /// <summary>一次认领最多给多少条 —— 32 条是常态，200 足够且不会把响应撑爆</summary>
    private const int MaxClaimBatch = 200;

    public AsyncTaskService(
        IAsyncTaskRepository taskRepo,
        IAsyncTaskItemRepository itemRepo,
        PublishService publishService,
        CollectionService collectionService)
    {
        _taskRepo = taskRepo;
        _itemRepo = itemRepo;
        _publishService = publishService;
        _collectionService = collectionService;
    }

    // ═══ 提交 ═══

    public async Task<SubmitAsyncDeleteResult> SubmitDeleteAsync(
        Guid memberId, SubmitAsyncDeleteRequest request, CancellationToken ct = default)
    {
        if (request.Targets is not { Count: > 0 })
            throw new ValidationException("至少需要指定一条待删除记录");

        var kind = Enum.IsDefined(typeof(AsyncTaskKind), request.Kind)
            ? (AsyncTaskKind)request.Kind
            : throw new ValidationException($"未知的任务类型 {request.Kind}");

        var task = AsyncTask.Create(
            AsyncTaskId.New(), memberId, kind,
            string.IsNullOrWhiteSpace(request.Title) ? "批量删除" : request.Title.Trim());

        foreach (var t in request.Targets)
        {
            task.AddItem(
                t.TargetId,
                Enum.IsDefined(typeof(AsyncTaskTargetKind), t.TargetKind)
                    ? (AsyncTaskTargetKind)t.TargetKind
                    : throw new ValidationException($"未知的目标类型 {t.TargetKind}"),
                t.GroupKey,
                t.PlatformKey,
                t.PlatformSessionId,
                Truncate(t.SessionLabel, 100),
                Truncate(t.PlatformObjectId, 500),
                t.ContentType);
        }

        await _taskRepo.AddAsync(task, ct);
        foreach (var item in task.Items)
            await _itemRepo.AddAsync(item, ct);
        await _taskRepo.SaveChangesAsync(ct);

        return new SubmitAsyncDeleteResult(task.Id.Value, task.Title, task.TotalCount);
    }

    // ═══ 查询 ═══

    public async Task<AsyncTaskListResponse> GetListAsync(
        Guid memberId, AsyncTaskListRequest request, CancellationToken ct = default)
    {
        // 越界防护（page=0 会让 Skip(-20) 抛 ArgumentException → 500）
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        AsyncTaskKind? kind = request.Kind is { } k && Enum.IsDefined(typeof(AsyncTaskKind), k)
            ? (AsyncTaskKind)k : null;

        var (items, total) = await _taskRepo.GetPagedByMemberAsync(
            memberId, page, pageSize, kind, request.OnlyUnsettled, request.OnlyFailed, ct);

        // 未完成/失败计数给侧栏徽章与「仅看失败」用（**不受当前筛选与分页影响** ——
        // 拿筛选后的结果去数，用户一点「仅看失败」就会看到「失败 3 条」变成「失败 3 条里的 3 条」）
        var (all, _) = await _taskRepo.GetPagedByMemberAsync(memberId, 1, 1000, kind, false, false, ct);

        return new AsyncTaskListResponse(
            items.Select(ToTaskDto).ToList(),
            total, page, pageSize,
            all.Count(t => !t.IsSettled),
            all.Count(t => t.FailedCount > 0));
    }

    public async Task<AsyncTaskDetailDto> GetDetailAsync(
        Guid memberId, Guid taskId, CancellationToken ct = default)
    {
        var task = await _taskRepo.GetByIdAndMemberAsync(AsyncTaskId.Of(taskId), memberId, ct)
            ?? throw new NotFoundException($"任务 {taskId} 不存在");

        var items = await _itemRepo.GetByTaskAsync(task.Id, ct);
        return new AsyncTaskDetailDto(ToTaskDto(task), items.Select(ToItemDto).ToList());
    }

    /// <summary>认领待执行子项（**不改状态**，见类注释）</summary>
    public async Task<ClaimAsyncTasksResponse> ClaimAsync(Guid memberId, CancellationToken ct = default)
    {
        var pending = await _itemRepo.GetPendingByMemberAsync(memberId, ct);
        return new ClaimAsyncTasksResponse(
            pending.Take(MaxClaimBatch).Select(ToItemDto).ToList());
    }

    /// <summary>
    /// 在飞的目标 —— 视频页靠 TargetIds 藏行，合集页靠 GroupKeys 藏卡片；
    /// 另有「已停稳但还有失败项」的合集组（卡上挂「删除未完成」警示条）。
    /// </summary>
    public async Task<InFlightTargetsResponse> GetInFlightAsync(
        Guid memberId, CancellationToken ct = default)
        => new(
            await _itemRepo.GetInFlightTargetIdsAsync(memberId, ct),
            await _itemRepo.GetInFlightCollectionGroupKeysAsync(memberId, ct),
            (await _itemRepo.GetFailedCollectionDeleteGroupsAsync(memberId, ct))
                .Select(g => new CollectionDeleteFailureDto(g.GroupKey, g.FailedCount))
                .ToList());

    // ═══ 回报 ═══

    /// <summary>
    /// 客户端报回一条结果。
    ///
    /// 顺序不可反：**先落平台侧结果、再清本地记录**。平台侧没删掉就清本地记录的话，
    /// 平台上的作品/合集再也没人管，也没有重试入口。
    ///
    /// 清本地记录失败要降级成 <see cref="AsyncTaskItemStatus.RecordCleanupFailed"/> ——
    /// 否则这条子项会停在「已删除」这个终态上，而记录其实还在，用户既看不见也重试不了
    /// （只能永久留在列表里，就是那种查不出原因的僵尸行）。
    /// </summary>
    public async Task<ReportAsyncTaskItemResult> ReportAsync(
        Guid memberId, ReportAsyncTaskItemRequest request, CancellationToken ct = default)
    {
        var item = await LoadOwnedItemAsync(memberId, request.ItemId, ct);

        // 幂等：重复回报（客户端重试、网络重发）不该报错，也不该把终态改回去
        if (AsyncTaskItemStatuses.IsTerminal(item.Status))
            return new ReportAsyncTaskItemResult(true, false, await GroupHasFailureAsync(item, ct));

        if (!Enum.IsDefined(typeof(AsyncTaskItemStatus), request.Status))
            throw new ValidationException($"未知的状态 {request.Status}");

        var status = (AsyncTaskItemStatus)request.Status;
        if (!AsyncTaskItemStatuses.IsTerminal(status))
            throw new ValidationException($"回报的状态必须是终态，收到 {status}");

        return await FinishItemAsync(memberId, item, status, request.ErrorMsg, ct);
    }

    /// <summary>
    /// 手动兜底：用户亲自确认「平台侧其实已删除」→ 把这条失败子项按「平台上已不存在」收尾。
    ///
    /// 为什么要有这个口子（2026-10-14 用户实测）：快手合集删除平台侧每次成功，
    /// 却被 SDK 误判成失败 —— 合集是「全组清完才删本地记录」，一条误判就把整张卡
    /// 永久卡在本地，用户没有任何出路（只能等发版修）。现在用户核对后可以自己收尾。
    ///
    /// **只对 <see cref="AsyncTaskItemStatus.PlatformFailed"/> 开放**：
    /// 「平台删除失败（可能已不存在）」正是「平台可能其实删掉了但被误判」的场景；
    /// 登录过期（重登后重试）与本地清理失败（重试自愈）各有正路，不开这个口子。
    /// </summary>
    public async Task<ReportAsyncTaskItemResult> ConfirmGoneAsync(
        Guid memberId, Guid itemId, CancellationToken ct = default)
    {
        var item = await LoadOwnedItemAsync(memberId, itemId, ct);

        if (item.Status != AsyncTaskItemStatus.PlatformFailed)
            throw new ValidationException("只有「平台删除失败」的子项可以确认已在平台删除");

        return await FinishItemAsync(memberId, item, AsyncTaskItemStatus.AlreadyGone, null, ct);
    }

    /// <summary>
    /// 子项落终态 → （若算已清）清本地记录 → 重算任务进度。回报与手动兜底共用，
    /// 两边对「已解绑算已清」「合集全组清完才删」这些边界的结论必须一模一样。
    /// </summary>
    private async Task<ReportAsyncTaskItemResult> FinishItemAsync(
        Guid memberId, AsyncTaskItem item, AsyncTaskItemStatus status, string? errorMsg,
        CancellationToken ct)
    {
        item.MarkFinished(status, errorMsg);
        await _itemRepo.SaveChangesAsync(ct);   // 先落库，下面读同组才是最新状态

        var localCleared = false;
        if (AsyncTaskItemStatuses.IsCleared(status))
        {
            try
            {
                localCleared = await CleanLocalAsync(memberId, item, ct);
            }
            catch (NotFoundException)
            {
                // 记录已经不在了（别的端删过 / 上一次重试清掉了）—— 当成功（服务端删除本就不幂等）
                localCleared = true;
            }
            catch (Exception ex)
            {
                item.MarkFinished(AsyncTaskItemStatus.RecordCleanupFailed, ex.Message);
                await _itemRepo.SaveChangesAsync(ct);
            }
        }

        await RefreshTaskProgressAsync(item.TaskId, ct);
        return new ReportAsyncTaskItemResult(true, localCleared, await GroupHasFailureAsync(item, ct));
    }

    /// <summary>
    /// 清掉本地记录。
    ///
    /// 视频：一子项 = 一条记录，直接删。
    /// 合集：**一子项 = 一个账号，整组全清完才删合集** —— 这就是「8 个账号全部删除了，
    /// 数据记录才会真删除」的落点。没清完就什么都不动（记录留着当重试依据）。
    /// </summary>
    private async Task<bool> CleanLocalAsync(Guid memberId, AsyncTaskItem item, CancellationToken ct)
    {
        switch (item.TargetKind)
        {
            case AsyncTaskTargetKind.PublishPlatform:
                await _publishService.DeletePlatformAsync(memberId, item.TargetId, ct);
                return true;

            case AsyncTaskTargetKind.CollectionPlatform:
            {
                var group = await _itemRepo.GetByTaskAndGroupAsync(item.TaskId, item.GroupKey, ct);
                if (group.Any(i => !AsyncTaskItemStatuses.IsCleared(i.Status)))
                    return false;   // 还有账号没删掉 → 整张卡保留

                await _collectionService.DeleteAsync(memberId, new DeleteCollectionRequest(item.GroupKey), ct);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>这一组里还有没有没删掉的（合集页据此显示「记录已保留，可重试」）</summary>
    private async Task<bool> GroupHasFailureAsync(AsyncTaskItem item, CancellationToken ct)
    {
        var group = await _itemRepo.GetByTaskAndGroupAsync(item.TaskId, item.GroupKey, ct);
        return group.Any(i => !AsyncTaskItemStatuses.IsCleared(i.Status));
    }

    // ═══ 重试 ═══

    public async Task RetryAsync(Guid memberId, RetryAsyncTaskRequest request, CancellationToken ct = default)
    {
        if (request.ItemId is { } itemId)
        {
            var item = await LoadOwnedItemAsync(memberId, itemId, ct);
            item.ResetForRetry();
            await _itemRepo.SaveChangesAsync(ct);
            await RefreshTaskProgressAsync(item.TaskId, ct);
            return;
        }

        if (request.TaskId is not { } taskId)
            throw new ValidationException("必须指定要重试的任务或子项");

        var task = await _taskRepo.GetByIdAndMemberAsync(AsyncTaskId.Of(taskId), memberId, ct)
            ?? throw new NotFoundException($"任务 {taskId} 不存在");

        var items = await _itemRepo.GetByTaskAsync(task.Id, ct);
        foreach (var item in items.Where(i => AsyncTaskItemStatuses.IsFailed(i.Status)))
            item.ResetForRetry();
        await _itemRepo.SaveChangesAsync(ct);
        await RefreshTaskProgressAsync(task.Id, ct);
    }

    // ═══ 删除任务 ═══

    /// <summary>
    /// 删掉一条任务（连同它的子项）。
    ///
    /// **只允许删已跑完的**：把还在跑的任务删掉，客户端那边认领到的子项就没了归属，
    /// 而且页面上藏起来的行再也不会有人放回来（在飞集合里凭空少一条）——
    /// 结果是「点了删除、行消失了、平台上还在」。要取消就先取消，别直接删。
    /// </summary>
    public async Task DeleteAsync(Guid memberId, Guid taskId, CancellationToken ct = default)
    {
        var task = await _taskRepo.GetByIdAndMemberAsync(AsyncTaskId.Of(taskId), memberId, ct)
            ?? throw new NotFoundException($"任务 {taskId} 不存在");

        if (!task.IsSettled)
            throw new ValidationException("任务还在进行中，不能删除 —— 请等它跑完");

        await _itemRepo.DeleteByTaskAsync(task.Id, ct);   // 先删子（应用层级联，无外键）
        await _taskRepo.DeleteAsync(task, ct);
    }

    /// <summary>
    /// 清空已完成的任务（连带子项）。**进行中的一条都不动** ——
    /// 删了的话页面上为它藏起来的行再也没人放回来。
    ///
    /// **还有失败子项的也不动**（哪怕任务已是终态）：那是用户重试/确认清理的唯一入口，
    /// 一起清掉 = 被它们卡住的本地记录（合集要全组清完才删）永远没有出口
    /// （2026-10-14 用户实测同款场景：合集 4 条里 1 条误判失败，任务被「清空已完成」扫掉
    /// 后连重试按钮都没了）。返回删掉的任务数。
    /// </summary>
    public async Task<int> ClearFinishedAsync(Guid memberId, CancellationToken ct = default)
    {
        var (all, _) = await _taskRepo.GetPagedByMemberAsync(memberId, 1, 1000, null, false, false, ct);
        var settled = all.Where(t => t.IsSettled).ToList();
        if (settled.Count == 0) return 0;

        var safe = new List<AsyncTask>();
        foreach (var task in settled)
        {
            var items = await _itemRepo.GetByTaskAsync(task.Id, ct);
            if (items.Any(i => AsyncTaskItemStatuses.IsFailed(i.Status))) continue;
            safe.Add(task);
        }
        if (safe.Count == 0) return 0;

        await _itemRepo.DeleteByTasksAsync(safe.Select(t => t.Id).ToList(), ct);   // 先删子（无外键，只能手写）
        foreach (var task in safe)
            await _taskRepo.DeleteAsync(task, ct);   // Remove + SaveChanges 一体
        return safe.Count;
    }

    // ═══ Helpers ═══

    private async Task<AsyncTaskItem> LoadOwnedItemAsync(
        Guid memberId, Guid itemId, CancellationToken ct)
    {
        var item = await _itemRepo.GetByIdAsync(AsyncTaskItemId.Of(itemId), ct)
            ?? throw new NotFoundException($"子项 {itemId} 不存在");

        // 归属校验走父任务（子项没有 member_id）—— 不校验就能改别人的任务状态
        _ = await _taskRepo.GetByIdAndMemberAsync(item.TaskId, memberId, ct)
            ?? throw new NotFoundException($"子项 {itemId} 不存在");

        return item;
    }

    /// <summary>按子项统计重算聚合根进度（<see cref="AsyncTask.Items"/> 不被 EF 加载，只能这么算）</summary>
    private async Task RefreshTaskProgressAsync(AsyncTaskId taskId, CancellationToken ct)
    {
        var task = await _taskRepo.GetByIdAsync(taskId, ct);
        if (task is null) return;

        var items = await _itemRepo.GetByTaskAsync(taskId, ct);
        task.ApplyProgress(
            items.Count(i => AsyncTaskItemStatuses.IsTerminal(i.Status)),
            items.Count(i => AsyncTaskItemStatuses.IsFailed(i.Status)));

        await _taskRepo.SaveChangesAsync(ct);
    }

    private static AsyncTaskDto ToTaskDto(AsyncTask t) => new(
        t.Id.Value, (int)t.Kind, KindText(t.Kind), t.Title,
        (int)t.Status, TaskStatusText(t.Status),
        t.TotalCount, t.DoneCount, t.FailedCount,
        t.CreatedAt ?? Clock.Now, t.LastModified);

    private static AsyncTaskItemDto ToItemDto(AsyncTaskItem i) => new(
        i.Id.Value, i.TaskId.Value, i.TargetId, (int)i.TargetKind, i.GroupKey,
        i.PlatformKey, PlatformNames.GetDisplayName(i.PlatformKey),
        i.PlatformSessionId, i.SessionLabel, i.PlatformObjectId,
        (int)i.Status, ItemStatusText(i.Status), i.ErrorMsg, i.RetryCount,
        i.FinishedAt, i.CreatedAt ?? Clock.Now, i.ContentType);

    private static string KindText(AsyncTaskKind k) => k switch
    {
        AsyncTaskKind.VideoDelete => "删除视频",
        AsyncTaskKind.CollectionDelete => "删除合集",
        _ => "任务",
    };

    private static string TaskStatusText(AsyncTaskStatus s) => s switch
    {
        AsyncTaskStatus.Pending => "等待中",
        AsyncTaskStatus.Running => "进行中",
        AsyncTaskStatus.Completed => "已完成",
        AsyncTaskStatus.PartialFailed => "部分失败",
        AsyncTaskStatus.Failed => "失败",
        AsyncTaskStatus.Cancelled => "已取消",
        _ => "未知",
    };

    private static string ItemStatusText(AsyncTaskItemStatus s) => s switch
    {
        AsyncTaskItemStatus.Pending => "等待中",
        AsyncTaskItemStatus.Deleted => "已删除",
        AsyncTaskItemStatus.AlreadyGone => "平台上已不存在，已清理记录",
        AsyncTaskItemStatus.UnboundCleaned => "账号已解绑，平台侧无从删除，已一并清理记录",
        AsyncTaskItemStatus.NeedLogin => "平台登录已过期，请重新登录后再删除",
        // 除抖音外平台 SDK 只回 bool —— 分不出 404 还是 401，别把不确定说成确定
        AsyncTaskItemStatus.PlatformFailed => "平台删除失败（可能已不存在），记录已保留，可重试",
        AsyncTaskItemStatus.RecordCleanupFailed => "平台上的已删除，但本地记录没清掉，可重试",
        _ => "未知",
    };

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}
