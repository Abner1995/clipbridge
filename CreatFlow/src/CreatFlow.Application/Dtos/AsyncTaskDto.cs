namespace CreatFlow.Application.DTOs;

// ═══ Request DTOs ═══

/// <summary>
/// 提交一批删除（视频作品 / 合集）。
/// POST /api/async-tasks/submit
///
/// 客户端把「看得见的那些行」原样报上来（含账号名与平台侧 ID 的快照）——
/// 本地记录可能先被删掉，服务端事后再去读就读不到了。
/// </summary>
public sealed record SubmitAsyncDeleteRequest(
    int Kind,                                  // 1=视频删除 2=合集删除
    string Title,                              // 任务页主行文案（客户端组好，含条数）
    List<AsyncDeleteTargetRequest> Targets
);

/// <summary>一条待删目标</summary>
public sealed record AsyncDeleteTargetRequest(
    Guid TargetId,                             // x_publish_platforms.id 或 x_collection_platforms.id
    int TargetKind,                            // 1=发布记录 2=合集记录
    Guid GroupKey,                             // 视频=PublishTaskId / 合集=CollectionId
    string PlatformKey,
    Guid? PlatformSessionId = null,
    string? SessionLabel = null,               // 账号名快照
    string? PlatformObjectId = null,           // 平台侧作品/合集 ID 快照
    int ContentType = 0                        // 合集内容类型快照：0=视频 1=文章（头条删除接口不同）
);

/// <summary>拉任务列表。POST /api/async-tasks/list</summary>
public sealed record AsyncTaskListRequest(
    int Page = 1,
    int PageSize = 20,
    int? Kind = null,
    bool OnlyUnsettled = false,
    /// <summary>只看有失败条目的（任务页「仅看失败」）—— 服务端过滤，客户端过滤会让计数说谎</summary>
    bool OnlyFailed = false
);

/// <summary>拉某个任务的子项明细。POST /api/async-tasks/detail</summary>
public sealed record AsyncTaskDetailRequest(
    Guid TaskId
);

/// <summary>删掉一条**已跑完**的任务。POST /api/async-tasks/delete</summary>
public sealed record DeleteAsyncTaskRequest(
    Guid TaskId
);

/// <summary>
/// 手动兜底：用户亲自确认「平台侧其实已删除」→ 把这条失败子项按已清收尾。
/// POST /api/async-tasks/confirm-gone
/// </summary>
public sealed record ConfirmGoneRequest(
    Guid ItemId
);

/// <summary>回报一条子项的结果。POST /api/async-tasks/report</summary>
public sealed record ReportAsyncTaskItemRequest(
    Guid ItemId,
    int Status,                                // AsyncTaskItemStatus 的整数值（必须终态）
    string? ErrorMsg = null
);

/// <summary>重试。TaskId 与 ItemId 二选一：给 ItemId 重试一条，给 TaskId 重试整批的失败项。</summary>
public sealed record RetryAsyncTaskRequest(
    Guid? TaskId = null,
    Guid? ItemId = null
);

// ═══ Response DTOs ═══

/// <summary>提交结果</summary>
public sealed record SubmitAsyncDeleteResult(
    Guid TaskId,
    string Title,
    int Total
);

/// <summary>任务页主行</summary>
public sealed record AsyncTaskDto(
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
    DateTime? LastModified
);

/// <summary>子项：**自带全部展示所需字段**（账号名/平台侧 ID 都是快照）——
/// 本地记录删掉之后任务页还能照常显示，不会变成一行「账号已解绑」。</summary>
public sealed record AsyncTaskItemDto(
    Guid Id,
    Guid TaskId,
    Guid TargetId,
    int TargetKind,
    /// <summary>同组键（视频=PublishTaskId / 合集=CollectionId）—— 任务页把合集的 N 个账号折成一组要它</summary>
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
    /// <summary>合集内容类型快照：0=视频 1=文章（消费端据此选头条删除接口）</summary>
    int ContentType = 0
);

/// <summary>任务明细</summary>
public sealed record AsyncTaskDetailDto(
    AsyncTaskDto Task,
    List<AsyncTaskItemDto> Items
);

/// <summary>分页任务列表</summary>
public sealed record AsyncTaskListResponse(
    List<AsyncTaskDto> Items,
    int Total,
    int Page,
    int PageSize,
    int UnsettledCount,
    int FailedCount
);

/// <summary>
/// 认领：把待执行子项交给桌面端消费者。
/// **不改服务端状态**（见 AsyncTaskItemStatus 的注释：改了就得配 reap 任务）。
/// </summary>
public sealed record ClaimAsyncTasksResponse(
    List<AsyncTaskItemDto> Items
);

/// <summary>
/// 在飞的记录：页面靠它把「正在删的行/卡片」继续藏起来。
///
/// 两份粒度分开给，因为两页的「一行」不是一回事：
/// <paramref name="TargetIds"/> = 视频页（一行 = 一条平台发布记录）；
/// <paramref name="GroupKeys"/> = 合集页（一张卡 = 一个合集，内部 N 条账号记录）。
///
/// <paramref name="FailedCollectionDeletes"/>：**已停稳但还有失败项**的合集组 ——
/// 卡不再「在飞」（不会被藏），但记录因「全组清完才删」留在本地；合集页据此
/// 在卡上挂「删除未完成」警示条（2026-10-14 用户实测：一张看着正常的卡其实是因为
/// 1 条子项失败而被卡住，页面上毫无解释）。
/// </summary>
public sealed record InFlightTargetsResponse(
    List<Guid> TargetIds,
    List<Guid> GroupKeys,
    List<CollectionDeleteFailureDto> FailedCollectionDeletes
);

/// <summary>删除失败还未收尾的合集组：group_key → 失败条数</summary>
public sealed record CollectionDeleteFailureDto(
    Guid GroupKey,
    int FailedCount
);

/// <summary>回报结果</summary>
public sealed record ReportAsyncTaskItemResult(
    bool Ok,
    /// <summary>本地记录是否已连平台侧一起清掉（合集要全组清完才清）</summary>
    bool LocalCleared,
    /// <summary>本组是否还有人没删完（合集页据此判断要不要保留记录）</summary>
    bool GroupHasFailure
);
