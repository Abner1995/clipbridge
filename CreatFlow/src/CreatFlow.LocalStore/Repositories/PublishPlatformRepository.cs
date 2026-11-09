namespace CreatFlow.LocalStore.Repositories;

public class PublishPlatformRepository(LocalDbContext _db) : IPublishPlatformRepository
{
    public async Task<PublishPlatform?> GetByIdAsync(PublishPlatformId id, CancellationToken ct = default)
        => await _db.Set<PublishPlatform>().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<List<PublishPlatform>> GetByTaskAsync(
        PublishTaskId taskId, CancellationToken ct = default)
        => await _db.Set<PublishPlatform>()
            .Where(p => p.PublishTaskId == taskId)
            .ToListAsync(ct);

    public async Task<PublishPlatform?> GetByTaskAndPlatformAsync(
        PublishTaskId taskId, string platformKey, Guid? sessionId, CancellationToken ct = default)
        => await _db.Set<PublishPlatform>()
            .FirstOrDefaultAsync(p =>
                p.PublishTaskId == taskId
                && p.PlatformKey == platformKey
                && p.PlatformSessionId == sessionId, ct);

    public async Task<List<PublishPlatform>> GetPendingAsync(CancellationToken ct = default)
        => await _db.Set<PublishPlatform>()
            .Where(p => p.Status == PublishPlatformStatus.Pending)
            .ToListAsync(ct);

    public async Task AddAsync(PublishPlatform aggregate, CancellationToken ct = default)
    {
        await _db.Set<PublishPlatform>().AddAsync(aggregate, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(PublishPlatform aggregate, CancellationToken ct = default)
    {
        _db.Set<PublishPlatform>().Update(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(PublishPlatform aggregate, CancellationToken ct = default)
    {
        _db.Set<PublishPlatform>().Remove(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(List<PublishPlatform> Items, int Total)> GetPagedByMemberAsync(
        Guid memberId, int page, int pageSize,
        PublishPlatformStatus? statusFilter = null,
        string? platformKey = null,
        string? keyword = null,
        CancellationToken ct = default)
    {
        var query = BuildMemberQuery(memberId, statusFilter, keyword);

        if (!string.IsNullOrWhiteSpace(platformKey))
            query = query.Where(pp => pp.PlatformKey == platformKey);

        // 稳定次序：同一次 submit 循环写下的 4~8 条记录 CreatedAt 常落在同一毫秒，
        // 只按时间排的话，同值组跨越页边界时 Skip/Take 会**重复或漏行**（归档页翻页会看到同一条两次）。
        query = query.OrderByDescending(pp => pp.CreatedAt).ThenByDescending(pp => pp.Id);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<Dictionary<string, int>> GetPlatformCountsAsync(
        Guid memberId,
        PublishPlatformStatus? statusFilter = null,
        string? keyword = null,
        CancellationToken ct = default)
    {
        var rows = await BuildMemberQuery(memberId, statusFilter, keyword)
            .GroupBy(pp => pp.PlatformKey)
            .Select(g => new { PlatformKey = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.PlatformKey, r => r.Count);
    }

    /// <summary>
    /// 「这些记录属于该会员」+ 状态过滤 + 关键词过滤的**共用基查询**。
    /// 列表查询与 facet 计数必须走同一份 —— 两处各写一遍 where 迟早漂移，
    /// 而漂移的后果是「计数说有 96 条、翻页只有 40 条」这种查不出原因的怪现象。
    /// </summary>
    private IQueryable<PublishPlatform> BuildMemberQuery(
        Guid memberId, PublishPlatformStatus? statusFilter, string? keyword)
    {
        // Subquery: find platform records whose parent task belongs to the member.
        var query = _db.Set<PublishPlatform>()
            .Where(pp => _db.Set<PublishTask>()
                .Any(pt => pt.Id == pp.PublishTaskId && pt.MemberId == memberId));

        if (statusFilter.HasValue)
            query = query.Where(pp => pp.Status == statusFilter.Value);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            // ⚠️ 权限敏感：**归属判定必须留在同一个 Any 里**。
            // 关键词匹配写在 `pt.MemberId == memberId` 之外（比如拆成两个 Where 用 OR 连接），
            // 就等于「只要标题沾边，别人的记录也能查出来」—— 归属过滤被 OR 旁路掉了。
            query = query.Where(pp =>
                (pp.Title != null && pp.Title.Contains(keyword))
                || _db.Set<PublishTask>().Any(pt =>
                    pt.Id == pp.PublishTaskId
                    && pt.MemberId == memberId
                    && ((pt.CommonTitle != null && pt.CommonTitle.Contains(keyword))
                        || pt.VideoFileName.Contains(keyword))));
        }

        return query;
    }

    public async Task DeleteByTaskAsync(PublishTaskId taskId, CancellationToken ct = default)
    {
        var platforms = await GetByTaskAsync(taskId, ct);
        _db.Set<PublishPlatform>().RemoveRange(platforms);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(int DeletedCount, List<PublishTaskId> AffectedTaskIds)> DeleteBySessionAsync(
        Guid sessionId, CancellationToken ct = default)
    {
        // 全部状态统一硬删（含 hidden_in_queue 软删行）—— 解绑语义：本端不留连不上的孤儿行。
        // PublishTaskId 是 record（值相等），内存 Distinct 安全；别把 Distinct 下推给带值转换器的列
        var rows = await _db.Set<PublishPlatform>()
            .Where(p => p.PlatformSessionId == sessionId)
            .ToListAsync(ct);

        var taskIds = rows.Select(p => p.PublishTaskId).Distinct().ToList();

        _db.Set<PublishPlatform>().RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        return (rows.Count, taskIds);
    }

    public async Task<(List<PublishPlatform> Items, int Total)> GetAllPagedAsync(
        int page, int pageSize,
        string? platformKey,
        Guid? memberId,
        PublishPlatformStatus? status,
        string? titleSearch,
        DateTime? dateFrom,
        DateTime? dateTo,
        CancellationToken ct = default)
    {
        var query = _db.Set<PublishPlatform>().AsQueryable();

        if (!string.IsNullOrWhiteSpace(platformKey))
            query = query.Where(pp => pp.PlatformKey == platformKey);

        if (status.HasValue)
            query = query.Where(pp => pp.Status == status.Value);

        // Member filter — JOIN with x_publish_tasks
        if (memberId.HasValue)
        {
            query = query.Where(pp => _db.Set<PublishTask>()
                .Any(pt => pt.Id == pp.PublishTaskId && pt.MemberId == memberId.Value));
        }

        // Title search — JOIN with x_publish_tasks
        if (!string.IsNullOrWhiteSpace(titleSearch))
        {
            query = query.Where(pp => _db.Set<PublishTask>()
                .Any(pt => pt.Id == pp.PublishTaskId
                    && (pt.CommonTitle != null && pt.CommonTitle.Contains(titleSearch)
                        || pt.VideoFileName.Contains(titleSearch))));
        }

        // Date range — JOIN with x_publish_tasks
        if (dateFrom.HasValue)
        {
            query = query.Where(pp => _db.Set<PublishTask>()
                .Any(pt => pt.Id == pp.PublishTaskId && pt.CreatedAt >= dateFrom.Value));
        }
        if (dateTo.HasValue)
        {
            var to = dateTo.Value.AddDays(1);
            query = query.Where(pp => _db.Set<PublishTask>()
                .Any(pt => pt.Id == pp.PublishTaskId && pt.CreatedAt < to));
        }

        query = query.OrderByDescending(pp => pp.CreatedAt);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
