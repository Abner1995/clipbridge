using System.Text.Json;

namespace CreatFlow.NewApp.Models;

// ═══ 请求 DTO（自老 App 拷贝） ═══

public record LoginRequest(
    string Username,
    string Password,
    string? MachineId = null,
    string? DeviceName = null,
    string? DeviceType = null,
    string? OsVersion = null,
    string? AppVersion = null
);

public record ActivateRequest(string Code, string? MachineId);

// ═══ 响应 DTO（和 Server 端 LoginResult / MemberDto / HeartbeatResult 对应） ═══

public record LoginResponse(
    bool Success,
    string? Token,
    string? Error,
    MemberInfo? Member,
    string? ClientSecret
);

public record MemberInfo(
    Guid Id,
    string Username,
    DateTime ExpireDate,
    bool IsActive,
    string? LastLoginIp = null,
    DateTime? LastLoginAt = null
);

public record ActivateResponse(
    bool Success,
    string? Error,
    MemberInfo? Member,
    string? ClientSecret,
    int DurationDays = 0
);

public record HeartbeatResponse(
    bool Ok,
    bool IsDisabled,
    DateTime ExpireDate,
    List<HeartbeatAnnouncementInfo>? Announcements,
    string? NewSecret,
    string? LatestVersion = null,
    string? DownloadUrl = null,
    string? ReleaseNotes = null
);

public record HeartbeatAnnouncementInfo(
    Guid Id,
    string Title,
    string Content,
    string Type,
    DateTime CreatedAt
);

// ═══ 公开系统配置（GET /api/config/public；联系我们页用）═══
// 服务端按 category 分三组，值统一是 JSON 值（contact 的内容是 HTML 字符串）

public record PublicConfigResponse(
    Dictionary<string, JsonElement>? System,
    Dictionary<string, JsonElement>? Upload,
    Dictionary<string, JsonElement>? Contact
);

// ═══ 公告 ═══

public record AnnouncementListResponse(
    List<AnnouncementItem> Items
);

public record AnnouncementItem(
    Guid Id,
    string Title,
    string Content,
    string Type,
    DateTime CreatedAt
);

// ═══ 通用错误响应（签名失败等） ═══

public record ErrorResponse(string Error);

/// <summary>
/// 平台配置项（从 GET /api/config/platforms 返回）。
/// </summary>
public record PlatformConfigItem(
    string PlatformKey,
    string DisplayName,
    string BrandColorHex,
    string LoginUrl,
    int EstimatedValidDays
);

// ═══ Platform Session API DTOs ═══

/// <summary>
/// Session list response from POST /api/platform-sessions/list
/// </summary>
public record PlatformSessionListResponse(
    List<PlatformSessionItem> Sessions
);

/// <summary>
/// Individual session item from the list API (no cookie).
/// </summary>
public record PlatformSessionItem(
    Guid Id,
    string PlatformKey,
    string? AccountLabel,
    bool IsPrimary,
    string? PlatformUid,
    string? PlatformName,
    string? PlatformAvatar,
    int Status,
    string StatusText,
    DateTime LoggedInAt,
    DateTime? ExpiredAt,
    DateTime? ProbedAt,
    string? ProbedResult
);

/// <summary>
/// Sync result from POST /api/platform-sessions/sync
/// </summary>
public record PlatformSessionSyncResult(
    Guid Id,
    string PlatformKey,
    string? AccountLabel,
    bool IsPrimary,
    string? PlatformUid,
    string? PlatformName,
    string? PlatformAvatar,
    int Status,
    string StatusText,
    DateTime LoggedInAt,
    DateTime? ExpiredAt
);

/// <summary>
/// Delete result from POST /api/platform-sessions/delete —— 服务端硬删的本端记录条数
/// （平台侧内容不动；解绑文案用它报数）。
/// </summary>
public record PlatformSessionDeleteResult(
    int PublishRecordsDeleted,
    int CollectionRecordsDeleted
);

