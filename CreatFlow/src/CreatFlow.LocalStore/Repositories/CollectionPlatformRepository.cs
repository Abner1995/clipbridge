namespace CreatFlow.LocalStore.Repositories;

public class CollectionPlatformRepository(LocalDbContext _db) : ICollectionPlatformRepository
{
    public async Task<CollectionPlatform?> GetByIdAsync(
        CollectionPlatformId id, CancellationToken ct = default)
        => await _db.Set<CollectionPlatform>().FirstOrDefaultAsync(cp => cp.Id == id, ct);

    public async Task<List<CollectionPlatform>> GetByCollectionAsync(
        CollectionId collectionId, CancellationToken ct = default)
        => await _db.Set<CollectionPlatform>()
            .Where(cp => cp.CollectionId == collectionId)
            .OrderBy(cp => cp.PlatformKey)
            .ToListAsync(ct);

    public async Task<CollectionPlatform?> GetByCollectionAndPlatformAsync(
        CollectionId collectionId, string platformKey, CancellationToken ct = default)
        => await _db.Set<CollectionPlatform>()
            .FirstOrDefaultAsync(cp => cp.CollectionId == collectionId
                                    && cp.PlatformKey == platformKey, ct);

    public async Task DeleteByCollectionAsync(
        CollectionId collectionId, CancellationToken ct = default)
    {
        var records = await _db.Set<CollectionPlatform>()
            .Where(cp => cp.CollectionId == collectionId)
            .ToListAsync(ct);

        _db.Set<CollectionPlatform>().RemoveRange(records);
    }

    public async Task<int> DeleteBySessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        // 解绑语义：硬删该会话的合集记录（父合集行保留）。
        // 注意与 DeleteByCollectionAsync 不同 —— 这个方法被单独调用，必须自己 SaveChanges
        var rows = await _db.Set<CollectionPlatform>()
            .Where(cp => cp.PlatformSessionId == sessionId)
            .ToListAsync(ct);

        _db.Set<CollectionPlatform>().RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task AddAsync(CollectionPlatform aggregate, CancellationToken ct = default)
    {
        await _db.Set<CollectionPlatform>().AddAsync(aggregate, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(CollectionPlatform aggregate, CancellationToken ct = default)
    {
        _db.Set<CollectionPlatform>().Update(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(CollectionPlatform aggregate, CancellationToken ct = default)
    {
        _db.Set<CollectionPlatform>().Remove(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
