namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for member account groups (账号分组)。
/// </summary>
public interface IAccountGroupRepository : IRepository<AccountGroup, AccountGroupId>
{
    /// <summary>
    /// Returns all groups of a member, ordered by sort_order (组条展示序).
    /// </summary>
    Task<List<AccountGroup>> GetByMemberAsync(Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// Returns rows matching the given IDs (包括他人的行 —— 供整表替换前的
    /// 「同 ID 属他人不越权占用」检查;一次查回,不循环)。
    /// </summary>
    Task<List<AccountGroup>> GetByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// 管理端分页列表（后台「账号分组」页）：按更新时间倒序；会员名一次批量解析。
    /// search 非空 = 组名 Contains（varchar，可翻译）；memberId 非空 = 只看该会员。
    /// </summary>
    Task<(List<AccountGroupAdminDto> Items, int Total)> GetAdminPagedAsync(
        string? search, Guid? memberId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Adds without saving — 与 <see cref="SaveChangesAsync"/> 合成一次事务
    /// （整表替换要求原子;不要改用 IRepository.AddAsync,那会逐行落库）。
    /// </summary>
    void Add(AccountGroup group);

    /// <summary>
    /// Removes without saving — 同 Add 的「不落库」口径
    /// （先例:CollectionPlatformRepository.DeleteByCollectionAsync）。
    /// </summary>
    void RemoveRange(IEnumerable<AccountGroup> groups);

    /// <summary>Saves all pending changes.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
