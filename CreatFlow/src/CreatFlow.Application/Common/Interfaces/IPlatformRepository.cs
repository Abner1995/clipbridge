namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for platform configuration entries.
/// </summary>
public interface IPlatformRepository : IRepository<Platform, PlatformId>
{
    /// <summary>
    /// Returns all platforms ordered by sort_order.
    /// </summary>
    Task<List<Platform>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns only enabled platforms ordered by sort_order.
    /// </summary>
    Task<List<Platform>> GetEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a platform by its key (e.g., "douyin").
    /// </summary>
    Task<Platform?> GetByKeyAsync(string platformKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves all pending changes.
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
