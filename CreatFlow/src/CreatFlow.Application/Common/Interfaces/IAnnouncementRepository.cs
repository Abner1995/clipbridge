using CreatFlow.Application.DTOs;

namespace CreatFlow.Application.Common.Interfaces;

public interface IAnnouncementRepository : IRepository<Announcement, AnnouncementId>
{
    Task<(List<AnnouncementDto> Items, int TotalCount)> GetPagedAsync(
        int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get active announcements for the heartbeat API (desktop clients).
    /// </summary>
    Task<List<Announcement>> GetActiveAsync(CancellationToken cancellationToken = default);
}
