namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for async tasks (batch delete and friends).
/// </summary>
public interface IAsyncTaskRepository : IRepository<AsyncTask, AsyncTaskId>
{
    Task<AsyncTask?> GetByIdAndMemberAsync(
        AsyncTaskId id, Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// Paginated task list for one member, newest first.
    /// <paramref name="onlyUnsettled"/> = true 时只返回未完成任务；
    /// <paramref name="onlyFailed"/> = true 时只返回「有失败条目」的。
    /// **两个筛选都在服务端做** —— 客户端过滤只作用于当前页，会让「共 N 条」说谎。
    /// </summary>
    Task<(List<AsyncTask> Items, int Total)> GetPagedByMemberAsync(
        Guid memberId, int page, int pageSize,
        AsyncTaskKind? kind = null, bool onlyUnsettled = false, bool onlyFailed = false,
        CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
