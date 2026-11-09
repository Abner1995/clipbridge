namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for logical collections.
/// </summary>
public interface ICollectionRepository : IRepository<Collection, CollectionId>
{
    /// <summary>
    /// Returns all collections for a member, newest first.
    /// </summary>
    Task<List<Collection>> GetByMemberAsync(Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// Finds a specific collection by ID and member (ownership check).
    /// </summary>
    Task<Collection?> GetByIdAndMemberAsync(CollectionId id, Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// Returns all collections (admin view) with pagination and optional filters.
    /// </summary>
    Task<(List<Collection> Items, int Total)> GetAllPagedAsync(
        int page, int pageSize,
        string? titleSearch,
        Guid? memberId,
        DateTime? dateFrom,
        DateTime? dateTo,
        CancellationToken ct = default);

    /// <summary>
    /// Saves all pending changes.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
