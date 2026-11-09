namespace CreatFlow.NewApp.Services;

/// <summary>
/// 账号芯片（发布页 / 合成页「自动发布」共用）的装配与勾选存档读写。
///
/// 为什么抽成服务：两页展示的是**同一份「上次勾选」**（用户拍板：在哪边改都生效），
/// 各自抄一份装配逻辑的下场是勾选语义漂移。这里持有唯一的内存存档
/// （服务单例 → 两页读写同一份），落盘走 <see cref="AccountSelectionStore"/>（跨重启保留）。
///
/// **会员级 + 换号重载**（2026-10-15 批次 7）：单例跨登录存活，会员由
/// <see cref="AccountSelectionStore.CurrentMemberId"/> 现取 —— 换号后首次使用检测到
/// 属主变化即整份重载（抄 <see cref="AccountGroupsService"/> 的双检模式），
/// 落盘一律带**快照属主**（_loadedMember），排队写不会落进新会员的目录。
///
/// 勾选语义沿发布页老规矩：**存「显式关掉」的集合** —— 全关是合法选择，
/// 不能与「还没选过（= 全选）」混为一谈（见 <see cref="AccountSelectionStore"/> 注释）。
/// 「自动发布」开关与勾选同住一份存档：写盘是整份覆盖，同源读写不互擦。
/// </summary>
public sealed class AccountChipsService
{
    private readonly AccountSelectionStore _selection;
    private readonly AccountOrderStore _orderStore;

    private AccountSelectionData _data = new();
    private bool _loaded;
    private Guid _loadedMember;   // _data 的属主会员（换号检测 + 落盘锁定）
    private Task _lastSave = Task.CompletedTask;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    /// <param name="selection">勾选存档（会员级）</param>
    /// <param name="orderStore">账号顺序（DI 注入与页面共享的同一实例；测试可省 → 自建）</param>
    public AccountChipsService(AccountSelectionStore selection, AccountOrderStore? orderStore = null)
    {
        _selection = selection;
        _orderStore = orderStore ?? new();
    }

    /// <summary>「合成完成后自动发布」开关（内存缓存；进页前先 <c>await EnsureLoadedAsync</c> 一家）</summary>
    public bool AutoPublish => _data.AutoPublish;

