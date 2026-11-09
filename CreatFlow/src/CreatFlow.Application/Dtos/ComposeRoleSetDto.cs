namespace CreatFlow.Application.DTOs;

// ═══ Response DTOs ═══

/// <summary>一条角色（Id 为任意字符串：builtin-*/Guid；数组序 = 角色库展示序）。</summary>
public sealed record ComposeRoleItemDto(
    string Id,
    string? Name,
    string? Template,
    string? Persona,
    string? Knowledge
);

/// <summary>
/// 会员的角色库。Exists 区分「从未保存过」（false，客户端保留内置示例）
/// 与「保存过（含显式空表）」（true，客户端整表落地）。
/// </summary>
public sealed record ComposeRoleSetDto(
    bool Exists,
    List<ComposeRoleItemDto> Roles
);

// ═══ Request DTOs ═══

/// <summary>整表替换某会员的角色库（桌面端每次改动推送全量；null ≠ 清库，清库发 "roles":[]）。</summary>
public sealed record SaveComposeRolesRequest(
    List<ComposeRoleItemDto>? Roles
);
