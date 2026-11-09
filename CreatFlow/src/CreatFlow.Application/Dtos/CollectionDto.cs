namespace CreatFlow.Application.DTOs;

// ═══ Response DTOs ═══

/// <summary>
/// Summary DTO for collection list display.
/// </summary>
public sealed record CollectionDto(
    Guid Id,
    string Title,
    string? Description,
    string? CoverImagePath,
    string? CoverImageUrl,
    int PlatformCount,
    int CreatedCount,
    DateTime CreatedAt,
    DateTime? LastModified,
    List<PlatformSummaryDto>? Platforms,     // ← 发布页用：取 platform_collection_id
    int ContentType = 0                      // 0=视频 1=文章（文章仅头条，卡片要显示类型徽标）
);

/// <summary>
/// Lightweight platform summary included in collection list.
/// Used by the publish page to map collection → platform_collection_id (mix_id).
/// </summary>
public sealed record PlatformSummaryDto(
    string PlatformKey,
    string? PlatformCollectionId,            // Douyin: mix_id
    Guid? PlatformSessionId,
    string? PlatformSessionLabel             // "大号@示例账号"
);

/// <summary>
/// Full collection detail including per-platform sync status.
/// </summary>
public sealed record CollectionDetailDto(
    Guid Id,
    string Title,
    string? Description,
    string? CoverImagePath,          // 本地路径（用于重试）
    string? CoverImageUrl,           // CDN URL（用于展示）
    List<CollectionPlatformDto> Platforms,
    DateTime CreatedAt,
    DateTime? LastModified,
    int ContentType = 0                      // 0=视频 1=文章（编辑时类型只读）
);

/// <summary>
/// Per-platform sync record DTO.
/// </summary>
public sealed record CollectionPlatformDto(
    Guid Id,
    string PlatformKey,
    string PlatformDisplayName,
    Guid? PlatformSessionId,
    string? PlatformCollectionId,
    string? CollectionType,
    string? Title,
    string? Description,
    string? CoverImage,
    int Status,
    string StatusText,
    string? ErrorMsg,
    string? ExtraDataJson,
    DateTime CreatedAt
);

/// <summary>
/// Result after syncing a collection to platforms.
/// </summary>
public sealed record CollectionSyncResultDto(
    Guid CollectionId,
    List<CollectionPlatformDto> Results
);

// ═══ Request DTOs ═══

/// <summary>
/// Create a new collection, optionally with initial platform targets.
/// Each target specifies the platform, the session (account), and optional collection type.
/// </summary>
public sealed record CreateCollectionRequest(
    string Title,
    string? Description,
    string? CoverImagePath,            // 本地封面图路径
    List<CreateTarget>? Targets,        // 初始同步到哪些平台+账号
    int ContentType = 0                // 0=视频 1=文章（文章仅允许头条目标）
);

/// <summary>
/// Single create target — platform + session + optional collection type.
/// </summary>
public sealed record CreateTarget(
    string PlatformKey,
    Guid? PlatformSessionId,
    string? CollectionType             // 合集类型（如"美食探店"），账号级自定义
);

/// <summary>
/// Update collection metadata.
/// </summary>
public sealed record UpdateCollectionRequest(
    Guid Id,
    string Title,
    string? Description,
    string? CoverImagePath            // 本地封面图路径
);

/// <summary>
/// Delete a collection and its platform records.
/// </summary>
public sealed record DeleteCollectionRequest(
    Guid Id
);

/// <summary>
/// Sync (or re-sync) a collection to specified platforms.
/// </summary>
public sealed record SyncCollectionRequest(
    Guid CollectionId,
    List<SyncTarget> Targets
);

/// <summary>
/// Single sync target — a platform + session pair.
/// </summary>
public sealed record SyncTarget(
    string PlatformKey,
    Guid? PlatformSessionId,
    string? Title,                  // 平台侧标题（null=继承）
    string? Description,            // 平台侧简介（null=继承）
    string? CoverImage,             // 平台侧封面（null=继承）
    string? ExtraDataJson           // 平台扩展数据
);

/// <summary>
/// Get detail of a single collection.
/// </summary>
public sealed record GetCollectionDetailRequest(
    Guid Id
);

/// <summary>
/// Admin list of all collections — includes member username.
/// </summary>
public sealed record AdminCollectionItem(
    Guid Id,
    string Title,
    string? Description,
    string? CoverImagePath,
    string? CoverImageUrl,
    string MemberUsername,
    int PlatformCount,
    int CreatedCount,
    DateTime CreatedAt,
    DateTime? LastModified
);

/// <summary>
/// Paginated result for admin collections list.
/// </summary>
public sealed record AdminCollectionListResponse(
    List<AdminCollectionItem> Items,
    int Total,
    int Page,
    int PageSize
);

/// <summary>
/// Report a single platform sync result from the desktop app.
/// Called after the desktop finishes calling a platform SDK (success or failure).
/// </summary>
public sealed record ReportCollectionRequest(
    Guid CollectionPlatformId,          // CollectionPlatform 记录 ID
    bool Success,                       // true = 平台创建成功
    string? PlatformCollectionId,       // 平台返回的合集 ID（成功时必填）
    string? ErrorMsg,                   // 失败原因（失败时必填）
    string? Title,                      // 平台侧合集标题（成功时填充，覆盖 platform record）
    string? Description,                // 平台侧合集简介（成功时填充）
    string? CoverImageUrl               // 平台侧封面图 TOS key（成功时填充，回填到 x_collections.cover_image_url）
);
