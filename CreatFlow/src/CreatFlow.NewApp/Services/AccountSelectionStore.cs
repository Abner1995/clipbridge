namespace CreatFlow.NewApp.Services;

/// <summary>
/// 账号勾选 + 「合成完成后自动发布」开关的存档形状（一份文件两类紧邻状态）。
/// 开关没进 ComposeSettings：那份文件是配音页/合成页三处 Save 共写的已知雷区
/// （写漏字段就互相擦），新概念不往里塞。
/// </summary>
public sealed record AccountSelectionData
{
    /// <summary>合成完成后自动发布（默认关 —— 自动化的安全默认）</summary>
    public bool AutoPublish { get; init; }

    /// <summary>用户**显式关掉**的账号（会话 ID 串）。「全关」是合法选择，见类注释</summary>
    public List<Guid> OffSessions { get; init; } = [];
}

/// <summary>
/// 账号勾选（发布页 / 合成页「自动发布」共用的那份「上次勾选」）的本地持久化。
///
/// 存的是**显式关掉的 sessionId 集合**（与发布页原 <c>_chipsOff</c> 同一语义）：
/// 「全关」是一个合法选择（这批先不发），不能和「还没选过」混为一谈 ——
/// 存 on 集合的话，全关会被读回成「空 = 首次 = 全选」，用户关掉的账号自己又活了。
/// 文件缺失 / 损坏 → 默认（= 全选 + 开关关），不阻断进页面。
///
/// **会员级路径**（2026-10-15 批次 7）：accounts/{memberId:N}/account-selection.json ——
/// offSessions 是会员自己的会话 ID，机器级共享时另一会员进页剪除遗骸会把自己这份误删
/// （与账号分组修过的同一类 bug）。旧机器级档由升级后第一个登录的会员一次性采纳。
/// 与 <see cref="AccountOrderStore"/> 同规矩：纯交互偏好，不含敏感信息，换机器丢失代价可接受。
/// </summary>
public sealed class AccountSelectionStore
{
    private const string FileName = "account-selection.json";

    private readonly LocalJsonFile _io;
    private readonly Func<Guid> _memberId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="root">存储根（默认 %LocalAppData%\CreatFlow；冒烟传 TEMP 根）</param>
    /// <param name="memberId">当前会员提供者（DI 传 SessionContext；默认 Guid.Empty = no-member）</param>
    public AccountSelectionStore(string? root = null, Func<Guid>? memberId = null)
    {
        _io = new LocalJsonFile(root ?? LocalJsonFile.DefaultRoot, "AccountSelection");
        _memberId = memberId ?? (() => Guid.Empty);
    }

    /// <summary>当前会员 ID（服务层换号检测用）</summary>
    public Guid CurrentMemberId => _memberId();

    /// <summary>当前会员的存档路径（冒烟 TEMP 根守卫 / 测试用）</summary>
    public string GetConfigPath() => MemberPath(CurrentMemberId);

    /// <summary>旧版机器级存档路径（一次性采纳的来源；冒烟清档要一并清）</summary>
    public string GetLegacyPath() => _io.PathOf(FileName);

    private string MemberPath(Guid memberId) => _io.PathOf(LocalJsonFile.MemberRelative(memberId, FileName));

    /// <summary>读存档。文件缺失/损坏 → 默认（全选 + 开关关）。</summary>
    public async Task<AccountSelectionData> LoadAsync(CancellationToken ct = default)
    {
        var memberId = _memberId();   // 入口锁定：await 期间换号不影响本次读的归属
        await _gate.WaitAsync(ct);
        try
        {
            return await _io.ReadMemberOrAdoptAsync<AccountSelectionData>(
                MemberPath(memberId), GetLegacyPath(), memberId,
                d => d.AutoPublish || d.OffSessions.Count > 0, "AccountSelection", ct) ?? new();
        }
        finally { _gate.Release(); }
    }

    /// <summary>整份覆盖写（每次勾选变化/开关切换都调 —— 只有十几条，写盘开销可忽略）。
    /// memberId 显式锁定归属会员 —— 服务层入队时锁定，排队落盘期间换会员也不写错目录。</summary>
    public async Task SaveAsync(AccountSelectionData data, Guid? memberId = null, CancellationToken ct = default)
    {
        var path = MemberPath(memberId ?? _memberId());
        await _gate.WaitAsync(ct);
        try { await _io.WriteAsync(path, data, ct); }
        finally { _gate.Release(); }
    }
}
