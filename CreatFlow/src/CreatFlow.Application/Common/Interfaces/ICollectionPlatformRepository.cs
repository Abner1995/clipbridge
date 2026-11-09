namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for per-platform collection records.
/// </summary>
public interface ICollectionPlatformRepository : IRepository<CollectionPlatform, CollectionPlatformId>
{
    /// <summary>
    /// Returns all platform records for a given collection.
    /// </summary>
    Task<List<CollectionPlatform>> GetByCollectionAsync(
        CollectionId collectionId, CancellationToken ct = default);

    /// <summary>
    /// Returns a specific platform record by collection + platform key.
    /// </summary>
    Task<CollectionPlatform?> GetByCollectionAndPlatformAsync(
        CollectionId collectionId, string platformKey, CancellationToken ct = default);

    /// <summary>
    /// Deletes all platform records belonging to a collection (cascade on collection delete).
    /// </summary>
    Task DeleteByCollectionAsync(CollectionId collectionId, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes every collection record of the given session (unbind cleanup).
    /// Parent collections are kept; records of other sessions are not affected.
    /// </summary>
    Task<int> DeleteBySessionAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Saves all pending changes.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
