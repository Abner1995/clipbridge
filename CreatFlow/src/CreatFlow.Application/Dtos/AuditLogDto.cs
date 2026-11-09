namespace CreatFlow.Application.DTOs;

/// <summary>
/// Read model for a single audit log row.
/// </summary>
public sealed record AuditLogDto(
    Guid Id,
    string? ActorUsername,
    string? MemberUsername,
    string? ActorType,
    string Action,
    string Detail,
    string? IpAddress,
    DateTime CreatedAt);

/// <summary>
/// Request for paginated audit log list with optional filters.
/// </summary>
public sealed record AuditLogListRequest(
    string? Action = null,
    string? Search = null,
    string? ActorType = null,
    string? ActorSearch = null,
    DateTime? DateFrom = null,
    DateTime? DateTo = null,
    int Page = 1,
    int PageSize = 20);
