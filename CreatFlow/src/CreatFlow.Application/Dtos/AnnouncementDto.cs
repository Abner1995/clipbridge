namespace CreatFlow.Application.DTOs;

/// <summary>
/// Read model for a single announcement row.
/// </summary>
public sealed record AnnouncementDto(
    Guid Id,
    string Title,
    string Content,
    AnnouncementType Type,
    bool IsActive,
    string CreatedByUsername,
    DateTime CreatedAt);

/// <summary>
/// Request to create or update an announcement.
/// </summary>
public sealed record SaveAnnouncementRequest(
    string Title,
    string Content,
    AnnouncementType Type);

/// <summary>
/// Request for paginated announcement list.
/// </summary>
public sealed record AnnouncementListRequest(
    int Page = 1,
    int PageSize = 20);
