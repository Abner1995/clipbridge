namespace CreatFlow.NewApp.Models;

// ═══════════════════════════════════════════════════════════════════════════
//  发布相关 wire DTO —— 与服务端 CreatFlow.Application/Dtos/PublishDto.cs 对应。
//
//  为什么在客户端再抄一份而不是引用 Application：NewApp 只引用 Shared/Platforms/Media，
//  不引用 Application（那是服务端内部层）。老 App 同样是自带一份
//  （CreatFlow.App/Models/PublishModels.cs），本文件沿用该惯例，P5 老 App 退役后
//  再统一收敛。**两边的属性名必须逐字一致** —— 序列化按属性名对，改名即静默丢字段。
//
//  ⚠️ 命名：服务端的 PublishTarget 与本项目客户端概念 PublishTarget（Models/PublishModels.cs）
//  重名，故 wire 侧一律加 Dto 后缀。
// ═══════════════════════════════════════════════════════════════════════════

// ═══ 请求 ═══

/// <summary>
/// 提交发布任务。POST /api/publish/submit
/// 一条 draft → 一个 task，targets 里每个目标 → 一条 PublishPlatform 记录。
/// </summary>
public sealed record SubmitPublishRequest(
    // -- 视频 --
    string VideoFilePath,
    string VideoFileName,
    long VideoFileSize,
    string VideoHash,
    int? VideoDurationS,
    int? VideoWidth,
    int? VideoHeight,
    // -- 全局 --
    string? CoverFilePath,
    string? CommonTitle,
    string? CommonDesc,
    DateTime? ScheduledAt,
    // -- 目标 --
    List<PublishTargetDto> Targets);

/// <summary>
/// 单个发布目标 —— 平台 + 会话（账号）+ 平台专有参数。
/// Title/Description 为 null 表示继承全局。
/// 合集不再走服务端列 <c>CollectionId</c>（平台侧 ID 在 ExtraDataJson 的载荷里），
/// 客户端自 2026-11-06 起停止发送；服务端契约的可选字段保留兼容。
/// </summary>
public sealed record PublishTargetDto(
    string PlatformKey,
    Guid? PlatformSessionId,
    string? Title,
    string? Description,
    string? CoverFilePath,
    string? ExtraDataJson);

/// <summary>
/// 回报单平台发布结果。POST /api/publish/report
/// 不回报则服务端轮询会把本地 Completed/Failed 覆盖回 Waiting。
/// </summary>
public sealed record ReportPublishRequest(
    Guid PlatformId,
    bool Success,
    string? PlatformVideoId,
    string? PlatformVideoUrl,
    string? ErrorMsg,
    string? CoverUrl,
    string? ExtraDataJson = null);

/// <summary>简单 Id 请求（detail / retry / delete）</summary>
public sealed record PublishIdRequest(Guid Id);

/// <summary>单平台记录操作请求（platforms/delete · platforms/hide）</summary>
public sealed record PublishPlatformIdRequest(Guid PlatformId);

/// <summary>分页查询（视频管理页）。POST /api/publish/platforms/page</summary>
public sealed record PlatformPageRequest(
    int Page = 1,
    int PageSize = 20,
    string? PlatformKey = null,
    string? Keyword = null);

/// <summary>列出一条视频的全部已发布副本。POST /api/publish/platforms/by-task</summary>
public sealed record PublishTaskIdRequest(Guid TaskId);

/// <summary>上面那个接口的响应包裹（服务端返回 `Ok(new { items })`）</summary>
public sealed record VideoArchiveByTaskResponse(List<VideoArchiveItem> Items);

// ═══ 响应 ═══

/// <summary>
/// 单平台发布记录。submit / detail / platforms 三处共用同一形状
/// （服务端 PublishPlatformDto）。
/// </summary>
public sealed record PublishPlatformItem(
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
    int Status,
    string StatusText,
    int ProgressPct,
    string? ErrorMsg,
    int RetryCount,
    string? PlatformVideoId,
    string? PlatformVideoUrl,
    DateTime? PublishedAt,
    DateTime CreatedAt);

