using Microsoft.EntityFrameworkCore;

namespace CreatFlow.LocalStore.Repositories;

public class PlatformSessionRepository(LocalDbContext _db) : IPlatformSessionRepository
{
    public async Task<PlatformSession?> GetByIdAsync(PlatformSessionId id, CancellationToken ct = default)
        => await _db.Set<PlatformSession>().FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<PlatformSession?> GetByIdAndMemberAsync(
        PlatformSessionId id, Guid memberId, CancellationToken ct = default)
        => await _db.Set<PlatformSession>()
            .FirstOrDefaultAsync(s => s.Id == id && s.MemberId == memberId, ct);

    public async Task<List<PlatformSession>> GetByMemberAsync(
        Guid memberId, string? platformKey = null, CancellationToken ct = default)
    {
        var query = _db.Set<PlatformSession>().Where(s => s.MemberId == memberId);
        if (!string.IsNullOrWhiteSpace(platformKey))
            query = query.Where(s => s.PlatformKey == platformKey);
        return await query.OrderByDescending(s => s.IsPrimary)
                          .ThenByDescending(s => s.LoggedInAt)
                          .ToListAsync(ct);
    }

    public async Task ClearPrimaryAsync(
        Guid memberId, string platformKey, PlatformSessionId? excludeId, CancellationToken ct = default)
    {
        var sessions = await _db.Set<PlatformSession>()
            .Where(s => s.MemberId == memberId
                     && s.PlatformKey == platformKey
                     && s.IsPrimary
                     && (excludeId == null || s.Id != excludeId))
            .ToListAsync(ct);

        foreach (var session in sessions)
            session.SetPrimary(false);
    }

    public async Task AddAsync(PlatformSession aggregate, CancellationToken ct = default)
    {
        await _db.Set<PlatformSession>().AddAsync(aggregate, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(PlatformSession aggregate, CancellationToken ct = default)
    {
        _db.Set<PlatformSession>().Update(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(PlatformSession aggregate, CancellationToken ct = default)
    {
        _db.Set<PlatformSession>().Remove(aggregate);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(List<PlatformSession> Items, int Total)> GetPagedAsync(
        string? search, int page, int pageSize, Guid? memberId = null, CancellationToken ct = default)
    {
        var query = _db.Set<PlatformSession>().AsQueryable();

        if (memberId.HasValue)
            query = query.Where(x => x.MemberId == memberId.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(x =>
                x.PlatformKey.Contains(s) ||
                (x.PlatformName != null && x.PlatformName.Contains(s)) ||
                (x.AccountLabel != null && x.AccountLabel.Contains(s)));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.Status == PlatformSessionStatus.Active)
            .ThenByDescending(x => x.ProbedAt)
            .ThenByDescending(x => x.LoggedInAt)
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
