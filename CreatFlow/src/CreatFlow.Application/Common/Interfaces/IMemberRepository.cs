namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Member-specific repository. Extends the generic IRepository with
/// username lookup, dashboard statistics, and paginated search.
/// </summary>
public interface IMemberRepository : IRepository<Member, MemberId>
{
    Task<Member?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);
    Task<int> CountAsync(CancellationToken cancellationToken = default);
    Task<int> CountActiveAsync(CancellationToken cancellationToken = default);
    Task<int> CountCreatedSinceAsync(DateTime since, CancellationToken cancellationToken = default);
    Task<int> CountExpiringWithinAsync(int days, CancellationToken cancellationToken = default);
    Task<List<Member>> GetExpiringWithinAsync(int days, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询已过期但仍在宽限期内的活跃会员（用于每日过期通知）。
    /// WHERE is_active = 1 AND expire_date >= (today - withinDays) AND expire_date < today
    /// </summary>
    Task<List<Member>> GetRecentlyExpiredAsync(int withinDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Paginated member search with optional keyword (username/phone) and status filter.
    /// Returns items for the current page plus total matching count.
    /// </summary>
    Task<(List<Member> Items, int TotalCount)> GetPagedAsync(
        string? search,
        MemberStatusFilter? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Status filter for member list query.
/// </summary>
public enum MemberStatusFilter
{
    All,
    Active,
    Expired,
    Disabled
}
