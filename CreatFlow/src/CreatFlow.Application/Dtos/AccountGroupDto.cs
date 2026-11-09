namespace CreatFlow.Application.DTOs;

// ═══ Response DTOs ═══

/// <summary>
/// 一个账号分组（组名 + 成员会话 ID 数组，数组序 = 组内成员序）。
/// </summary>
public sealed record AccountGroupDto(
    Guid Id,
    string Name,
    List<Guid> SessionIds
);

// ═══ Request DTOs ═══

/// <summary>
/// 整表替换某会员的分组（桌面端每次改动推送全量;缺的按 ID diff 删除）。
/// </summary>
public sealed record SaveAccountGroupsRequest(
    List<SaveAccountGroupItem>? Groups
);

/// <summary>
/// 推送的单组（客户端生成的 GUID 为 ID;数组序 = sort_order）。
/// </summary>
public sealed record SaveAccountGroupItem(
    Guid Id,
    string? Name,
    List<Guid>? SessionIds
);
