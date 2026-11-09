namespace CreatFlow.NewApp.Services;

/// <summary>
/// 账号分组的纯函数核心（2026-10-14 用户需求：发布页点组名一键只勾选该组）。
/// 自检直接钉这里 —— 集合语义（恰好相等 / 交集 / 空组）错一格，用户看到的就是「点了没反应」
/// 或「连带勾错账号」，而这种错在页面上很难一眼看出。
/// </summary>
public static class AccountGroups
{
    /// <summary>「全部账号」的哨兵组 ID（组条第 0 格；真实组 ID 是 Guid "N"，撞不上）
    /// —— 全部不是一个真分组（不落盘），只是组条上的一格。</summary>
    public const string AllGroupsId = "__all__";

    /// <summary>
    /// 组 ∩ 在册。**显示计数、点组勾选一律用它** —— 已解绑成员的悬空 Id 天然被忽略，
    /// 不需要（也不该）靠剪除来保证正确性（剪除只在平台页做，是管理动作）。
    /// </summary>
    public static HashSet<Guid> Members(AccountGroup group, IReadOnlyCollection<Guid> alive)
    {
        var aliveSet = alive as HashSet<Guid> ?? [.. alive];
        return [.. group.Sessions.Where(aliveSet.Contains)];
    }

    /// <summary>
    /// 当前勾选恰好等于组条上哪一格：全勾（芯片非空）→ <see cref="AllGroupsId"/>；
    /// 恰等某组 → 该组 Id；都不等 → null（部分勾选 / 空组 / 空芯片列表都不会命中）。
    /// 「恰好相等」= 集合相等（顺序无关）。
    /// </summary>
    public static string? ActiveGroupId(
        IReadOnlyList<AccountGroup> groups, IReadOnlyCollection<Guid> onIds,
        IReadOnlyCollection<Guid> allIds)
    {
        var all = allIds.ToHashSet();
        if (all.Count == 0) return null;

        var on = onIds.ToHashSet();
        if (on.SetEquals(all)) return AllGroupsId;

        foreach (var g in groups)
        {
            var members = Members(g, all);
            // 空组永不命中：全不勾时不该把某个碰巧也是空的组点亮
            if (members.Count > 0 && members.SetEquals(on)) return g.Id;
        }
        return null;
    }

    /// <summary>
    /// 组名校验：去首尾空格后非空、且除自己外不重名。返回错误文案，通过返回 null。
    /// **重名拒绝**（不是允许）：组条上按名字点，两个「宠物」用户无法区分 —— 真实误操作源。
    /// </summary>
    public static string? ValidateName(string? name, IEnumerable<AccountGroup> existing, string? selfId)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return "请填分组名称";
        if (existing.Any(g => g.Id != selfId &&
                              string.Equals(g.Name, trimmed, StringComparison.Ordinal)))
            return "已有同名分组，换一个名字";
        return null;
    }
}
