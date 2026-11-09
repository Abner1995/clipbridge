namespace CreatFlow.Application.DTOs;

// ═══ Response DTOs ═══

/// <summary>
/// Summary DTO for publish task list display.
/// </summary>
public sealed record PublishTaskDto(
    Guid Id,
    string VideoFileName,
    string? VideoFilePath,
    string? CommonTitle,
    long VideoFileSize,
    int? VideoDurationS,
    int PlatformCount,
    int PublishedCount,
    int FailedCount,
    int Status,                     // PublishTaskStatus cast to int
    string StatusText,
    DateTime? ScheduledAt,
    DateTime CreatedAt,
    DateTime? LastModified
);

/// <summary>
/// Full publish task detail including all platform records.
/// </summary>
public sealed record PublishTaskDetailDto(
    Guid Id,
    Guid MemberId,
    string VideoFileName,
    string VideoFilePath,
    long VideoFileSize,
    string VideoHash,
    int? VideoDurationS,
    int? VideoWidth,
    int? VideoHeight,
    string? CoverFilePath,
    string? CommonTitle,
    string? CommonDesc,
    DateTime? ScheduledAt,
    int Status,
    string StatusText,
    List<PublishPlatformDto> Platforms,
    DateTime CreatedAt,
    DateTime? LastModified
);

/// <summary>
/// Per-platform publish record DTO.
/// </summary>
public sealed record PublishPlatformDto(
    Guid Id,
    string PlatformKey,
    string PlatformDisplayName,
    Guid? PlatformSessionId,
    string? Title,
    string? Description,
    string? CoverFilePath,
    string? CoverUrl,
    Guid? CollectionId,
    string? ExtraDataJson,
    int Status,                     // PublishPlatformStatus cast to int
    string StatusText,
    int ProgressPct,
    string? ErrorMsg,
    int RetryCount,
    string? PlatformVideoId,
    string? PlatformVideoUrl,
    DateTime? PublishedAt,
    DateTime CreatedAt
);

/// <summary>
/// Result after submitting a publish task.
/// </summary>
public sealed record PublishSubmitResult(
    Guid TaskId,
    List<PublishPlatformDto> Platforms
);

// ═══ Request DTOs ═══

/// <summary>
/// Submit a new publish task.
/// Each target specifies platform + session (account) + platform-specific config.
/// </summary>
public sealed record SubmitPublishRequest(
    // -- Video --
    string VideoFilePath,
    string VideoFileName,
    long VideoFileSize,
    string VideoHash,
    int? VideoDurationS,
    int? VideoWidth,
    int? VideoHeight,

    // -- Common --
    string? CoverFilePath,
    string? CommonTitle,
    string? CommonDesc,
    DateTime? ScheduledAt,

    // -- Platform targets --
    List<PublishTarget> Targets
);

/// <summary>
/// Single publish target — platform + session + platform-specific params.
/// </summary>
public sealed record PublishTarget(
    string PlatformKey,
    Guid? PlatformSessionId,
    string? Title,                  // 平台侧标题（null=继承 common）
    string? Description,            // 平台侧简介（null=继承 common）
    string? CoverFilePath,          // 平台侧封面图路径
    Guid? CollectionId,             // 关联合集ID
    string? ExtraDataJson           // 平台特有参数 JSON
);

/// <summary>
/// Report a single platform publish result from desktop app.
/// Called after desktop finishes a platform SDK call (upload + publish).
/// </summary>
public sealed record ReportPublishRequest(
    Guid PlatformId,                    // PublishPlatform ID
    bool Success,
    string? PlatformVideoId,            // 平台返回的视频ID（成功时必填）
    string? PlatformVideoUrl,           // 平台返回的视频链接
    string? ErrorMsg,                   // 失败原因（失败时必填）
    string? CoverUrl,                   // 封面上传后 CDN URL
    string? ExtraDataJson = null        // 回写到 extra_data_json 的字段（如 ks_cover_key）
);

/// <summary>
/// Get detail of a single publish task.
/// </summary>
public sealed record GetPublishDetailRequest(
    Guid Id
);

/// <summary>
/// Retry a failed platform publish record.
/// </summary>
public sealed record RetryPublishRequest(
    Guid PlatformId
);

/// <summary>
/// Delete a single platform publish record.
/// </summary>
public sealed record DeletePlatformRequest(
    Guid PlatformId
);

/// <summary>
/// Hide a published platform record from the upload queue (soft-delete).
/// The record remains in the DB for video management page.
/// </summary>
public sealed record HidePlatformRequest(
    Guid PlatformId
);

/// <summary>
/// Paginated query for platform records (video management page).
/// Only returns Published records.
/// 2026-09-21：加平台筛选与标题搜索 —— 位置参数**都带默认值**，
/// 老客户端（只发 page/pageSize）行为不变。
/// </summary>
public sealed record PlatformPageRequest(
    int Page = 1,
    int PageSize = 20,
    string? PlatformKey = null,
    string? Keyword = null
);