/// <summary>
/// Sync request sent to POST /api/platform-sessions/sync
/// </summary>
public record SyncSessionRequest(
    Guid? Id,
    string PlatformKey,
    string? PlatformUid,
    string? PlatformName,
    string? PlatformAvatar,
    string? Cookie,
    string? LocalStorageJson,
    string? ExtraDataJson
);

/// <summary>
/// Simple request wrapper for operations that only need an ID.
/// </summary>
public record SessionIdRequest(Guid Id);

/// <summary>
/// Rename request
/// </summary>
public record RenameSessionRequest(Guid Id, string? AccountLabel);

/// <summary>
/// Probe result from POST /api/platform-sessions/probe
/// </summary>
public record ProbeResponse(List<ProbeItem> Results);

/// <summary>
/// Individual probe result item.
/// </summary>
public record ProbeItem(Guid Id, int Status, string? ProbedResult);

// ═══ 钉钉配置 API DTOs ═══
// 注意：必须保持 public（KeepPublicApi + obfuscar SkipType 保护）。
// 若改为 private 嵌套类型会被混淆抹掉构造函数参数名，STJ 反序列化直接抛异常。

/// <summary>
/// Response from POST /api/dingtalk/config
/// </summary>
public record DingTalkConfigResponse(Guid Id, string? WebhookKey, string? SecretMasked);

/// <summary>
/// Response from POST /api/dingtalk/config/save
/// </summary>
public record SaveConfigResponse(bool Success, string? Error, DingTalkConfigResponse? Config);

/// <summary>
/// Response from POST /api/dingtalk/config/delete
/// </summary>
public record SimpleResult(bool Success, string? Error);

/// <summary>
/// Response from POST /api/dingtalk/config/test
/// </summary>
public record TestResult(bool Success, string? Error);

// ═══ 账号分组 API DTOs（2026-10-15 上云；整表替换语义）═══
// 同钉钉 DTO 的纪律：必须保持 public（obfuscar 整跳 Models.*；改嵌套/私有会被混淆破坏 STJ 绑定）。

/// <summary>
/// 一组账号分组（服务端 <-> 客户端一一对应；数组序 = 组序 / 组内成员序）。
/// </summary>
public record AccountGroupItem(Guid Id, string? Name, List<Guid>? SessionIds);

/// <summary>
/// Response from POST /api/account-groups/list
/// </summary>
public record AccountGroupListResponse(List<AccountGroupItem>? Groups);

/// <summary>
/// Body of POST /api/account-groups/save（整表替换：按 ID diff 更新/新增/删除）。
/// </summary>
public record SaveAccountGroupsRequest(List<AccountGroupItem> Groups);

/// <summary>
/// Response from POST /api/account-groups/save
/// </summary>
public record SaveAccountGroupsResponse(bool Ok);

// ═══ 角色库 API DTOs（2026-10-15 上云；每会员一行、整表替换 + exists 标记）═══

/// <summary>
/// 一条角色（Id 为任意字符串：builtin-*/Guid；数组序 = 角色库展示序）。
/// </summary>
public record ComposeRoleItem(string? Id, string? Name, string? Template, string? Persona, string? Knowledge);

/// <summary>
/// Response from POST /api/compose-roles/list —— Exists 区分「从未保存过」（false，客户端保留内置示例）
/// 与「保存过（含显式空库）」（true，客户端整表落地）。
/// </summary>
public record ComposeRoleListResponse(bool Exists, List<ComposeRoleItem>? Roles);

/// <summary>
/// Body of POST /api/compose-roles/save（整表替换；null ≠ 清库，清库发 "roles":[]）。
/// </summary>
public record SaveComposeRolesRequest(List<ComposeRoleItem> Roles);

/// <summary>
/// Response from POST /api/compose-roles/save
/// </summary>
public record SaveComposeRolesResponse(bool Ok);
