namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for publish tasks.
/// </summary>
public interface IPublishTaskRepository : IRepository<PublishTask, PublishTaskId>
{
    /// <summary>
    /// Returns all publish tasks for a member, newest first.
    /// </summary>
    Task<List<PublishTask>> GetByMemberAsync(Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// Finds a specific task by ID and member (ownership check).
    /// </summary>
    Task<PublishTask?> GetByIdAndMemberAsync(PublishTaskId id, Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// Returns all tasks (admin view) with pagination and optional filters.
    /// </summary>
    Task<(List<PublishTask> Items, int Total)> GetAllPagedAsync(
        int page, int pageSize,
        string? titleSearch,
        Guid? memberId,
        PublishTaskStatus? status,
        DateTime? dateFrom,
        DateTime? dateTo,
        CancellationToken ct = default);

    /// <summary>
    /// Saves all pending changes.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
