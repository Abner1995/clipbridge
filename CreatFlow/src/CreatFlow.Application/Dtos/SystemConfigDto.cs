namespace CreatFlow.Application.DTOs;

/// <summary>
/// Read model for a system config row in the settings page.
/// </summary>
public sealed record SystemConfigDto(
    Guid Id,
    string Key,
    string Value,
    string Category,
    string? Label,
    string Editor,
    int SortOrder,
    bool IsPublic);

/// <summary>
/// Request to update one or more config values.
/// Key = config key, Value = new value.
/// </summary>
public sealed record UpdateConfigsRequest(
    Dictionary<string, string> Configs);

/// <summary>
/// Public config returned to desktop app (no auth required).
/// Returns only IsPublic == true entries, flattened by category.
/// </summary>
public sealed record PublicConfigDto(
    Dictionary<string, object> System,
    Dictionary<string, object> Upload,
    Dictionary<string, object> Contact);
