namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Admin-specific repository. Extends the generic IRepository with
/// username lookup and paginated list for admin management.
/// </summary>
public interface IAdminRepository : IRepository<Admin, AdminId>
{
    Task<Admin?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);

    Task<(List<Admin> Items, int TotalCount)> GetPagedAsync(
        int page, int pageSize, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
