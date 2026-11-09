namespace CreatFlow.LocalStore.Repositories;

public class AsyncTaskItemRepository(LocalDbContext _db) : IAsyncTaskItemRepository
{
    public async Task<AsyncTaskItem?> GetByIdAsync(AsyncTaskItemId id, CancellationToken ct = default)
        => await _db.Set<AsyncTaskItem>().FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<List<AsyncTaskItem>> GetByTaskAsync(
        AsyncTaskId taskId, CancellationToken ct = default)
        => await _db.Set<AsyncTaskItem>()
            .Where(i => i.TaskId == taskId)
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .ToListAsync(ct);

    public async Task<List<AsyncTaskItem>> GetByTaskAndGroupAsync(
        AsyncTaskId taskId, Guid groupKey, CancellationToken ct = default)
        => await _db.Set<AsyncTaskItem>()
            .Where(i => i.TaskId == taskId && i.GroupKey == groupKey)
            .ToListAsync(ct);

    /// <summary>
    /// ⚠️ 归属判定**必须留在同一个 Any 里**（与 PublishPlatformRepository.BuildMemberQuery 同款）：
    /// 子项没有 member_id，只能靠父任务。拆成两个 Where 用 OR 连，
    /// 就等于「只要条件沾边，别人的子项也能读到」—— 归属过滤被旁路掉。
    /// </summary>
    private IQueryable<AsyncTaskItem> OwnedBy(Guid memberId) =>
        _db.Set<AsyncTaskItem>().Where(i => _db.Set<AsyncTask>()
            .Any(t => t.Id == i.TaskId && t.MemberId == memberId));

    public async Task<List<AsyncTaskItem>> GetByTaskForMemberAsync(
        AsyncTaskId taskId, Guid memberId, CancellationToken ct = default)
        => await OwnedBy(memberId)
            .Where(i => i.TaskId == taskId)
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .ToListAsync(ct);

    public async Task<List<AsyncTaskItem>> GetPendingByMemberAsync(
        Guid memberId, CancellationToken ct = default)
        => await OwnedBy(memberId)
            .Where(i => i.Status == AsyncTaskItemStatus.Pending)
            // 老的先跑：用户先后点的那几批按顺序完成
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .ToListAsync(ct);

    public async Task<List<Guid>> GetInFlightTargetIdsAsync(
        Guid memberId, CancellationToken ct = default)
        => await OwnedBy(memberId)
            // 只有 Pending 是「在飞」—— 子项没有 Running 态（见 AsyncTaskItemStatus 的注释）
            // 且只取**视频**目标：合集目标走 GetInFlightCollectionGroupKeysAsync（另一路字段），
            // 混进来会让视频页把合集的活儿也当成「正在删的行」。
            .Where(i => i.Status == AsyncTaskItemStatus.Pending
                     && i.TargetKind == AsyncTaskTargetKind.PublishPlatform)
            .Select(i => i.TargetId)
            .Distinct()
            .ToListAsync(ct);

    public async Task<List<Guid>> GetInFlightCollectionGroupKeysAsync(
        Guid memberId, CancellationToken ct = default)
        => await OwnedBy(memberId)
            .Where(i => i.Status == AsyncTaskItemStatus.Pending
                     && i.TargetKind == AsyncTaskTargetKind.CollectionPlatform)
            .Select(i => i.GroupKey)
            .Distinct()
            .ToListAsync(ct);

    public async Task<List<(Guid GroupKey, int FailedCount)>> GetFailedCollectionDeleteGroupsAsync(
        Guid memberId, CancellationToken ct = default)
    {
        // 失败三态显式列出 —— 不能在 EF 里调 AsyncTaskItemStatuses.IsFailed（方法翻不成 SQL）
        var rows = await OwnedBy(memberId)
            .Where(i => i.TargetKind == AsyncTaskTargetKind.CollectionPlatform
                     && (i.Status == AsyncTaskItemStatus.NeedLogin
                      || i.Status == AsyncTaskItemStatus.PlatformFailed
                      || i.Status == AsyncTaskItemStatus.RecordCleanupFailed))
            .GroupBy(i => i.GroupKey)
            .Select(g => new { GroupKey = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return rows.Select(r => (r.GroupKey, r.Count)).ToList();
    }

    public async Task AddAsync(AsyncTaskItem aggregate, CancellationToken ct = default)
    {
        await _db.Set<AsyncTaskItem>().AddAsync(aggregate, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(AsyncTaskItem aggregate, CancellationToken ct = default)
    {
        _db.Set<AsyncTaskItem>().Update(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(AsyncTaskItem aggregate, CancellationToken ct = default)
    {
        _db.Set<AsyncTaskItem>().Remove(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteByTaskAsync(AsyncTaskId taskId, CancellationToken ct = default)
    {
        var items = await GetByTaskAsync(taskId, ct);
        _db.Set<AsyncTaskItem>().RemoveRange(items);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteByTasksAsync(
        IReadOnlyList<AsyncTaskId> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return;

        // 值对象集合的 Contains：两边都是 AsyncTaskId，EF 会对整个属性套转换器（可以翻译）。
        // ⚠️ 别写成 taskIds.Contains(i.TaskId.Value) —— .Value 翻不了，见 ValueObjects 的注释
        var items = await _db.Set<AsyncTaskItem>()
            .Where(i => taskIds.Contains(i.TaskId))
            .ToListAsync(ct);

        _db.Set<AsyncTaskItem>().RemoveRange(items);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}
