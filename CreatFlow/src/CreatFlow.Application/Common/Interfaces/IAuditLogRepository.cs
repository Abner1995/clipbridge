using CreatFlow.Application.DTOs;

namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Audit log repository — read-only from the management perspective.
/// Writes happen via the application service.
/// </summary>
public interface IAuditLogRepository
{
    Task<(List<AuditLogDto> Items, int TotalCount)> GetPagedAsync(
        AuditLogListRequest request, CancellationToken cancellationToken = default);

    Task AddAsync(AuditLog log, CancellationToken cancellationToken = default);

    Task<List<AuditLogDto>> GetRecentAsync(int count, CancellationToken cancellationToken = default);

    Task DeleteAsync(AuditLog log, CancellationToken cancellationToken = default);

    Task<AuditLog?> GetByIdAsync(AuditLogId id, CancellationToken cancellationToken = default);
}
