namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for platform session entries.
/// </summary>
public interface IPlatformSessionRepository : IRepository<PlatformSession, PlatformSessionId>
{
    /// <summary>
    /// Returns all sessions for a member, optionally filtered by platform.
    /// </summary>
    Task<List<PlatformSession>> GetByMemberAsync(Guid memberId, string? platformKey = null, CancellationToken ct = default);

    /// <summary>
    /// Finds a specific session by ID and member (ownership check).
    /// </summary>
    Task<PlatformSession?> GetByIdAndMemberAsync(PlatformSessionId id, Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// Clears is_primary flag for all sessions of a member on a platform.
    /// Called before setting a new primary.
    /// </summary>
    Task ClearPrimaryAsync(Guid memberId, string platformKey, PlatformSessionId? excludeId, CancellationToken ct = default);

    /// <summary>
    /// Admin: paginated query across all members, with optional search filter.
    /// </summary>
    Task<(List<PlatformSession> Items, int Total)> GetPagedAsync(
        string? search, int page, int pageSize, Guid? memberId = null, CancellationToken ct = default);

    /// <summary>
    /// Saves all pending changes.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
