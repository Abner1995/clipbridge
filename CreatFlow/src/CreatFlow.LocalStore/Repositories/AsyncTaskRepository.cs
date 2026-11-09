namespace CreatFlow.LocalStore.Repositories;

public class AsyncTaskRepository(LocalDbContext _db) : IAsyncTaskRepository
{
    public async Task<AsyncTask?> GetByIdAsync(AsyncTaskId id, CancellationToken ct = default)
        => await _db.Set<AsyncTask>().FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<AsyncTask?> GetByIdAndMemberAsync(
        AsyncTaskId id, Guid memberId, CancellationToken ct = default)
        => await _db.Set<AsyncTask>()
            .FirstOrDefaultAsync(t => t.Id == id && t.MemberId == memberId, ct);

    public async Task<(List<AsyncTask> Items, int Total)> GetPagedByMemberAsync(
        Guid memberId, int page, int pageSize,
        AsyncTaskKind? kind = null, bool onlyUnsettled = false, bool onlyFailed = false,
        CancellationToken ct = default)
    {
        var query = _db.Set<AsyncTask>().Where(t => t.MemberId == memberId);

        if (kind.HasValue) query = query.Where(t => t.Kind == kind.Value);

        if (onlyUnsettled)
            query = query.Where(t => t.Status == AsyncTaskStatus.Pending
                                  || t.Status == AsyncTaskStatus.Running);

        // 按冗余的 failed_count 过滤，不 join 子项表 —— 计数是回报时同步维护的
        if (onlyFailed) query = query.Where(t => t.FailedCount > 0);

        var total = await query.CountAsync(ct);
        var items = await query
            // 双键排序：一批子项同毫秒写入是常态，只按时间排会让翻页重漏
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task AddAsync(AsyncTask aggregate, CancellationToken ct = default)
    {
        await _db.Set<AsyncTask>().AddAsync(aggregate, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(AsyncTask aggregate, CancellationToken ct = default)
    {
        _db.Set<AsyncTask>().Update(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(AsyncTask aggregate, CancellationToken ct = default)
    {
        _db.Set<AsyncTask>().Remove(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}
