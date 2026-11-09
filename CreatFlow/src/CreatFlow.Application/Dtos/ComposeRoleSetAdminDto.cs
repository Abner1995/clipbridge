namespace CreatFlow.Application.DTOs;

/// <summary>
/// 后台「角色库」列表行（x_compose_role_sets，每会员一行）。
/// RolesJson 原样带出 —— json 列不进 EF 表达式（不能过滤/排序/比较）；
/// 角色数与角色详情由页面层解析（口径 = ComposeRoleSetService.JsonOpts，camelCase）。
/// 与桌面端 API 的 ComposeRoleSetDto 区分，勿混用。
/// </summary>
public sealed record ComposeRoleSetAdminDto(
    Guid Id,
    Guid MemberId,
    string MemberUsername,
    string RolesJson,
    DateTime? CreatedAt,
    DateTime? LastModified);
