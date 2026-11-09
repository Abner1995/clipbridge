namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for exception log CRUD operations.
/// </summary>
public interface IExceptionLogRepository
{
    Task AddAsync(ExceptionLog log, CancellationToken ct = default);
    Task<ExceptionLog?> GetByIdAsync(ExceptionLogId id, CancellationToken ct = default);
    Task UpdateAsync(ExceptionLog log, CancellationToken ct = default);
    Task<(List<ExceptionLog> Items, int TotalCount)> GetPagedAsync(
        ExceptionLogStatusFilter? status,
        string? platform,
        string? search,
        int page,
        int pageSize,
        CancellationToken ct = default);
    Task<Dictionary<ExceptionLogStatus, int>> CountByStatusAsync(CancellationToken ct = default);
}
