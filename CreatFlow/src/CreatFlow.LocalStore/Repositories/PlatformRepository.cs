namespace CreatFlow.LocalStore.Repositories;

public class PlatformRepository(LocalDbContext _db) : IPlatformRepository
{
    public async Task<Platform?> GetByIdAsync(PlatformId id, CancellationToken ct = default)
        => await _db.Set<Platform>().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<Platform?> GetByKeyAsync(string platformKey, CancellationToken ct = default)
        => await _db.Set<Platform>().FirstOrDefaultAsync(p => p.PlatformKey == platformKey, ct);

    public async Task<List<Platform>> GetAllAsync(CancellationToken ct = default)
        => await _db.Set<Platform>()
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);

    public async Task<List<Platform>> GetEnabledAsync(CancellationToken ct = default)
        => await _db.Set<Platform>()
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);

    public async Task AddAsync(Platform aggregate, CancellationToken ct = default)
    {
        await _db.Set<Platform>().AddAsync(aggregate, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Platform aggregate, CancellationToken ct = default)
    {
        _db.Set<Platform>().Update(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Platform aggregate, CancellationToken ct = default)
    {
        _db.Set<Platform>().Remove(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
