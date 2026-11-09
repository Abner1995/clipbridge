using CreatFlow.NewApp.Models;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 异步任务的服务端调用集中点（与 <see cref="VideoArchiveApi"/> / <see cref="PlatformSessionApi"/>
/// 同款：一个域一个文件，页面 VM 里不出现裸路径字符串）。
/// </summary>
public static class AsyncTaskApi
{
    /// <summary>提交一批删除，返回新任务的 ID</summary>
    public static Task<SubmitAsyncDeleteResult?> SubmitAsync(SubmitAsyncDeleteRequest request) =>
        ApiClient.PostAsync<SubmitAsyncDeleteResult>("/api/async-tasks/submit", request);

    /// <summary>认领待执行子项（服务端不改状态，所以崩了下次还能拿到）</summary>
    public static async Task<List<ServerAsyncTaskItem>> ClaimAsync()
    {
        var resp = await ApiClient.PostAsync<ClaimAsyncTasksResponse>(
            "/api/async-tasks/claim", new { });
        return resp?.Items ?? [];
    }

    /// <summary>回报一条子项的结果</summary>
    public static Task<ReportAsyncTaskItemResult?> ReportAsync(
        Guid itemId, TaskItemStatus status, string? errorMsg = null) =>
        ApiClient.PostAsync<ReportAsyncTaskItemResult>(
            "/api/async-tasks/report",
            new ReportAsyncTaskItemRequest(itemId, (int)status, errorMsg));

    /// <summary>
    /// 在飞的目标 ID。视频页/合集页进页时用它把「正在删的行」继续藏起来 ——
    /// 不藏的话翻页/切走再回来就会看见刚删掉的行复活。
    /// </summary>
    public static async Task<HashSet<Guid>> InFlightTargetIdsAsync()
    {
        var resp = await ApiClient.PostAsync<InFlightTargetsResponse>("/api/async-tasks/in-flight", new { });
        return resp?.TargetIds is { Count: > 0 } ids ? [.. ids] : [];
    }

    /// <summary>
    /// 合集页一次拉全（在飞 + 删除失败）：
    ///   · 在飞的合集 ID（藏卡片用 —— 卡是合集粒度，记录是账号粒度）；
    ///   · 删除失败未收尾的合集组 → 失败条数（卡上挂「删除未完成」警示条用）。
    /// 同一份响应里就有两份，分两次打纯是浪费。
    /// </summary>
    public static async Task<(HashSet<Guid> InFlight, Dictionary<Guid, int> FailedDeleteCounts)>
        CollectionInFlightAsync()
    {
        var resp = await ApiClient.PostAsync<InFlightTargetsResponse>("/api/async-tasks/in-flight", new { });
        HashSet<Guid> inFlight = resp?.GroupKeys is { Count: > 0 } keys ? [.. keys] : [];

        var failed = new Dictionary<Guid, int>();
        foreach (var d in resp?.FailedCollectionDeletes ?? [])
            failed[d.GroupKey] = d.FailedCount;   // 服务端已按组聚合；重复键覆盖而不抛（防御）
        return (inFlight, failed);
    }

    /// <summary>任务列表（含未完成/失败计数，侧栏徽章用）。筛选在**服务端**做 —— 客户端过滤会让计数说谎</summary>
    public static Task<AsyncTaskListResponse?> ListAsync(
        int page = 1, int pageSize = 20, int? kind = null,
        bool onlyUnsettled = false, bool onlyFailed = false) =>
        ApiClient.PostAsync<AsyncTaskListResponse>(
            "/api/async-tasks/list",
            new AsyncTaskListRequest(page, pageSize, kind, onlyUnsettled, onlyFailed));

    /// <summary>平台配置（子项色点用）。失败返回空表走兜底色 —— 这只是装饰，不该挡住列表</summary>
    public static async Task<List<PlatformConfigItem>> PlatformsAsync()
    {
        try
        {
            return await ApiClient.GetAsync<List<PlatformConfigItem>>("/api/config/platforms") ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DeleteTask] 拉平台配置失败");
            return [];
        }
    }

    /// <summary>任务明细</summary>
    public static Task<AsyncTaskDetailResponse?> DetailAsync(Guid taskId) =>
        ApiClient.PostAsync<AsyncTaskDetailResponse>(
            "/api/async-tasks/detail", new AsyncTaskDetailRequest(taskId));

    /// <summary>重试失败项（给 TaskId 重试整批，给 ItemId 重试单条）</summary>
    public static Task<object?> RetryAsync(Guid? taskId = null, Guid? itemId = null) =>
        ApiClient.PostAsync<object>("/api/async-tasks/retry", new RetryAsyncTaskRequest(taskId, itemId));

    /// <summary>删掉一条已跑完的任务（进行中的会被服务端拒绝）</summary>
    public static Task<object?> DeleteAsync(Guid taskId) =>
        ApiClient.PostAsync<object>("/api/async-tasks/delete", new DeleteAsyncTaskRequest(taskId));

    /// <summary>
    /// 手动兜底：用户确认「平台上其实已删除」→ 服务端把这条失败子项按已清收尾
    /// （全组清完时合集/记录一起清掉）。被误判卡死时的逃生门。
    /// </summary>
    public static Task<ReportAsyncTaskItemResult?> ConfirmGoneAsync(Guid itemId) =>
        ApiClient.PostAsync<ReportAsyncTaskItemResult>(
            "/api/async-tasks/confirm-gone", new ConfirmGoneRequest(itemId));

    /// <summary>清空已完成的任务（进行中的留着）；返回删掉的条数</summary>
    public static async Task<int> ClearFinishedAsync()
    {
        var resp = await ApiClient.PostAsync<ClearFinishedResponse>(
            "/api/async-tasks/clear-finished", new { });
        return resp?.Removed ?? 0;
    }
}
