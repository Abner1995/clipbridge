namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Repository for member compose-role sets (合成角色库，每会员一行)。
///
/// ⚠️ 值对象表达式树纪律（先例 AccountGroupRepository）：查询里整值对象比较，
/// 禁写 <c>x.Id.Value</c>（翻不了，整个接口 500）。本接口只有 Guid 列比较，天然安全。
/// </summary>
public interface IComposeRoleSetRepository : IRepository<ComposeRoleSet, ComposeRoleSetId>
{
    /// <summary>该会员的角色库行（每会员至多一行；无行 = 从未保存）。</summary>
    Task<ComposeRoleSet?> GetByMemberAsync(Guid memberId, CancellationToken ct = default);

    /// <summary>
    /// 管理端分页列表（后台「角色库」页）：按更新时间倒序；会员名一次批量解析
    /// （先例 ActivationCodeRepository.GetPagedCodesAsync，不循环查库）。
    /// memberId 非空 = 只看该会员（每会员至多一行 → 0/1 行）。
    /// </summary>
    Task<(List<ComposeRoleSetAdminDto> Items, int Total)> GetAdminPagedAsync(
        Guid? memberId, int page, int pageSize, CancellationToken ct = default);
}
