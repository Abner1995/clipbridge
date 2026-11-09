namespace CreatFlow.LocalStore.Repositories;

public class PublishTaskRepository(LocalDbContext _db) : IPublishTaskRepository
{
    public async Task<PublishTask?> GetByIdAsync(PublishTaskId id, CancellationToken ct = default)
        => await _db.Set<PublishTask>().FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<PublishTask?> GetByIdAndMemberAsync(
        PublishTaskId id, Guid memberId, CancellationToken ct = default)
        => await _db.Set<PublishTask>()
            .FirstOrDefaultAsync(t => t.Id == id && t.MemberId == memberId, ct);

    public async Task<List<PublishTask>> GetByMemberAsync(
        Guid memberId, CancellationToken ct = default)
        => await _db.Set<PublishTask>()
            .Where(t => t.MemberId == memberId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(PublishTask aggregate, CancellationToken ct = default)
    {
        await _db.Set<PublishTask>().AddAsync(aggregate, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(PublishTask aggregate, CancellationToken ct = default)
    {
        _db.Set<PublishTask>().Update(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(List<PublishTask> Items, int Total)> GetAllPagedAsync(
        int page, int pageSize,
        string? titleSearch,
        Guid? memberId,
        PublishTaskStatus? status,
        DateTime? dateFrom,
        DateTime? dateTo,
        CancellationToken ct = default)
    {
        var query = _db.Set<PublishTask>().AsQueryable();

        if (!string.IsNullOrWhiteSpace(titleSearch))
            query = query.Where(t => t.CommonTitle != null && t.CommonTitle.Contains(titleSearch));

        if (memberId.HasValue)
            query = query.Where(t => t.MemberId == memberId.Value);

        if (status.HasValue)
            query = query.Where(t => t.Status == status.Value);

        if (dateFrom.HasValue)
            query = query.Where(t => t.CreatedAt >= dateFrom.Value);

        if (dateTo.HasValue)
            query = query.Where(t => t.CreatedAt <= dateTo.Value.AddDays(1));

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task DeleteAsync(PublishTask aggregate, CancellationToken ct = default)
    {
        _db.Set<PublishTask>().Remove(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
