using CreatFlow.Application.DTOs;

namespace CreatFlow.Application.Identity.Services;

/// <summary>
/// Manages platform configuration.
/// Used by both the Blazor admin platforms page and the desktop platform config API.
/// </summary>
public class PlatformService
{
    private readonly IPlatformRepository _repo;

    public PlatformService(IPlatformRepository repo)
    {
        _repo = repo;
    }

    /// <summary>
    /// Returns all platforms (for the admin management page).
    /// </summary>
    public async Task<List<PlatformDto>> GetAllAsync(CancellationToken ct = default)
    {
        var list = await _repo.GetAllAsync(ct);
        return list.Select(ToDto).ToList();
    }

    /// <summary>
    /// Returns only enabled platforms (for the desktop app).
    /// </summary>
    public async Task<List<PlatformConfigDto>> GetEnabledAsync(CancellationToken ct = default)
    {
        var list = await _repo.GetEnabledAsync(ct);
        return list.Select(p => new PlatformConfigDto(
            p.PlatformKey,
            p.DisplayName,
            p.BrandColorHex,
            p.LoginUrl,
            p.EstimatedValidDays)).ToList();
    }

    /// <summary>
    /// Finds a platform by its ID.
    /// </summary>
    public async Task<PlatformDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var platform = await _repo.GetByIdAsync(PlatformId.Of(id), ct);
        return platform is null ? null : ToDto(platform);
    }

    /// <summary>
    /// Updates platform config from the admin edit dialog.
    /// </summary>
    public async Task UpdateAsync(Guid id, UpdatePlatformRequest request, CancellationToken ct = default)
    {
        var platform = await _repo.GetByIdAsync(PlatformId.Of(id), ct)
            ?? throw new NotFoundException($"平台 {id} 不存在");

        platform.UpdateConfig(
            request.DisplayName,
            request.BrandColorHex,
            request.LoginUrl,
            request.EstimatedValidDays,
            request.SortOrder);

        await _repo.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Toggles platform enabled/disabled.
    /// </summary>
    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        var platform = await _repo.GetByIdAsync(PlatformId.Of(id), ct)
            ?? throw new NotFoundException($"平台 {id} 不存在");

        platform.SetEnabled(enabled);
        await _repo.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Creates a new platform.
    /// </summary>
    public async Task<PlatformDto> CreateAsync(CreatePlatformRequest request, CancellationToken ct = default)
    {
        var platform = Platform.Create(
            PlatformId.Of(Guid.NewGuid()),
            request.PlatformKey,
            request.DisplayName,
            request.BrandColorHex,
            request.LoginUrl,
            request.EstimatedValidDays,
            true,
            request.SortOrder);

        await _repo.AddAsync(platform, ct);
        return ToDto(platform);
    }

    /// <summary>
    /// Deletes a platform.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var platform = await _repo.GetByIdAsync(PlatformId.Of(id), ct)
            ?? throw new NotFoundException($"平台 {id} 不存在");

        await _repo.DeleteAsync(platform, ct);
    }

    private static PlatformDto ToDto(Platform p) => new(
        Id: p.Id.Value,
        PlatformKey: p.PlatformKey,
        DisplayName: p.DisplayName,
        BrandColorHex: p.BrandColorHex,
        LoginUrl: p.LoginUrl,
        EstimatedValidDays: p.EstimatedValidDays,
        IsEnabled: p.IsEnabled,
        SortOrder: p.SortOrder);
}
