namespace CreatFlow.NewApp.Services;

/// <summary>
/// 角色库的读写服务（DI 单例，2026-10-15 批次 8 上云）：AI 设置页编辑、合成页 / 合成队列页读取
/// —— 同一份内存，「在 AI 设置页改完切到合成页立刻可见」（页面进页刷新的既有契约）。
///
/// 云端同步：本服务只管本地读写 + 变更广播；推拉编排在 <see cref="ComposeRoleCloudSync"/>
/// （App 层把 <see cref="Changed"/> 接到静默推；自检 / 冒烟不接线）。
///   · 本地每次变更置 Dirty + Revision++（Revision 供「推送落地时又改了一笔就不许清 dirty」守卫）；
///   · 云端替换 / 清 dirty 走**静默保存**（不触发 Changed —— 否则 推→拉→推 死循环）；
///   · 换会员（登出→登其他号，单例复用）由 EnsureLoadedAsync 比对 store.CurrentMemberId 重载。
/// 内存 + <c>_lastSave</c> 串行落盘的写法与 <see cref="AccountGroupsService"/> 同款。
///
/// ⚠️ 本服务全链路不写 ConfigureAwait(false)：Changed / 页面续体必须回 UI 线程
/// （RoleRows 是 ObservableCollection，只能在 UI 线程改）。
/// </summary>
public sealed class ComposeRolesService
{
    private readonly ComposeRoleStore _store;
    private ComposeRolesData _data = new();
    private bool _loaded;
    private Guid _loadedMember;
    private int _revision;
    private Task _lastSave = Task.CompletedTask;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public ComposeRolesService(ComposeRoleStore store) => _store = store;

    /// <summary>本地变更落盘后触发（App 层接线 → 静默推云端；自检 / 冒烟不接线）</summary>
    public event Action? Changed;

    /// <summary>有未推送云端的本地改动</summary>
    public bool IsDirty => _data.Dirty;

    /// <summary>变更计数（本地改动与云端替换都会 +1；推送清 dirty 前比对）</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>当前会员 ID（透传 store）</summary>
    public Guid CurrentMemberId => _store.CurrentMemberId;

    /// <summary>存档路径（AI 设置页「角色存档」显示 / 冒烟守卫透传）</summary>
    public string GetConfigPath() => _store.GetConfigPath();

    /// <summary>内存快照（未载入 = 空；页面先 EnsureLoadedAsync）</summary>
    public IReadOnlyList<ComposeRole> Roles => _data.Roles;

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

    /// <summary>整份保存（AI 设置页每次增删改都推全量）—— 内存即时 + 落盘，写完才返回
    /// （页面保存后立即回读需要确定性）。</summary>
    public async Task SaveAsync(IReadOnlyCollection<ComposeRole> roles, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        _data = _data with { Roles = [.. roles], Dirty = true };
        Interlocked.Increment(ref _revision);
        QueueSave();
        await _lastSave;
    }

    // ═══ 云端同步入口（只给 ComposeRoleCloudSync 用） ═══

    /// <summary>用云端列表整体替换本地（拉取落地；静默保存、清 dirty；**不触发 Changed**）。</summary>
    public async Task ReplaceFromCloudAsync(IReadOnlyList<ComposeRole> roles, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        _data = new ComposeRolesData { Roles = [.. roles], Dirty = false };
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

    private async Task ChainSaveAsync(Task prev, ComposeRolesData snapshot, Guid member, bool raiseChanged)
    {
        try { await prev; } catch { /* 上一次失败不阻断这次 */ }
        await _store.SaveAsync(snapshot, member);
        if (raiseChanged) Changed?.Invoke();
    }
}
