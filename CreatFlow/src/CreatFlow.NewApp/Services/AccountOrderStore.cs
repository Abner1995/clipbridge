namespace CreatFlow.NewApp.Services;

/// <summary>
/// 账号显示顺序的本地持久化 —— 一条**扁平**的 (平台 × 账号) 序列，见 <see cref="AccountOrder"/>。
///
/// **会员级路径**（2026-10-15 批次 7）：accounts/{memberId:N}/account-order.json ——
/// key 里含会员自己的会话 ID，机器级共享时另一会员重排/「恢复默认」会整份覆盖掉这份
/// （保存写全量序列）。旧机器级档由升级后第一个登录的会员一次性采纳。
///
/// 为什么落本地而不是搬到服务端 x_platform_sessions 加一列：这是纯显示偏好、不含敏感信息，
/// 换机器丢失的代价可接受，而服务端方案要付加列 + 迁移 + 端点 + 老 App 兼容四份成本。
/// 真要跨机器同步时，只需换掉这个类的实现，调用点（发布页/账号坞/平台页）一行都不用动。
/// </summary>
public sealed class AccountOrderStore
{
    private const string FileName = "account-order.json";

    private readonly LocalJsonFile _io;
    private readonly Func<Guid> _memberId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="root">存储根（默认 %LocalAppData%\CreatFlow；冒烟传 TEMP 根）</param>
    /// <param name="memberId">当前会员提供者（DI 传 SessionContext；默认 Guid.Empty = no-member）</param>
    public AccountOrderStore(string? root = null, Func<Guid>? memberId = null)
    {
        _io = new LocalJsonFile(root ?? LocalJsonFile.DefaultRoot, "AccountOrder");
        _memberId = memberId ?? (() => Guid.Empty);
    }

    /// <summary>当前会员 ID（服务层换号检测用）</summary>
    public Guid CurrentMemberId => _memberId();

    /// <summary>当前会员的存档路径（冒烟 TEMP 根守卫 / 测试用）</summary>
    public string GetConfigPath() => MemberPath(CurrentMemberId);

    /// <summary>旧版机器级存档路径（一次性采纳的来源；冒烟清档要一并清）</summary>
    public string GetLegacyPath() => _io.PathOf(FileName);

    private string MemberPath(Guid memberId) => _io.PathOf(LocalJsonFile.MemberRelative(memberId, FileName));

    /// <summary>读用户顺序。文件缺失/损坏 → 空列表（= 回落到默认序，不阻断进页面）。</summary>
    public Task<List<string>> LoadAsync(CancellationToken ct = default) => LoadForAsync(_memberId(), ct);

    /// <summary>整份覆盖写（每次上下移都调 —— 序列只有十几条，写盘开销可忽略）。</summary>
    public Task SaveAsync(IReadOnlyList<string> keys, Guid? memberId = null, CancellationToken ct = default)
        => SaveForAsync(memberId ?? _memberId(), keys, ct);

    /// <summary>
    /// 清掉已解绑账号的 key。
    ///
    /// 不裁剪的后果不只是"留个没用的字符串"：服务端 recreate 拿到同一 Guid 时
    /// （客户端先行生成 ID，重登/补建都会复用），新账号会**继承旧账号在列表里的位置** ——
    /// 用户看到的是"我刚加的号怎么排到中间去了"，且完全看不出原因。
    ///
    /// 入口锁定 member：load + save 必须是同一会员的档（两个 await 之间可能换号）。
    /// </summary>
    public async Task<int> PruneAsync(IReadOnlyCollection<Guid> activeSessionIds,
        Guid? memberId = null, CancellationToken ct = default)
    {
        var member = memberId ?? _memberId();
        var order = await LoadForAsync(member, ct);
        var alive = activeSessionIds.Select(id => id.ToString("D"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 格式不符的 key（没有 ':'、段数不对）一并当脏数据清掉
        var kept = order.Where(k => k.Split(':') is [_, var sid] && alive.Contains(sid)).ToList();
        var removed = order.Count - kept.Count;
        if (removed == 0) return 0;

        await SaveForAsync(member, kept, ct);
        Log.Debug("[AccountOrder] 清理已解绑账号的排序 {Count} 条", removed);
        return removed;
    }

    private async Task<List<string>> LoadForAsync(Guid memberId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await _io.ReadMemberOrAdoptAsync<List<string>>(
                MemberPath(memberId), GetLegacyPath(), memberId,
                l => l.Count > 0, "AccountOrder", ct) ?? [];
        }
        finally { _gate.Release(); }
    }

    private async Task SaveForAsync(Guid memberId, IReadOnlyList<string> keys, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await _io.WriteAsync(MemberPath(memberId), keys, ct); }
        finally { _gate.Release(); }
    }
}
