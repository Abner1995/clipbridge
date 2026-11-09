namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for system config entries (key-value store).
/// </summary>
public interface ISystemConfigRepository : IRepository<SystemConfig, SystemConfigId>
{
    /// <summary>
    /// Returns all configs grouped by category, ordered by sort_order.
    /// </summary>
    Task<List<SystemConfig>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns only configs marked as public (exposed to desktop app).
    /// </summary>
    Task<List<SystemConfig>> GetPublicAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a config entry by its key.
    /// </summary>
    Task<SystemConfig?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves all pending changes. Used for batch updates in the settings page.
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