/// <summary>
/// 列出一个发布任务（= 一条视频）的**全部已发布平台记录**。
/// POST /api/publish/platforms/by-task
///
/// 为什么不能靠分页接口凑：分页单位就是平台记录，一个视频发到 8 账号 × 4 平台 = 32 条，
/// 必然被切在相邻几页里。「删除该视频全部副本」只作用于当前页的话，
/// 用户会以为删干净了、实际平台上还留着 —— 正是老版 V2 那个坑的翻版。
/// </summary>
public sealed record PublishTaskIdRequest(
    Guid TaskId
);

// ═══ Admin DTOs ═══

/// <summary>
/// Admin list item — publish task summary with member info.
/// </summary>
public sealed record AdminPublishItem(
    Guid Id,
    string VideoFileName,
    string? CommonTitle,
    string MemberUsername,
    long VideoFileSize,
    int PlatformCount,
    int PublishedCount,
    int FailedCount,
    int Status,
    string StatusText,
    DateTime CreatedAt,
    DateTime? LastModified
);

/// <summary>
/// Paginated result for admin publish tasks list.
/// </summary>
public sealed record AdminPublishListResponse(
    List<AdminPublishItem> Items,
    int Total,
    int Page,
    int PageSize
);

/// <summary>
/// Combined platform + task DTO for upload queue display.
/// One record = one platform publish attempt, with key task-level fields included.
/// </summary>
public sealed record PublishPlatformWithTaskDto(
    // -- Task --
    Guid TaskId,
    string VideoFileName,
    string VideoFilePath,

    // -- Platform --
    Guid PlatformId,
    string PlatformKey,
    string PlatformDisplayName,
    Guid? PlatformSessionId,
    string? Title,
    string? Description,
    string? CoverFilePath,
    string? CoverUrl,
    Guid? CollectionId,
    string? ExtraDataJson,
    int Status,
    string StatusText,
    int ProgressPct,
    string? ErrorMsg,
    int RetryCount,
    string? PlatformVideoId,
    string? PlatformVideoUrl,
    DateTime? PublishedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

/// <summary>
/// Response for GET /api/publish/platforms — all platform records for the current member.
/// </summary>
public sealed record PlatformListResponse(
    List<PublishPlatformWithTaskDto> Platforms
);

// ═══ Video Management (paginated platform list) ═══

/// <summary>
/// A single row in the video management page — one platform publish record
/// with its parent task's video info.
/// </summary>
public sealed record PlatformPageItem(
    // -- Task --
    Guid TaskId,
    string VideoFileName,
    string VideoFilePath,
    long VideoFileSize,
    string? CommonTitle,

    // -- Platform --
    Guid PlatformId,
    string PlatformKey,
    string PlatformDisplayName,
    Guid? PlatformSessionId,
    string? Title,
    string? Description,
    string? CoverFilePath,
    string? CoverUrl,
    Guid? CollectionId,
    string? ExtraDataJson,
    int Status,
    string StatusText,
    int ProgressPct,
    string? ErrorMsg,
    int RetryCount,
    string? PlatformVideoId,
    string? PlatformVideoUrl,
    DateTime? PublishedAt,
    DateTime CreatedAt,
    // 已从上传队列隐藏（软删除）。归档页**要**显示它们 ——
    // 队列页「清空已完成」之后记录只在这里看得到，不说清楚用户会以为发丢了。
    bool HiddenInQueue
);

/// <summary>每平台记录条数（视频管理页筛选 chip 上的计数）。</summary>
public sealed record PlatformCountItem(
    string PlatformKey,
    int Count
);

/// <summary>
/// Paginated response for video management page.
/// POST /api/publish/platforms/page
/// </summary>
public sealed record PlatformPageResponse(
    List<PlatformPageItem> Items,
    int Total,
    int Page,
    int PageSize,
    // Facet 计数：跟 Keyword，**不跟** PlatformKey（否则选中抖音后所有 chip 都变成同一个数）。
    // 「全部」那一格用 Total，不要拿它求和。
    List<PlatformCountItem> PlatformCounts
);

// ═══ Admin Platform List DTOs ═══

/// <summary>
/// Admin list item — one platform publish record with parent task and member info.
/// </summary>
public sealed record AdminPlatformItem(
    // -- Task --
    Guid TaskId,
    string VideoFileName,
    string VideoFilePath,
    long VideoFileSize,
    string? CommonTitle,
    string MemberUsername,

    // -- Platform --
    Guid PlatformId,
    string PlatformKey,
    string PlatformDisplayName,
    Guid? PlatformSessionId,
    string? Title,
    string? Description,
    string? CoverUrl,
    string? ExtraDataJson,
    int Status,
    string StatusText,
    int ProgressPct,
    string? ErrorMsg,
    int RetryCount,
    string? PlatformVideoId,
    string? PlatformVideoUrl,
    DateTime? PublishedAt,
    DateTime CreatedAt
);

/// <summary>
/// Paginated result for admin platform records list.
/// </summary>
public sealed record AdminPlatformListResponse(
    List<AdminPlatformItem> Items,
    int Total,
    int Page,
    int PageSize
);
