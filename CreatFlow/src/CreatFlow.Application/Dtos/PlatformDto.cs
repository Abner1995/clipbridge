namespace CreatFlow.Application.DTOs;

/// <summary>
/// Read model for a platform row in the admin management page.
/// </summary>
public sealed record PlatformDto(
    Guid Id,
    string PlatformKey,
    string DisplayName,
    string BrandColorHex,
    string LoginUrl,
    int EstimatedValidDays,
    bool IsEnabled,
    int SortOrder);

/// <summary>
/// Request to update platform details from the admin page.
/// </summary>
public sealed record UpdatePlatformRequest(
    string DisplayName,
    string BrandColorHex,
    string LoginUrl,
    int EstimatedValidDays,
    int SortOrder);

/// <summary>
/// Request to create a new platform from the admin page.
/// </summary>
public sealed record CreatePlatformRequest(
    string PlatformKey,
    string DisplayName,
    string BrandColorHex,
    string LoginUrl,
    int EstimatedValidDays,
    int SortOrder);

/// <summary>
/// Platform config returned to the desktop app (only enabled platforms, sorted).
/// Omits internal fields the desktop doesn't need (Id, IsEnabled, SortOrder).
/// </summary>
public sealed record PlatformConfigDto(
    string PlatformKey,
    string DisplayName,
    string BrandColorHex,
    string LoginUrl,
    int EstimatedValidDays);
