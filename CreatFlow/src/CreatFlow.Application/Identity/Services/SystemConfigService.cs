using CreatFlow.Application.DTOs;

namespace CreatFlow.Application.Identity.Services;

/// <summary>
/// Manages system configuration (email, upload limits, platform toggles, etc.).
/// Used by both the Blazor admin settings page and the desktop public config API.
/// </summary>
public class SystemConfigService
{
    private readonly ISystemConfigRepository _repo;

    public SystemConfigService(ISystemConfigRepository repo)
    {
        _repo = repo;
    }

    /// <summary>
    /// Returns all configs grouped by category (for the admin settings page).
    /// </summary>
    public async Task<Dictionary<string, List<SystemConfigDto>>> GetAllGroupedAsync(
        CancellationToken ct = default)
    {
        var list = await _repo.GetAllAsync(ct);
        return list
            .OrderBy(c => c.SortOrder)
            .GroupBy(c => c.Category)
            .ToDictionary(
                g => g.Key,
                g => g.Select(ToDto).ToList());
    }

    /// <summary>
    /// Returns only public configs (for desktop app startup).
    /// </summary>
    public async Task<PublicConfigDto> GetPublicAsync(CancellationToken ct = default)
    {
        var list = await _repo.GetPublicAsync(ct);

        var result = new PublicConfigDto(
            new Dictionary<string, object>(),
            new Dictionary<string, object>(),
            new Dictionary<string, object>());

        foreach (var cfg in list)
        {
            var dict = cfg.Category switch
            {
                "system" => result.System,
                "upload" => result.Upload,
                "contact" => result.Contact,
                _ => null
            };
            if (dict is null) continue;

            // Try parse as number / bool, fallback to string
            dict[cfg.Key] = ParseValue(cfg.Value, cfg.Editor);
        }

        return result;
    }

    /// <summary>
    /// Batch update config values (one request from the settings page).
    /// Only updates configs whose values actually changed.
    /// </summary>
    public async Task UpdateAsync(UpdateConfigsRequest request, CancellationToken ct = default)
    {
        foreach (var (key, newValue) in request.Configs)
        {
            var cfg = await _repo.GetByKeyAsync(key, ct);
            if (cfg is null) continue;
            if (cfg.Value == newValue) continue;  // no change → skip

            cfg.UpdateValue(newValue);
        }

        await _repo.SaveChangesAsync(ct);
    }

    private static SystemConfigDto ToDto(SystemConfig c) => new(
        Id: c.Id.Value,
        Key: c.Key,
        Value: c.Value,
        Category: c.Category,
        Label: c.Label,
        Editor: c.Editor,
        SortOrder: c.SortOrder,
        IsPublic: c.IsPublic);

    private static object ParseValue(string raw, string editor) => editor switch
    {
        "number" => double.TryParse(raw, out var d) ? d : raw,
        "switch" => raw is "true" or "1",
        "json" => raw, // desktop parses itself
        _ => raw
    };
}
