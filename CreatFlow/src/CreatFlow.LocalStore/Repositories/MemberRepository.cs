namespace CreatFlow.LocalStore.Repositories;

public class MemberRepository(LocalDbContext _db) : IMemberRepository
{
    public async Task<Member?> GetByIdAsync(MemberId id, CancellationToken cancellationToken = default)
        => await _db.Member.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<Member?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
        => await _db.Member.FirstOrDefaultAsync(x => x.Username == new Username(username), cancellationToken);

    public async Task AddAsync(Member aggregate, CancellationToken cancellationToken = default)
    {
        await _db.Member.AddAsync(aggregate, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Member aggregate, CancellationToken cancellationToken = default)
    {
        _db.Member.Update(aggregate);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Member aggregate, CancellationToken cancellationToken = default)
    {
        _db.Member.Remove(aggregate);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
        => await _db.Member.CountAsync(cancellationToken);

    public async Task<int> CountActiveAsync(CancellationToken cancellationToken = default)
        => await _db.Member.CountAsync(m => m.IsActive, cancellationToken);

    public async Task<int> CountCreatedSinceAsync(DateTime since, CancellationToken cancellationToken = default)
        => await _db.Member.CountAsync(m => m.CreatedAt >= since, cancellationToken);

    public async Task<int> CountExpiringWithinAsync(int days, CancellationToken cancellationToken = default)
    {
        var now = Clock.Now;
        var deadline = now.AddDays(days);
        return await _db.Member
            .CountAsync(m => m.IsActive && m.ExpireDate >= now && m.ExpireDate <= deadline, cancellationToken);
    }

    public async Task<List<Member>> GetExpiringWithinAsync(int days, CancellationToken cancellationToken = default)
    {
        var now = Clock.Now;
        var deadline = now.AddDays(days);
        return await _db.Member
            .Where(m => m.IsActive && m.ExpireDate >= now && m.ExpireDate <= deadline)
            .OrderBy(m => m.ExpireDate)
            .Take(20)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Member>> GetRecentlyExpiredAsync(int withinDays, CancellationToken cancellationToken = default)
    {
        var now = Clock.Now;
        var since = now.AddDays(-withinDays);
        return await _db.Member
            .Where(m => m.IsActive && m.ExpireDate < now && m.ExpireDate >= since)
            .OrderBy(m => m.ExpireDate)
            .Take(20)
            .ToListAsync(cancellationToken);
    }

    public async Task<(List<Member> Items, int TotalCount)> GetPagedAsync(
        string? search,
        MemberStatusFilter? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Member.AsQueryable();

        // Keyword search: username or phone number
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(m =>
                ((string)(object)m.Username).Contains(term) ||
                (m.PhoneNumber != null && ((string)(object)m.PhoneNumber).Contains(term)));
        }

        // Status filter
        var now = Clock.Now;
        query = status switch
        {
            MemberStatusFilter.Active => query.Where(m => m.IsActive && m.ExpireDate > now),
            MemberStatusFilter.Expired => query.Where(m => m.ExpireDate <= now),
            MemberStatusFilter.Disabled => query.Where(m => !m.IsActive),
            _ => query
        };

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }
}
