namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for per-platform publish records.
/// </summary>
public interface IPublishPlatformRepository : IRepository<PublishPlatform, PublishPlatformId>
{
    /// <summary>
    /// Returns all platform records for a given publish task.
    /// </summary>
    Task<List<PublishPlatform>> GetByTaskAsync(
        PublishTaskId taskId, CancellationToken ct = default);

    /// <summary>
    /// Returns a specific platform record by task + platform key + session.
    /// </summary>
    Task<PublishPlatform?> GetByTaskAndPlatformAsync(
        PublishTaskId taskId, string platformKey, Guid? sessionId, CancellationToken ct = default);

    /// <summary>
    /// Returns all pending/scheduled platform records (for Worker polling).
    /// </summary>
    Task<List<PublishPlatform>> GetPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns paginated platform records for a member (JOIN with x_publish_tasks).
    /// Optionally filtered by status (null = all, typically set to Published for video management),
    /// platform key, and a keyword matched against the platform title / task title / file name.
    /// Ordering is (CreatedAt desc, Id desc) — the second key keeps paging stable when a submit
    /// batch shares the same timestamp.
    /// </summary>
    Task<(List<PublishPlatform> Items, int Total)> GetPagedByMemberAsync(
        Guid memberId, int page, int pageSize,
        PublishPlatformStatus? statusFilter = null,
        string? platformKey = null,
        string? keyword = null,
        CancellationToken ct = default);

    /// <summary>
    /// Counts of a member's platform records per platform key, under the same
    /// status/keyword filters as <see cref="GetPagedByMemberAsync"/> (facets for the filter chips).
    /// The platform key filter is deliberately **not** applied here.
    /// </summary>
    Task<Dictionary<string, int>> GetPlatformCountsAsync(
        Guid memberId,
        PublishPlatformStatus? statusFilter = null,
        string? keyword = null,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes all platform records belonging to a task (cascade on task delete).
    /// </summary>
    Task DeleteByTaskAsync(PublishTaskId taskId, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes every platform record of the given session — ALL statuses,
    /// including soft-hidden (hidden_in_queue) rows. Returns the deleted count and
    /// the distinct task IDs the deletion touched (callers sweep tasks left with
    /// zero records). Unbind cleanup — the platform side is never touched.
    /// </summary>
    Task<(int DeletedCount, List<PublishTaskId> AffectedTaskIds)> DeleteBySessionAsync(
        Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Returns all platform records (admin view) with pagination and optional filters.
    /// </summary>
    Task<(List<PublishPlatform> Items, int Total)> GetAllPagedAsync(
        int page, int pageSize,
        string? platformKey,
        Guid? memberId,
        PublishPlatformStatus? status,
        string? titleSearch,
        DateTime? dateFrom,
        DateTime? dateTo,
        CancellationToken ct = default);

    /// <summary>
    /// Saves all pending changes.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
