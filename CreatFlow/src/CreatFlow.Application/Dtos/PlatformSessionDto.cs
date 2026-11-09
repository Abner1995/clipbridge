namespace CreatFlow.Application.DTOs;

// ═══ Response DTOs ═══

/// <summary>
/// Full session info returned to desktop app for display.
/// </summary>
public sealed record PlatformSessionDto(
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
/// Summary-only DTO (no cookie). Used in list responses.
/// </summary>
public sealed record PlatformSessionSummaryDto(
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
/// Result after sync (login/relogin). Returns the full session including
/// newly issued ID so the desktop can associate it.
/// </summary>
public sealed record PlatformSessionSyncResultDto(
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
/// Result of a session unbind (delete). Cleanup counts of the LOCAL records that
/// were hard-deleted — the platform-side videos/collections are never touched.
/// </summary>
public sealed record PlatformSessionDeleteResultDto(
    int PublishRecordsDeleted,
    int CollectionRecordsDeleted
);

/// <summary>
/// Per-session probe result.
/// </summary>
public sealed record ProbeResultDto(
    Guid Id,
    int Status,
    string? ProbedResult
);

// ═══ Request DTOs ═══

/// <summary>
/// Sync (login / relogin) request from desktop after WebView2 cookie extraction.
/// id == null → insert new session; id != null → update existing.
/// </summary>
public sealed record SyncPlatformSessionRequest(
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
/// Rename request.
/// </summary>
public sealed record RenamePlatformSessionRequest(
    Guid Id,
    string? AccountLabel
);

/// <summary>
/// Set-primary request.
/// </summary>
public sealed record SetPrimaryRequest(
    Guid Id
);

/// <summary>
/// Delete request.
/// </summary>
public sealed record DeletePlatformSessionRequest(
    Guid Id
);

/// <summary>
/// Probe request — probe all sessions of a given platform for the current member.
/// platform_key is optional; omit to probe all platforms.
/// </summary>
public sealed record ProbePlatformSessionsRequest(
    string? PlatformKey
);

/// <summary>
/// Response DTO containing the full session credentials needed for desktop-side SDK calls.
/// Includes cookie and extra data (csrfToken, msToken, a_bogus).
/// Only returned for authenticated requests from the session owner.
/// </summary>
public sealed record PlatformSessionCredentialsDto(
    Guid Id,
    string PlatformKey,
    string? Cookie,
    string? ExtraDataJson,          // JSON: { csrfToken, msToken, a_bogus, ... }
    string? LocalStorageJson,       // 完整 localStorage JSON（含 ECDSA 密钥等）
    string? PlatformUid,
    string? PlatformName
);

/// <summary>
/// Request DTO for fetching session credentials by session ID.
/// </summary>
public sealed record GetSessionCredentialsRequest(
    Guid SessionId
);
