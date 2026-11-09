namespace CreatFlow.Application.DTOs;

/// <summary>
/// 后台「账号分组」列表行（x_account_groups，一行 = 一组）。
/// SessionIdsJson 原样带出（json 列不进 EF 表达式）；账号数解析在页面层
/// （口径 = AccountGroupService.ParseSessions，坏行按空组）。
/// 与桌面端 API 的 AccountGroupDto 区分，勿混用。
/// </summary>
public sealed record AccountGroupAdminDto(
    Guid Id,
    Guid MemberId,
    string MemberUsername,
    string Name,
    string? SessionIdsJson,
    int SortOrder,
    DateTime? CreatedAt,
    DateTime? LastModified);
