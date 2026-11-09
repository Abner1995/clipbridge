namespace CreatFlow.NewApp.Services;

/// <summary>
/// 账号分组的读写服务（DI 单例）：平台页编辑、发布页/合成页组条读取 —— 同一份内存，
/// 「在平台页改完切到发布页立刻可见」（页面进页刷新的既有契约）。
///
/// 与 <see cref="AccountChipsService"/> 分开（不并进去）：分组是**管理面**（平台页用），
/// 勾选是**使用面**；两边存储写者、刷新时机都不同。
///
/// 云端同步（2026-10-15 上云）：本服务只管本地读写 + 变更广播；推拉编排在
/// <see cref="AccountGroupCloudSync"/>（App 层把 <see cref="Changed"/> 接到静默推；自检不接线）。
///   · 本地每次变更置 Dirty + Revision++（Revision 供「推送落地时又改了一笔就不许清 dirty」守卫）；
///   · 云端替换/清 dirty 走**静默保存**（不触发 Changed —— 否则 推→拉→推 死循环）；
///   · 换会员（登出→登其他号，单例复用）由 EnsureLoadedAsync 比对 store.CurrentMemberId 重载。
/// 内存 + <c>_lastSave</c> 串行落盘的写法与 AccountChipsService 同款。
/// </summary>
public sealed class AccountGroupsService
{
    private readonly AccountGroupStore _store;
    private AccountGroupsData _data = new();
    private bool _loaded;
    private Guid _loadedMember;
    private int _revision;
    private Task _lastSave = Task.CompletedTask;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public AccountGroupsService(AccountGroupStore store) => _store = store;

    /// <summary>本地变更落盘后触发（App 层接线 → 静默推云端；自检/冒烟不接线）</summary>
    public event Action? Changed;

    /// <summary>有未推送云端的本地改动</summary>
    public bool IsDirty => _data.Dirty;

    /// <summary>变更计数（本地改动与云端替换都会 +1；推送清 dirty 前比对）</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>当前会员 ID（透传 store）</summary>
    public Guid CurrentMemberId => _store.CurrentMemberId;

    /// <summary>确保存档已载入（幂等；各页进页各调一次）。换会员时重载对应目录的档。</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded && _loadedMember == _store.CurrentMemberId) return;
        await _loadGate.WaitAsync(ct);
        try
        {
            if (_loaded && _loadedMember == _store.CurrentMemberId) return;
            // 先把上一会员的待写排空，否则排队那笔会落进新会员目录
            try { await _lastSave; } catch { /* 上次失败不阻断 */ }
            _data = await _store.LoadAsync(ct);
            _loadedMember = _store.CurrentMemberId;
            _loaded = true;
        }
        finally { _loadGate.Release(); }
    }

    /// <summary>内存快照（保存后即时反映）</summary>
    public IReadOnlyList<AccountGroup> Groups => _data.Groups;

    /// <summary>按 Id upsert 一个组（新增/改名/改成员都走它）—— 内存即时 + 落盘，写完才返回
    /// （页面保存后立即回读需要确定性）。</summary>
    public async Task SaveAsync(AccountGroup group, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var list = _data.Groups.ToList();
        var idx = list.FindIndex(g => g.Id == group.Id);
        if (idx >= 0) list[idx] = group; else list.Add(group);
        MarkDirty(list);
        QueueSave();
        await _lastSave;
    }

    /// <summary>删除一个组。只删分组定义 —— 不碰账号、不碰勾选存档、不碰已入队任务的快照。</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var list = _data.Groups.Where(g => g.Id != id).ToList();
        if (list.Count == _data.Groups.Count) return;
        MarkDirty(list);
        QueueSave();
        await _lastSave;
    }

    /// <summary>
    /// 剔掉已解绑的成员（组保留，哪怕空了 —— 用户起的名不替他丢）。
    /// 只在平台页「账号列表拉取成功」后调（那里才有权威在册集合）；无变化不落盘。
    /// </summary>
    public async Task PruneAsync(IReadOnlyCollection<Guid> alive, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        var aliveSet = alive.ToHashSet();
        var changed = false;
        var list = new List<AccountGroup>(_data.Groups.Count);
        foreach (var g in _data.Groups)
        {
            var members = g.Sessions.Where(aliveSet.Contains).Distinct().ToList();
            if (members.Count != g.Sessions.Count)
            {
                changed = true;
                list.Add(g with { Sessions = members });
            }
            else
            {
                list.Add(g);   // 没动过的组原样保留（引用不变）
            }
        }
        if (!changed) return;
        MarkDirty(list);
        QueueSave();
        await _lastSave;
        Log.Debug("[AccountGroups] 清理已解绑账号的分组遗骸");
    }

    // ═══ 云端同步入口（只给 AccountGroupCloudSync 用） ═══

    /// <summary>用云端列表整体替换本地（拉取落地；静默保存、清 dirty；**不触发 Changed**）。</summary>
    public async Task ReplaceFromCloudAsync(IReadOnlyList<AccountGroup> groups, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        _data = new AccountGroupsData { Groups = [.. groups], Dirty = false };
        Interlocked.Increment(ref _revision);
        QueueSaveSilently();
        await _lastSave;
    }

    /// <summary>推送成功后清 dirty（**revision 守卫**：推送期间用户又改了一笔 → 不清位、
    /// 下次再推 —— 否则那笔改动会永远到不了云端）。静默保存，不触发 Changed。</summary>
    public async Task ClearDirtyAsync(int revision, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        if (!_data.Dirty || Revision != revision) return;
        _data = _data with { Dirty = false };
        QueueSaveSilently();
        await _lastSave;
    }

    // ═══ 落盘链 ═══

    private void MarkDirty(List<AccountGroup> groups)
    {
        _data = _data with { Groups = groups, Dirty = true };
        Interlocked.Increment(ref _revision);
    }

    private void QueueSave()
    {
        var snapshot = _data;
        var member = _store.CurrentMemberId;   // 入队即锁定归属会员（换号瞬间排队那笔不写错目录）
        _lastSave = ChainSaveAsync(_lastSave, snapshot, member, raiseChanged: true);
    }

    private void QueueSaveSilently()
    {
        var snapshot = _data;
        var member = _store.CurrentMemberId;
        _lastSave = ChainSaveAsync(_lastSave, snapshot, member, raiseChanged: false);
    }

    private async Task ChainSaveAsync(Task prev, AccountGroupsData snapshot, Guid member, bool raiseChanged)
    {
        try { await prev; } catch { /* 上一次失败不阻断这次 */ }
        await _store.SaveAsync(snapshot, member);
        if (raiseChanged) Changed?.Invoke();
    }
}
