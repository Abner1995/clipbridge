namespace CreatFlow.Application.Identity.Results;

/// <summary>
/// Heartbeat API response — lightweight announcement payload (not the full Admin DTO).
/// </summary>
public record HeartbeatAnnouncement(
    Guid Id,
    string Title,
    string Content,
    string Type,
    DateTime CreatedAt);

/// <summary>
/// Heartbeat API response for desktop clients.
/// </summary>
public record HeartbeatResult(
    bool Ok,
    bool IsDisabled,
    DateTime ExpireDate,
    List<HeartbeatAnnouncement>? Announcements = null,
    string? NewSecret = null,
    string? LatestVersion = null,
    string? DownloadUrl = null,
    string? ReleaseNotes = null);