/// <summary>submit 响应（服务端 PublishSubmitResult）</summary>
public sealed record PublishSubmitResult(
    Guid TaskId,
    List<PublishPlatformItem> Platforms);

/// <summary>任务详情（服务端 PublishTaskDetailDto）</summary>
public sealed record PublishTaskDetail(
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
    List<PublishPlatformItem> Platforms,
    DateTime CreatedAt,
    DateTime? LastModified);

/// <summary>任务列表项（服务端 PublishTaskDto）</summary>
public sealed record PublishTaskSummary(
    Guid Id,
    string VideoFileName,
    string? VideoFilePath,
    string? CommonTitle,
    long VideoFileSize,
    int? VideoDurationS,
    int PlatformCount,
    int PublishedCount,
    int FailedCount,
    int Status,
    string StatusText,
    DateTime? ScheduledAt,
    DateTime CreatedAt,
    DateTime? LastModified);

// ═══ 队列页：任务 × 平台联合行 ═══

/// <summary>
/// POST /api/publish/platforms 的响应 —— 队列页重建本地队列的唯一数据源
/// （服务端状态是权威，本地不另存队列）。
/// </summary>
public sealed record PlatformListResponse(List<ServerPlatformItem> Platforms);

/// <summary>
/// 任务 + 平台记录的联合行（服务端 PublishPlatformWithTaskDto）。
/// 队列页按 TaskId 聚合成「视频」卡，展开看 PlatformId 明细。
/// </summary>
public sealed record ServerPlatformItem(
    // -- 任务 --
    Guid TaskId,
    string VideoFileName,
    string VideoFilePath,
    // -- 平台 --
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
    DateTime UpdatedAt);

/// <summary>
/// 视频管理页的一行 = 一条平台发布记录（服务端 PlatformPageItem）。
///
/// ⚠️ **不要复用队列页的 <see cref="ServerPlatformItem"/>**：那个形状少了
/// <see cref="VideoFileSize"/> / <see cref="CommonTitle"/>（标题回退链要它）、多一个服务端
/// 分页响应根本不发的 `UpdatedAt`。用错了不报错，只是静默显示成「0 B」，且标题回退链拿不到通用标题。
/// </summary>
public sealed record VideoArchiveItem(
    // -- 任务 --
    Guid TaskId,
    string VideoFileName,
    string VideoFilePath,
    long VideoFileSize,
    string? CommonTitle,
    // -- 平台 --
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
    bool HiddenInQueue);

/// <summary>某平台的记录条数（筛选 chip 上的计数）</summary>
public sealed record PlatformCountItem(string PlatformKey, int Count);

/// <summary>
/// 分页响应（视频管理页）。<see cref="PlatformCounts"/> 是 **facet** ——
/// 跟关键词、不跟平台筛选（否则点了抖音之后其余 chip 的计数会一起变成抖音的数）。
/// 「全部」用 <see cref="Total"/>，别拿 counts 求和。
/// </summary>
public sealed record VideoArchivePage(
    List<VideoArchiveItem> Items,
    int Total,
    int Page,
    int PageSize,
    List<PlatformCountItem> PlatformCounts);

// ═══ 状态枚举（数值与服务端 PublishPlatformStatus / PublishTaskStatus 对齐） ═══

/// <summary>
/// 单平台记录状态 —— 数值即服务端 <c>PublishPlatformStatus</c> 枚举值，直接强转使用。
/// 老版把 int 裸传到 UI（到处魔法数字）；这里给名字。
/// 逐字对齐 CreatFlow.Domain/Entities/PublishPlatform.cs:251。
/// </summary>
public enum PublishPlatformStatus
{
    Pending = 0,
    Uploading = 1,
    /// <summary>等平台转码（视频号 clip / 抖音转码）</summary>
    Transcoding = 2,
    Publishing = 3,
    Published = 4,
    Failed = 5,
}

/// <summary>任务聚合状态</summary>
public enum PublishTaskStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    PartialFailed = 3,
    Failed = 4,
}
