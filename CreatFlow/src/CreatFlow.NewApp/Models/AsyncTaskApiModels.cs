namespace CreatFlow.NewApp.Models;

// ═══════════════════════════════════════════════════════════════════════════
//  异步任务的服务端契约（wire DTO）
//
//  ⚠️ 这些类型是**逐字抄服务端** CreatFlow.Application/DTOs/AsyncTaskDto.cs 的：
//  STJ 按属性名绑定，改一边的名字另一边就静默丢字段（队列页踩过）。
//  服务端那份改了名字，这里必须同步。
// ═══════════════════════════════════════════════════════════════════════════

// ═══ 请求 ═══

/// <summary>提交一批删除。POST /api/async-tasks/submit</summary>
public sealed record SubmitAsyncDeleteRequest(
    int Kind,
    string Title,
    List<AsyncDeleteTargetRequest> Targets);

/// <summary>一条待删目标（含账号名与平台侧 ID 的快照 —— 本地记录随后会被删掉）</summary>
public sealed record AsyncDeleteTargetRequest(
    Guid TargetId,
    int TargetKind,
    Guid GroupKey,
    string PlatformKey,
    Guid? PlatformSessionId = null,
    string? SessionLabel = null,
    string? PlatformObjectId = null,
    int ContentType = 0);              // 合集内容类型快照：0=视频 1=文章（头条删除接口不同）

/// <summary>任务列表。POST /api/async-tasks/list</summary>
public sealed record AsyncTaskListRequest(
    int Page = 1,
    int PageSize = 20,
    int? Kind = null,
    bool OnlyUnsettled = false,
    bool OnlyFailed = false);

/// <summary>任务明细。POST /api/async-tasks/detail</summary>
public sealed record AsyncTaskDetailRequest(Guid TaskId);

/// <summary>回报一条子项的结果。POST /api/async-tasks/report</summary>
public sealed record ReportAsyncTaskItemRequest(Guid ItemId, int Status, string? ErrorMsg = null);

/// <summary>重试失败项。POST /api/async-tasks/retry</summary>
public sealed record RetryAsyncTaskRequest(Guid? TaskId = null, Guid? ItemId = null);

/// <summary>删掉一条已跑完的任务。POST /api/async-tasks/delete</summary>
public sealed record DeleteAsyncTaskRequest(Guid TaskId);

/// <summary>确认「平台侧其实已删除」→ 清理本地记录（手动兜底）。POST /api/async-tasks/confirm-gone</summary>
public sealed record ConfirmGoneRequest(Guid ItemId);

// ═══ 响应 ═══

public sealed record SubmitAsyncDeleteResult(Guid TaskId, string Title, int Total);

/// <summary>任务页主行</summary>
public sealed record ServerAsyncTask(
    Guid Id,
    int Kind,
    string KindText,
    string Title,
    int Status,
    string StatusText,
    int TotalCount,
    int DoneCount,
    int FailedCount,
    DateTime CreatedAt,
    DateTime? LastModified);

/// <summary>
/// 子项。**自带账号名与平台侧 ID 的快照** —— 本地记录删掉之后任务页还能照常显示，
/// 不会退化成一行「账号已解绑」。
/// </summary>
public sealed record ServerAsyncTaskItem(
    Guid Id,
    Guid TaskId,
    Guid TargetId,
    int TargetKind,
    Guid GroupKey,
    string PlatformKey,
    string PlatformDisplayName,
    Guid? PlatformSessionId,
    string? SessionLabel,
    string? PlatformObjectId,
    int Status,
    string StatusText,
    string? ErrorMsg,
    int RetryCount,
    DateTime? FinishedAt,
    DateTime CreatedAt,
    int ContentType = 0);              // 合集内容类型快照：0=视频 1=文章

public sealed record AsyncTaskDetailResponse(ServerAsyncTask Task, List<ServerAsyncTaskItem> Items);

public sealed record AsyncTaskListResponse(
    List<ServerAsyncTask> Items,
    int Total,
    int Page,
    int PageSize,
    int UnsettledCount,
    int FailedCount);

/// <summary>认领的待执行子项（服务端**不因认领改状态**，所以崩溃自愈）</summary>
public sealed record ClaimAsyncTasksResponse(List<ServerAsyncTaskItem> Items);

/// <summary>在飞的记录：TargetIds = 视频页（记录粒度）/ GroupKeys = 合集页（合集粒度）；
/// FailedCollectionDeletes = 已停稳但还有失败项的合集组（卡上挂「删除未完成」条）</summary>
public sealed record InFlightTargetsResponse(
    List<Guid> TargetIds,
    List<Guid> GroupKeys,
    List<CollectionDeleteFailureDto>? FailedCollectionDeletes = null);

/// <summary>删除失败还未收尾的合集组：group_key → 失败条数</summary>
public sealed record CollectionDeleteFailureDto(Guid GroupKey, int FailedCount);

public sealed record ReportAsyncTaskItemResult(bool Ok, bool LocalCleared, bool GroupHasFailure);

/// <summary>清空已完成的结果（服务端返回 `Ok(new { ok, removed })`）</summary>
public sealed record ClearFinishedResponse(bool Ok, int Removed);
