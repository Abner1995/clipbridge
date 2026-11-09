using CreatFlow.Application.DTOs;

namespace CreatFlow.Application.Identity.Services;

public class AnnouncementService
{
    private readonly IAnnouncementRepository _repo;
    private readonly IAdminRepository _adminRepo;

    public AnnouncementService(
        IAnnouncementRepository repo,
        IAdminRepository adminRepo)
    {
        _repo = repo;
        _adminRepo = adminRepo;
    }

    /// <summary>
    /// Creates an announcement. Domain event → AuditLog handled automatically.
    /// </summary>
    public async Task<AnnouncementDto> CreateAsync(
        SaveAnnouncementRequest request,
        string adminUsername,
        CancellationToken ct = default)
    {
        var admin = await _adminRepo.GetByUsernameAsync(adminUsername, ct)
            ?? throw new ValidationException($"管理员 '{adminUsername}' 不存在");

        var announcement = Announcement.Create(
            AnnouncementId.Of(Guid.NewGuid()),
            request.Title,
            request.Content,
            request.Type,
            admin.Id);

        await _repo.AddAsync(announcement, ct);
        return ToDto(announcement, admin.Username.Value);
    }

    /// <summary>
    /// Updates an announcement. Domain event → AuditLog handled automatically.
    /// </summary>
    public async Task UpdateAsync(Guid id, SaveAnnouncementRequest request, CancellationToken ct = default)
    {
        var announcement = await _repo.GetByIdAsync(AnnouncementId.Of(id), ct)
            ?? throw new NotFoundException("公告不存在");

        announcement.Edit(request.Title, request.Content, request.Type);
        await _repo.UpdateAsync(announcement, ct);
    }

    /// <summary>
    /// Toggles announcement active state. Domain event → AuditLog handled automatically.
    /// </summary>
    public async Task ToggleActiveAsync(Guid id, CancellationToken ct = default)
    {
        var announcement = await _repo.GetByIdAsync(AnnouncementId.Of(id), ct)
            ?? throw new NotFoundException("公告不存在");

        if (announcement.IsActive)
            announcement.Deactivate();
        else
            announcement.Activate();

        await _repo.UpdateAsync(announcement, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var announcement = await _repo.GetByIdAsync(AnnouncementId.Of(id), ct)
            ?? throw new NotFoundException("公告不存在");

        await _repo.DeleteAsync(announcement, ct);
    }

    private static AnnouncementDto ToDto(Announcement a, string creatorUsername) => new(
        Id: a.Id.Value,
        Title: a.Title,
        Content: a.Content,
        Type: a.Type,
        IsActive: a.IsActive,
        CreatedByUsername: creatorUsername,
        CreatedAt: a.CreatedAt ?? DateTime.MinValue);
}
