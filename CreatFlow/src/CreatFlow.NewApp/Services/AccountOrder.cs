namespace CreatFlow.NewApp.Services;

/// <summary>
/// 账号显示顺序算法 —— 纯函数，不碰 IO（便于 <see cref="PublishSelfTest"/> 直接断言）。
///
/// **为什么不排"平台序 + 组内序"两层**：发布页的「发布到」芯片行、账号坞标签条本身
/// 就是扁平的，最后总要拍扁成一条序列；两层模型只是把拍扁推给调用方，还多一套概念。
/// 「整组移动」是 UI 层把一个平台的所有行整体 Move，不是数据模型里的一个概念 ——
/// 存储和算法都只认这一条扁平序列。
///
/// 排序的**唯一入口**：所有列账号的地方（发布页 / 账号坞 / 平台页）都必须过这里，
/// 否则就出现"我在排序页排好了，账号坞却没跟着变"。
/// </summary>
public static class AccountOrder
{
    /// <summary>账号在顺序表里的 key —— 与发布预设同构（缺了 Guid 格式差异会静默不命中）。</summary>
    public static string KeyOf(string platformKey, Guid sessionId)
        => PublishDraftStore.KeyOf(platformKey, sessionId);

    /// <summary>
    /// 默认顺序：平台配置序（= 后台平台的「排序」字段）→ 组内主号优先 → 组内登录时间倒序。
    ///
    /// 平台序直接取 <paramref name="platforms"/> 的**数组下标** —— GET /api/config/platforms
    /// 已由 PlatformRepository.GetEnabledAsync 按 SortOrder 排好返回，客户端不必再取一遍序号。
    /// 因此后台把「视频号」的排序值改小，全体客户端的默认序就跟着变，无需改代码。
    /// </summary>
    public static List<PlatformSessionItem> Default(
        IEnumerable<PlatformSessionItem> sessions, IReadOnlyList<PlatformConfigItem> platforms)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < platforms.Count; i++) rank[platforms[i].PlatformKey] = i;

        return [.. sessions
            .OrderBy(s => rank.GetValueOrDefault(s.PlatformKey, int.MaxValue))
            .ThenByDescending(s => s.IsPrimary)
            .ThenByDescending(s => s.LoggedInAt)];
    }

    /// <summary>
    /// 在默认序之上套用户自定义序。三条规则缺一条就会重新变乱：
    ///
    ///  1. **排过的** → 严格按用户序；
    ///  2. **没排过的（新登录的账号）→ 插到同平台那一组的末尾**，而不是简单追加到最后 ——
    ///     追加到最后会把「抖音组」劈成两半（老号在最前、新号在最后），等于又乱了。
    ///     只有该平台在序列里根本没出现过，才追加到最后；
    ///  3. **用户序里残留的已解绑 key** → 忽略（正常由 <see cref="AccountOrderStore.PruneAsync"/> 清掉，
    ///     这里兜底：万一裁剪没跑成，也不能让列表凭空少人）。
    ///
    /// <paramref name="defaults"/> 必须是 <see cref="Default"/> 的输出 —— 未排过的账号
    /// 按它给出的相对次序落位，否则同一组内新号的先后是随机的。
    /// </summary>
    public static List<PlatformSessionItem> Apply(
        IReadOnlyList<PlatformSessionItem> defaults, IReadOnlyList<string> userOrder)
    {
        if (userOrder.Count == 0) return [.. defaults];

        var byKey = new Dictionary<string, PlatformSessionItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in defaults) byKey[KeyOf(s.PlatformKey, s.Id)] = s;

        var result = new List<PlatformSessionItem>(defaults.Count);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in userOrder)
            if (byKey.TryGetValue(key, out var s) && placed.Add(key))
                result.Add(s);

        foreach (var s in defaults)
        {
            if (!placed.Add(KeyOf(s.PlatformKey, s.Id))) continue;

            // 找同平台已就位的最后一行，插到它后面（同平台多条未排的按默认序依次落位）
            var at = -1;
            for (var i = result.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(result[i].PlatformKey, s.PlatformKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                at = i;
                break;
            }

            if (at < 0) result.Add(s);
            else result.Insert(at + 1, s);
        }

        return result;
    }
}