    /// <summary>确保存档已载入（幂等；两页进页时各调一次）。换号后首次调用整份重载 ——
    /// 重载前先把上一会员的在途写排空（否则排队那笔会落进新会员目录）。</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded && _loadedMember == _selection.CurrentMemberId) return;
        await _loadGate.WaitAsync(ct);
        try
        {
            if (_loaded && _loadedMember == _selection.CurrentMemberId) return;
            try { await _lastSave; } catch { /* 上一次失败不阻断重载 */ }
            _data = await _selection.LoadAsync(ct);
            _loadedMember = _selection.CurrentMemberId;
            _loaded = true;
        }
        finally { _loadGate.Release(); }
    }

    /// <summary>
    /// 拉平台配置 + 已绑定会话，按账号顺序装配芯片（IsOn 来自共享勾选存档）。
    /// 返回 null = 接口失败（调用方保留现有列表）；返回空列表 = 确实没有绑定账号。
    /// </summary>
    public async Task<List<AccountChipViewModel>?> BuildAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);

        List<PlatformConfigItem> platforms;
        PlatformSessionListResponse? sessions;
        try
        {
            platforms = await ApiClient.GetAsync<List<PlatformConfigItem>>("/api/config/platforms") ?? [];
            sessions = await ApiClient.PostAsync<PlatformSessionListResponse>("/api/platform-sessions/list");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AccountChips] 加载账号失败（保留现有列表）");
            return null;
        }

        var brandOf = platforms.ToDictionary(p => p.PlatformKey, p => p.BrandColorHex,
            StringComparer.OrdinalIgnoreCase);
        var nameOf = platforms.ToDictionary(p => p.PlatformKey, p => p.DisplayName,
            StringComparer.OrdinalIgnoreCase);

        // 顺序：默认序（平台配置序 → 组内主号优先 → 登录时间）之上套用户自定义序。
        // **必须走 AccountOrder**，否则用户排好的顺序在账号坞/平台页与这里不一致。
        //
        // 2026-11-06 用户要求：B站/头条先放开勾选，用于立即联调编辑器（话题/活动/预设）。
        // ⚠️ 发布执行链路仍在阶段 4/5 —— 勾选后能编辑、能提交，但上传队列会明确报
        // 「平台 SDK 尚未实现」；阶段 4.5/5.4 接上执行分支后才是真正可发布。
        var userOrder = await _orderStore.LoadAsync(ct);
        var ordered = AccountOrder.Apply(
            AccountOrder.Default(sessions?.Sessions ?? [], platforms), userOrder)
            .ToList();

        var off = _data.OffSessions.ToHashSet();
        var chips = new List<AccountChipViewModel>(ordered.Count);
        foreach (var s in ordered)
        {
            var key = s.PlatformKey;
            var canPublish = PlatformNames.SupportsPublishing(key);
            chips.Add(new AccountChipViewModel
            {
                SessionId = s.Id,
                PlatformKey = key,
                PlatformName = nameOf.GetValueOrDefault(key, PlatformNames.GetDisplayName(key)),
                AccountLabel = string.IsNullOrWhiteSpace(s.AccountLabel)
                    ? (string.IsNullOrWhiteSpace(s.PlatformName) ? "未命名" : s.PlatformName!)
                    : s.AccountLabel!,
                BrandHex = brandOf.GetValueOrDefault(key, "#6C5CE7"),
                // 服务端 Status：0 正常 / 1 即将过期 / 2 已过期
                IsLoggedIn = s.Status != 2,
                CanPublish = canPublish,
                IsPrimary = s.IsPrimary,
                // 存档里的「关掉过」= 不勾；首次（空存档）全选。
                // 发布未接入的平台（B站/头条）永远不勾 —— 选择路径也跳过它们。
                IsOn = canPublish && !off.Contains(s.Id),
            });
        }
        return chips;
    }

    /// <summary>
    /// 芯片勾选变化时调（两页各自订阅自己的芯片集转进来）。
    /// 内存存档即时生效（另一页下次 BuildAsync 立刻可见），落盘串行排队 ——
    /// <c>_lastSave</c> 链保证「勾一下立刻切页再回来」时读到的是自己刚写的那份。
    /// </summary>
    public void RecordToggle(Guid sessionId, bool isOn)
    {
        // 未载入不记账：换号窗口里的孤儿回调不许把旧 _data 写进任何档
        //（现网路径上页面进页先 BuildAsync，属防御性守卫）
        if (!_loaded) return;

        var off = _data.OffSessions.Where(id => id != sessionId).ToList();
        if (!isOn) off.Add(sessionId);
        _data = _data with { OffSessions = off };
        QueueSave();
    }

    /// <summary>
    /// 整组勾选（2026-10-14 账号分组）：用 (on, all) 一次写定 OffSessions = all − on，
    /// 只落一次盘 —— 逐颗 RecordToggle 是 N 次整文件写。
    ///
    /// **调用契约**：页面侧先在**同步循环**里改芯片 IsOn（用抑制标志短路逐颗记账），
    /// 循环内不得 await，循环结束后再 await 本方法 —— 先读后写之间让出会让期间的
    /// 逐颗勾选被这一笔覆盖。只从 UI 线程调（两页命令都在 UI 线程，天然串行）。
    /// </summary>
    public async Task ReplaceSelectionAsync(
        IReadOnlyCollection<Guid> onIds, IReadOnlyCollection<Guid> allIds, CancellationToken ct = default)
    {
        var on = onIds.ToHashSet();
        var off = allIds.Distinct().Where(id => !on.Contains(id)).ToList();

        await EnsureLoadedAsync(ct);   // 正常路径此时已载入 → 同步返回，不产生让出窗口
        _data = _data with { OffSessions = off };
        QueueSave();
        await _lastSave;               // 写完才返回（冒烟/测试要「写完再读」的确定性）
    }

    /// <summary>切「合成完成后自动发布」开关（内存即时 + 落盘；写完才返回 —— 测试/批量确认
    /// 依赖「写完再读」的确定性）</summary>
    public async Task SetAutoPublishAsync(bool on, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        if (_data.AutoPublish == on) return;
        _data = _data with { AutoPublish = on };
        QueueSave();
        await _lastSave;
    }

    /// <summary>账号解绑后清掉勾选遗骸（在册账号之外的一律移出 off 集合）</summary>
    public async Task PruneAsync(IReadOnlyCollection<Guid> alive, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var aliveSet = alive.ToHashSet();
        var off = _data.OffSessions.Where(aliveSet.Contains).ToList();
        if (off.Count == _data.OffSessions.Count) return;

        _data = _data with { OffSessions = off };
        QueueSave();
        await _lastSave;
        Log.Debug("[AccountChips] 清理已解绑账号的勾选遗骸");
    }

    /// <summary>当前勾选的账号快照（批量确认计数 / 入队快照共用，顺序 = 芯片顺序）</summary>
    public async Task<List<AutoPublishTarget>> SelectedAsync(CancellationToken ct = default)
    {
        var chips = await BuildAsync(ct);
        if (chips is null) return [];
        return [.. chips.Where(c => c.IsOn && c.CanPublish)
            .Select(c => new AutoPublishTarget(c.PlatformKey, c.SessionId, c.AccountLabel, c.IsLoggedIn))];
    }

    private void QueueSave()
    {
        var snapshot = _data;
        var member = _loadedMember;   // 入队即锁定归属会员：排队落盘期间换号也不写错目录
        _lastSave = ChainSaveAsync(_lastSave, snapshot, member);
    }

    private async Task ChainSaveAsync(Task prev, AccountSelectionData snapshot, Guid member)
    {
        try { await prev; } catch { /* 上一次失败不阻断这次 */ }
        await _selection.SaveAsync(snapshot, member);
    }
}
