namespace CreatFlow.LocalStore.Repositories;

public class CollectionRepository(LocalDbContext _db) : ICollectionRepository
{
    public async Task<Collection?> GetByIdAsync(CollectionId id, CancellationToken ct = default)
        => await _db.Set<Collection>().FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<Collection?> GetByIdAndMemberAsync(
        CollectionId id, Guid memberId, CancellationToken ct = default)
        => await _db.Set<Collection>()
            .FirstOrDefaultAsync(c => c.Id == id && c.MemberId == memberId, ct);

    public async Task<List<Collection>> GetByMemberAsync(
        Guid memberId, CancellationToken ct = default)
        => await _db.Set<Collection>()
            .Where(c => c.MemberId == memberId)
            .OrderByDescending(c => c.LastModified)
            .ToListAsync(ct);

    public async Task AddAsync(Collection aggregate, CancellationToken ct = default)
    {
        await _db.Set<Collection>().AddAsync(aggregate, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Collection aggregate, CancellationToken ct = default)
    {
        _db.Set<Collection>().Update(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(List<Collection> Items, int Total)> GetAllPagedAsync(
        int page, int pageSize,
        string? titleSearch,
        Guid? memberId,
        DateTime? dateFrom,
        DateTime? dateTo,
        CancellationToken ct = default)
    {
        var query = _db.Set<Collection>().AsQueryable();

        if (!string.IsNullOrWhiteSpace(titleSearch))
            query = query.Where(c => c.Title.Contains(titleSearch));

        if (memberId.HasValue)
            query = query.Where(c => c.MemberId == memberId.Value);

        if (dateFrom.HasValue)
            query = query.Where(c => c.CreatedAt >= dateFrom.Value);

        if (dateTo.HasValue)
            query = query.Where(c => c.CreatedAt <= dateTo.Value.AddDays(1));

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(c => c.LastModified)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task DeleteAsync(Collection aggregate, CancellationToken ct = default)
    {
        _db.Set<Collection>().Remove(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
