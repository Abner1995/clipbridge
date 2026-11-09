using System.ComponentModel;
using System.Diagnostics;
using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 合成队列页 ViewModel（DI 单例 = 页面 + 消费者同体，仿 UploadQueueViewModel）：
/// 串行执行合成任务（并发 1），任务快照落 compose_queue.json —— 关掉再开：排队中的续跑、
/// 合成中标「已中断」可重试。合成页单条合成与「从文件夹批量添加」都从这里入队，
/// 执行走 <see cref="ComposeRunner"/>，与页面状态解耦（关掉页面照跑）。
///
/// 许可门槛（用户规则：凡是功能页面，都必须登录且激活才能使用）：消费者领取任务前查一道，
/// 拦下就停摆（正在合成的那条跑完停住，排队中的留队）；退出登录 / 服务端撤销会员时
/// App 层直接调 <see cref="PauseBecause"/>，许可恢复后壳调 <see cref="Resume"/> 接续。
/// </summary>
public partial class ComposeQueueViewModel : ViewModelBase, INavigationAware
{
    private readonly ComposeQueueStore _store;
    private readonly ComposeSettingsStore _settings;
    private readonly WatermarkSettingsStore _watermark;
    private readonly SessionContext _session;
    private readonly ComposeRolesService _roleService;
    private readonly RoleOptimizer _roleOptimizer;
    private readonly AccountChipsService _chips;
    private readonly AccountGroupsService _groups;
    private readonly PublishUnitSubmitter _autoSubmitter;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _runCts;
    private bool _started;
    private bool _loaded;

    /// <summary>内存队列的**属主会员**（批次 7：落盘一律带它 —— 任务在换号挂起期间跑完也不写错目录）。
    /// 只在 UI 线程写（InitAsync 同步段 / 换装段）。</summary>
    private Guid _loadedMember;

    /// <summary>换号挂起目标（非 null = 已检出换会员、等空闲切换；UI 线程读写）。挂起期间不领新任务。</summary>
    private Guid? _targetMember;

    /// <summary>许可停摆原因（非 null = 不领取新任务）—— 只在内存里、不落盘：
    /// 配音队列的暂停是用户可点的持久态，这里的停摆由许可事件驱动（登出/撤权置、Resume 清）</summary>
    private string? _gateReason;

    public ComposeQueueViewModel(ComposeQueueStore store, ComposeSettingsStore settings,
        WatermarkSettingsStore watermark,
        SessionContext session, ComposeRolesService roleService, RoleOptimizer roleOptimizer,
        AccountChipsService chips, AccountGroupsService groups, PublishUnitSubmitter autoSubmitter)
    {
        _store = store;
        _settings = settings;
        _watermark = watermark;
        _session = session;
        _roleService = roleService;
        _roleOptimizer = roleOptimizer;
        _chips = chips;
        _groups = groups;
        _autoSubmitter = autoSubmitter;
        // 自动发布账号芯片（2026-10-15 批次 6 从合成页复制）：勾选变化 → 转报共享服务
        //（发布页/合成页读同一份，「在哪边改都生效」）+ 播报摘要
        TargetChips.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null) return;
            foreach (AccountChipViewModel chip in e.NewItems)
                chip.PropertyChanged += OnTargetChipPropertyChanged;
            RaiseAutoPublishSummary();
        };
        Tasks.CollectionChanged += (_, _) => NotifySummary();
    }

    /// <summary>跳「上传队列」/ 其他页 —— 页 VM 不认识壳，导航能力由壳注入（与合成页同范式）</summary>
    public Action<string>? OnNavigateRequested { get; set; }

    [RelayCommand]
    private void GoUploadQueue() => OnNavigateRequested?.Invoke("queue");

    /// <summary>进页加载的在飞任务（角色 / 账号芯片）</summary>
    private Task _navigatedTask = Task.CompletedTask;

    /// <summary>排干进页加载。给 <c>--smoke</c>：造景假芯片前必须等它落定，
    /// 否则晚到的芯片重建会把造景洗掉（同发布页/合成页）。永不抛。</summary>
    internal async Task DrainNavigatedAsync()
    {
        try { await _navigatedTask; } catch { /* 进页失败不该把调用方带崩 */ }
    }

    /// <summary>进页刷新角色列表（角色库可能刚在 AI 设置页改过）+ 账号芯片/开关
    ///（账号可能刚在平台页绑定/解绑；勾选走共享存档 —— 刷新会把开关回灌）</summary>
    public void OnNavigatedTo()
    {
        _navigatedTask = Task.WhenAll(RefreshRolesAsync(), RefreshTargetChipsAsync());
    }

    public MaterialIconKind HeaderIcon => MaterialIconKind.TrayFull;

    public ObservableCollection<ComposeTask> Tasks { get; } = [];

    /// <summary>统计变化回调（壳刷侧栏徽章）—— 都在 UI 线程触发</summary>
    public Action? OnStatsChanged { get; set; }

    /// <summary>批量入口的文件夹选择器（View 提供 StorageProvider；多选，可单选一个父目录）</summary>
    public Func<Task<IReadOnlyList<string>?>>? PickBatchFoldersAsync { get; set; }

    // ═══ 生命周期（壳开壳即启动） ═══

    public void Start()
    {
        // ⚠️ 冒烟只巡检：消费者会真跑合成（分钟级，且往输出目录写发布单元）——
        // 与上传队列同一道闸，必须挡在启动之前；冒烟里队列是静止的（入队可用，不执行）
        if (App.SmokeMode) return;

        if (!_started)
        {
            _started = true;
            _loopCts = new CancellationTokenSource();
            _ = InitAsync(_loopCts.Token);
            return;
        }

        // 壳每次打开（登录/自动登录/恢复重建）都会调 Start —— 非首次时检查换会员：
        // 检出后挂起，等在途任务收尾，由消费循环载入新会员的队列（批次 7）
        RequestMemberSwitchIfNeeded();
    }

    /// <summary>消费者是否已启动（冒烟断言用：冒烟里必须为 false）</summary>
    public bool IsConsumerStarted => _started;

    /// <summary>换号挂起中（页面预检用：挂起期间不能提交新任务 —— 入队会被拒）</summary>
    public bool IsMemberSwitchPending => _targetMember is not null;

    /// <summary>
    /// 换会员检测（<see cref="Start"/> 每次开壳调用，幂等）：
    /// 属主变了 → 挂起切换并踢一脚信号（空闲时立刻在消费循环落地）；
    /// A→B→A 快速来回 → 撤销挂起（属主其实没变）。
    /// </summary>
    private void RequestMemberSwitchIfNeeded()
    {
        if (!_loaded) return;   // 首载还没完成：InitAsync 会按当前会员装
        var current = _store.CurrentMemberId;
        if (current == _loadedMember)
        {
            _targetMember = null;   // 切回来了：撤销挂起
            return;
        }
        if (_targetMember == current) return;
        _targetMember = current;
        StatusNote = "已切换账号 —— 在途任务跑完后自动载入新账号的队列（期间不能提交新任务）";
        IsStatusError = false;
        _signal.Release();
    }

    /// <summary>
    /// 换号落地（消费循环每轮开头调）：空闲 → 排空在飞落盘 → 整档换装。
    /// 不空闲（当前那条还在跑/提交）→ 下轮再看 —— 与登出暂停同语义：「正在合成的那条跑完停住」，
    /// 收尾状态按属主锁定落的是老会员的档。
    /// </summary>
    private async Task TryCompleteMemberSwitchAsync()
    {
        var ctx = await Dispatcher.UIThread.InvokeAsync(() => (
            Target: _targetMember,
            Idle: _runCts is null
                  && Tasks.All(t => t.Status != ComposeTaskStatus.Running
                                 && t.AutoPublishState != AutoPublishState.Submitting)));
        if (ctx.Target is not { } target || !ctx.Idle) return;

        // 排空在飞落盘（此前每笔快照都带老会员 id，落的是老会员的档）
        await _saveLock.WaitAsync();
        _saveLock.Release();

        // 按挂起的**目标会员**显式载入（不用 CurrentMemberId —— 期间又换号也不漂移）
        var data = ComposeQueueStore.ApplyRestoreRules(await _store.LoadAsync(target));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_targetMember != target) return;   // 期间被撤销（或目标漂移）→ 放弃本次换装
            Tasks.Clear();
            foreach (var d in data)
            {
                var task = ComposeTask.FromData(d);
                Attach(task);
                Tasks.Add(task);
            }
            _loadedMember = target;
            _targetMember = null;
            if (StatusNote.StartsWith("已切换账号", StringComparison.Ordinal)) StatusNote = "";
        });
        _signal.Release();   // 新队列的待合成马上开跑
    }

    // ═══ 许可停摆 / 接续（App 层调用，规则：功能一律要登录且激活） ═══

    /// <summary>带原因的停摆（App 层调用：退出登录 / 服务端撤销会员）—— 原因显示在状态条。
    /// 与配音队列的暂停同姿势：正在合成的那条跑完停住，排队中的留在队里等恢复。</summary>
    public void PauseBecause(string reason)
    {
        _gateReason = reason;
        StatusNote = reason;
        IsStatusError = false;
    }

    /// <summary>清掉许可停摆并踢一脚（壳在会员生效 / 重进壳时调用；没有停摆时是空操作）</summary>
    public void Resume()
    {
        if (_gateReason is null) return;
        if (StatusNote == _gateReason) StatusNote = "";
        _gateReason = null;
        _signal.Release();
    }

    private async Task InitAsync(CancellationToken ct)
    {
        if (!_loaded)
        {
            _loaded = true;
            _loadedMember = _store.CurrentMemberId;   // ★ 同步先锁属主（第一个 await 之前）：本队列归它
            var data = ComposeQueueStore.ApplyRestoreRules(await _store.LoadAsync(_loadedMember, ct));
            foreach (var d in data)
            {
                var task = ComposeTask.FromData(d);
                Attach(task);
                Tasks.Add(task);
            }
        }
        _ = Task.Run(() => ConsumeLoopAsync(ct), ct);   // 消费循环丢线程池：重活在池里跑，UI 只收编组后的更新
        _signal.Release();                              // 启动踢一脚：待合成任务马上开跑
    }

    // ═══ 统计 / 徽章 ═══

    public int PendingCount => Tasks.Count(t => t.Status == ComposeTaskStatus.Pending);
    public int RunningCount => Tasks.Count(t => t.Status == ComposeTaskStatus.Running);
    public int DoneCount => Tasks.Count(t => t.Status == ComposeTaskStatus.Done);
    public int FailedCount => Tasks.Count(t => t.Status is ComposeTaskStatus.Failed or ComposeTaskStatus.Interrupted);

    /// <summary>徽章数 = 还没落地的（失败/中断也算，仿异步任务徽章的理由：都跑完但有条失败时徽章不能消失）</summary>
    public int BadgeCount => PendingCount + RunningCount + FailedCount;

    public bool HasTasks => Tasks.Count > 0;
    public bool IsEmpty => Tasks.Count == 0;

    public string SummaryText => Tasks.Count == 0
        ? "队列为空 —— 到「合成视频」点开始合成，或用上方「新建模板」/「从文件夹批量添加」"
        : $"共 {Tasks.Count} 条：待合成 {PendingCount} · 合成中 {RunningCount} · 完成 {DoneCount} · 失败 {FailedCount}";

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(BadgeCount));
        OnPropertyChanged(nameof(SummaryText));
        OnStatsChanged?.Invoke();
    }

    private void Attach(ComposeTask task) =>
        task.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ComposeTask.Status)) NotifySummary();
        };

    /// <summary>某任务前面还有几条在排（合成页提示「前面还有 N 条」用）</summary>
    public int PendingAheadOf(ComposeTask task) =>
        Tasks.TakeWhile(t => !ReferenceEquals(t, task)).Count(t => t.IsActive);

    // ═══ 批量角色 / AI 优化（2026-10-11 批次 4：只作用于「从文件夹批量添加」） ═══

    /// <summary>可选角色（首位哨兵「（不使用角色）」；进页刷新 + 批量入口懒加载兜底）</summary>
    [ObservableProperty]
    private IReadOnlyList<ComposeRole> _roles = [ComposeRole.None];

    [ObservableProperty]
    private ComposeRole? _selectedRole = ComposeRole.None;

    /// <summary>「用 AI 优化每条」（默认不勾；勾了在批量添加时逐条优化、失败回落原文）</summary>
    [ObservableProperty]
    private bool _useAiOptimize;

    public bool CanUseAiOptimize => ActiveRole is { HasPersona: true };

    partial void OnSelectedRoleChanged(ComposeRole? value)
    {
        OnPropertyChanged(nameof(SelectedRoleIndex));
        OnPropertyChanged(nameof(CanUseAiOptimize));
        if (!CanUseAiOptimize) UseAiOptimize = false;   // 角色没人设 → 勾选自动收起
    }

    /// <summary>角色下拉的 SelectedIndex 代理（列表会重建的组合框必须走它，见 2026-08 级联坑）</summary>
    public int SelectedRoleIndex
    {
        get
        {
            if (SelectedRole is null) return -1;
            for (var i = 0; i < Roles.Count; i++)
                if (Roles[i].Id == SelectedRole.Id) return i;
            return 0;
        }
        set
        {
            if (value >= 0 && value < Roles.Count) SelectedRole = Roles[value];
        }
    }

    private ComposeRole? ActiveRole => SelectedRole is { IsNone: false } r ? r : null;

    /// <summary>批量优化进度（只在进行中显示）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPickBatchFolder))]
    [NotifyCanExecuteChangedFor(nameof(PickBatchFolderCommand))]
    private bool _isBatchOptimizing;

    [ObservableProperty] private int _batchOptimizeProgress;
    [ObservableProperty] private string _batchOptimizeMessage = "";

    private CancellationTokenSource? _batchOptimizeCts;

    /// <summary>取消 = 跳过剩余的 AI 优化（已排队的照常添加、用原文）——不给"取消=整批放弃"的歧义</summary>
    [RelayCommand]
    private void CancelBatchOptimize() => _batchOptimizeCts?.Cancel();

    private bool _rolesInitialized;

    private async Task RefreshRolesAsync()
    {
        try
        {
            // 角色库走共享服务（批次 8 上云）：换会员自动重载；AI 设置页改完切过来即时可见
            await _roleService.EnsureLoadedAsync();
            var fresh = new List<ComposeRole> { ComposeRole.None };
            fresh.AddRange(_roleService.Roles);
            if (!Roles.SequenceEqual(fresh)) Roles = fresh;
            // 按 Id 找回当前选择；用户还没选过就用设置里记的角色（与合成页默认一致）
            var targetId = SelectedRole is { IsNone: false } r
                ? r.Id
                : !_rolesInitialized ? (await _settings.LoadAsync()).RoleId : null;
            var next = targetId is { Length: > 0 }
                ? fresh.FirstOrDefault(x => x.Id == targetId) ?? ComposeRole.None
                : ComposeRole.None;
            if (!Equals(SelectedRole, next)) SelectedRole = next;
            OnPropertyChanged(nameof(SelectedRoleIndex));
            _rolesInitialized = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ComposeQueue] 读取角色库失败");
        }
    }

    // ═══ 合成完成后自动发布（2026-10-15 批次 6：配置区从合成页复制到本页 ——
    //     开关与勾选走同一份共享存档（「在哪边改都生效」），在队列页干批量的人不必再跳页。
    //     批量链路本身（确认条/快照/PublishUnitSubmitter/状态轴）一概未动。逻辑照抄
    //     ComposeViewModel 同段，改一处要同步另一处；摘要文案是本页专版（标题 = 文件夹名）。）═══

    /// <summary>目标账号芯片（装配与勾选存档在 <see cref="AccountChipsService"/> —— 与发布页/合成页同一份）</summary>
    public ObservableCollection<AccountChipViewModel> TargetChips { get; } = [];

    /// <summary>「合成完成后自动发布」开关（存共享存档；进页时从服务载入）</summary>
    [ObservableProperty]
    private bool _autoPublish;

    partial void OnAutoPublishChanged(bool value)
    {
        _ = _chips.SetAutoPublishAsync(value);
        RaiseAutoPublishSummary();
    }

    public bool HasTargetChips => TargetChips.Count > 0;

    /// <summary>自动发布摘要（开关旁一行字）—— 本页专版：标题 = 文件夹名、批量恒为整批同一时间点；
    /// 开且没勾账号 = 红色警示（批量添加会跳过自动发布）</summary>
    public string AutoPublishSummary
    {
        get
        {
            if (!AutoPublish)
                return "关闭 —— 批量添加只合成，发布可在「发布视频」页导入发布";
            var on = TargetChips.Count(t => t.IsOn && t.CanPublish);
            if (on == 0)
                return "已开启但没有勾选任何账号 —— 勾一个账号，或关掉这个开关（批量添加会跳过自动发布）";
            var when = ScheduledAtOrNull is { } at ? $"定时 {at:yyyy-MM-dd HH:mm} 发布" : "立即发布";
            return $"将发布到 {on} 个账号（共 {on} 条平台记录）· {when} · 简介/话题等跟随发布单元与账号预设";
        }
    }

    /// <summary>红字警示：开了自动发布却一个账号都没勾（**含一个账号都没绑**的情形 —— 空列表全不勾也是真空）
    /// 与 <see cref="AutoPublishSummary"/> 的判据必须同源（on == 0），否则「文字在警告、颜色却是灰的」</summary>
    public bool IsAutoPublishSummaryError =>
        AutoPublish && TargetChips.Where(t => t.CanPublish).All(t => !t.IsOn);

    private void OnTargetChipPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AccountChipViewModel.IsOn)
            || sender is not AccountChipViewModel chip) return;

        // 一键勾选（组条）期间：逐颗记账由整批的 ReplaceSelectionAsync 接管 ——
        // 摘要/高亮也在批量结束后补一次（见 ApplyTargetGroupAsync）
        if (_applyingGroup) return;

        // 勾选进共享存档（发布页/合成页读同一份）；本页只播报摘要
        _chips.RecordToggle(chip.SessionId, chip.IsOn);
        RaiseAutoPublishSummary();
        RefreshActiveGroup();   // 手动微调后「哪一组正被应用」的高亮跟着走
    }

    // ═══ 账号分组组条（与发布页 / 合成页同款 —— 点一格 = 一键只勾该组；改一处要同步另外两处） ═══

    /// <summary>组条数据源（第 0 格恒为「全部」哨兵；没有真分组时整条隐藏）</summary>
    public ObservableCollection<AccountGroupChoiceViewModel> GroupChoices { get; } = [];

    /// <summary>有没有真分组（只有「全部」一格 = 没分组）</summary>
    public bool HasGroupChoices => GroupChoices.Count > 1;

    /// <summary>一键勾选进行中（循环内短路逐颗记账；循环结束后统一补摘要/高亮）</summary>
    private bool _applyingGroup;

    /// <summary>点一格：把勾选整体替换成「该格成员 / 全部」。所有芯片保持可见，可再手动微调。</summary>
    [RelayCommand]
    private async Task ApplyTargetGroupAsync(AccountGroupChoiceViewModel? choice)
    {
        if (choice is null || choice.IsEmpty) return;

        var all = TargetChips.Where(c => c.CanPublish).Select(c => c.SessionId).ToList();
        HashSet<Guid> on;
        if (choice.Id == AccountGroups.AllGroupsId)
        {
            on = [.. all];
        }
        else
        {
            var group = _groups.Groups.FirstOrDefault(g => g.Id == choice.Id);
            if (group is null) return;   // 组刚在平台页被删（组条还没刷新）——静默忽略
            on = AccountGroups.Members(group, all);
        }

        // 同步循环改 IsOn（循环内**不得 await** —— ReplaceSelectionAsync 的契约）
        _applyingGroup = true;
        try
        {
            foreach (var c in TargetChips.Where(c => c.CanPublish)) c.IsOn = on.Contains(c.SessionId);
        }
        finally { _applyingGroup = false; }

        await _chips.ReplaceSelectionAsync(on, all);
        RaiseAutoPublishSummary();   // 「将发布到 N 个账号」立刻跟着变
        RefreshActiveGroup();
    }

    /// <summary>组条重建（进页/账号芯片刷新时：分母与成员存活都变了）。空组也列出（灰着）。
    /// internal：冒烟特判直接调（离线时不会被 RefreshTargetChipsAsync 失败挡在半路）。</summary>
    internal async Task RefreshGroupChoicesAsync()
    {
        await _groups.EnsureLoadedAsync();
        var all = TargetChips.Where(c => c.CanPublish).Select(c => c.SessionId).ToList();

        GroupChoices.Clear();
        GroupChoices.Add(new AccountGroupChoiceViewModel
        {
            Id = AccountGroups.AllGroupsId,
            Name = "全部",
            Count = all.Count,
        });
        foreach (var g in _groups.Groups)
        {
            GroupChoices.Add(new AccountGroupChoiceViewModel
            {
                Id = g.Id,
                Name = g.Name,
                Count = AccountGroups.Members(g, all).Count,
            });
        }
        OnPropertyChanged(nameof(HasGroupChoices));
        RefreshActiveGroup();
    }

    /// <summary>高亮重算：**就地改属性**（批量勾选一次循环会触发 N 次 —— 重建集合会 N 次闪 UI）</summary>
    private void RefreshActiveGroup()
    {
        var all = TargetChips.Where(c => c.CanPublish).Select(c => c.SessionId).ToList();
        var on = TargetChips.Where(c => c.IsOn && c.CanPublish).Select(c => c.SessionId).ToList();
        var active = AccountGroups.ActiveGroupId(_groups.Groups, on, all);
        foreach (var ch in GroupChoices) ch.IsActive = ch.Id == active;
    }

    private void RaiseAutoPublishSummary()
    {
        OnPropertyChanged(nameof(AutoPublishSummary));
        OnPropertyChanged(nameof(IsAutoPublishSummaryError));
        OnPropertyChanged(nameof(HasTargetChips));
    }

    /// <summary>进页刷新账号芯片（勾选态来自共享存档；开关也回灌 —— 另一页可能刚改过）。
    /// internal：冒烟先 await 排干在飞刷新、再塞造景芯片（晚到的刷新会把造景洗掉）。</summary>
    internal async Task RefreshTargetChipsAsync()
    {
        try
        {
            await _chips.EnsureLoadedAsync();
            AutoPublish = _chips.AutoPublish;   // 另一页可能刚改过开关
            var chips = await _chips.BuildAsync();
            if (chips is null) return;          // 接口失败：保留现有列表

            TargetChips.Clear();
            foreach (var chip in chips) TargetChips.Add(chip);
            RaiseAutoPublishSummary();
            await RefreshGroupChoicesAsync();   // 组条的成员存活/分母都跟着这次刷新走
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ComposeQueue] 刷新自动发布账号失败");
        }
    }

    // ── 定时发布（批次级：整批同一个时间点，写入每条发布单元的「发布时间」；不勾 = 立即）──
    // 会话级、不落盘（与合成页同款：ComposeSettings 里本来就没有这三个字段）。
    // 校验口径与合成页 ValidateForCompose 完全一致，集中在 BatchScheduleError。

    /// <summary>定时发布（写入发布单元的「发布时间」；不勾 = 立即）</summary>
    [ObservableProperty]
    private bool _isScheduled;

    [ObservableProperty]
    private DateTimeOffset? _scheduledDate;

    [ObservableProperty]
    private TimeSpan? _scheduledTime;

    partial void OnIsScheduledChanged(bool value)
    {
        // 勾上时给个顺手的默认：明天 20:00（发布页定时窗口是 2 小时~14 天，明天 20 点稳在窗口内）
        if (value && ScheduledDate is null)
        {
            ScheduledDate = DateTime.Today.AddDays(1);
            ScheduledTime ??= new TimeSpan(20, 0, 0);
        }
        RaiseAutoPublishSummary();   // 摘要里的「立即发布 / 定时 …」跟着变
    }

    partial void OnScheduledDateChanged(DateTimeOffset? value) => RaiseAutoPublishSummary();
    partial void OnScheduledTimeChanged(TimeSpan? value) => RaiseAutoPublishSummary();

    /// <summary>定时时刻（勾了定时但日期/时间不全 → null，批量添加前会拦）</summary>
    public DateTime? ScheduledAtOrNull =>
        IsScheduled && ScheduledDate is { } d && ScheduledTime is { } t ? d.Date.Add(t) : null;

    /// <summary>
    /// 批量入口的定时校验（纯函数，自检钉住；口径与合成页 ValidateForCompose :1062-1082 一致）：
    /// ① 勾了定时但日期/时间不全 —— 永远拦；② 2h~14 天窗口 —— 只在「这批确实要自动发布」时拦
    ///（与合成页相同：不自动发布时定时只写单元参数，窗口由日后导入/提交时的预检响亮报错）。
    /// </summary>
    internal static string? BatchScheduleError(bool isScheduled, DateTime? at, bool autoPublish, DateTime now)
    {
        if (isScheduled && at is null)
            return "「定时发布」需要同时选日期和时间（或取消勾选）";
        if (autoPublish && at is { } t
            && PublishPreflight.ScheduleWindowError(t, now) is { } windowError)
            return "自动发布的" + windowError;
        return null;
    }

    // ═══ 入队 ═══

    /// <summary>合成页单条入队（入队即跑：队列空闲时消费者立刻捡起）。
    /// **换号挂起期间拒绝（返回 null）** —— 此刻入队会落进旧属主的档，等切换完成再交（批次 7）。</summary>
    public ComposeTask? Enqueue(ComposeJob job)
    {
        if (_targetMember is not null)
        {
            StatusNote = "账号切换中 —— 在途任务跑完后才能提交新任务";
            IsStatusError = false;
            return null;
        }

        var task = ComposeTask.FromJob(job);
        Attach(task);
        Tasks.Add(task);
        Persist();
        _signal.Release();
        return task;
    }

    /// <summary>从文件夹批量添加（页面按钮 / 冒烟造景共用）。返回扫描出的单元数。
    /// 选了角色 → 套台词模板（瞬时）；勾了「用 AI 优化每条」→ 逐条 AI 优化（**先优化裸文案、再套模板**，
    /// 单条失败回落原文、取消只跳过剩余）；**全部优化完成后才一次性入队** —— 消费者不会开跑半批。
    /// autoTargets = 批量确认条里定下的自动发布目标（null = 本批不自动发布）。
    /// scheduledAt = 批次定时快照（整批同一时间点；「仅添加」也照写 —— 定时是单元参数，与自动发布独立）。</summary>
    public async Task<int> AddFolderBatchAsync(IReadOnlyList<string> folders,
        IReadOnlyList<AutoPublishTarget>? autoTargets = null, DateTime? scheduledAt = null)
    {
        // 许可门槛（用户规则：凡是功能页面，都必须登录且激活才能使用）—— 页面开着、会员中途过期时兜住
        if (FeatureGate.BlockReason(_session.IsExpired, _session.Username, App.SmokeMode, "合成功能") is { } block)
        {
            StatusNote = block;
            IsStatusError = true;
            return 0;
        }

        // 换号挂起期间拒绝本批（此刻入队会落进旧属主的档，破坏两会员的档边界）
        if (_targetMember is not null)
        {
            StatusNote = "账号切换中 —— 在途任务跑完后才能提交新任务";
            IsStatusError = false;
            return 0;
        }

        // 定时兜底校验（PickBatchFolder 已有一道；这里护住冒烟/直接调用，以及确认条待决策期间
        // 时间滑出窗口的情形 —— 失败时不加任何任务）
        if (BatchScheduleError(IsScheduled, scheduledAt, autoTargets is { Count: > 0 }, DateTime.Now) is { } schedError)
        {
            StatusNote = schedError;
            IsStatusError = true;
            return 0;
        }

        var scan = ComposeFolderScanner.Scan(folders);
        var settings = await _settings.LoadAsync();
        // 水印偏好：批量入队时读一次，冻进每条任务快照（之后改设置不影响本批）
        var watermark = WatermarkSpec.Default;
        try { watermark = (await _watermark.LoadAsync()).ToSpec(); }
        catch (Exception ex) { Log.Warning(ex, "[ComposeQueue] 读水印设置失败，按默认开启"); }
        await RefreshRolesAsync();   // 兜底懒加载（冒烟直接调本方法，不依赖导航）
        var role = ActiveRole;       // 快照（防优化期间用户改选）

        var units = scan.Units.ToList();
        var optimized = 0;
        var optimizeFailed = 0;
        var optimizeSkipped = 0;
        string? firstFailReason = null;

        // ① 可选 AI 优化（逐条串行；只对「有文案」的单元 —— 自备配音的音频单元本来就没有文案）
        if (role is { HasPersona: true } && UseAiOptimize)
        {
            var candidates = Enumerable.Range(0, units.Count).Where(i => units[i].Text is not null).ToList();
            _batchOptimizeCts = new CancellationTokenSource();
            var ct = _batchOptimizeCts.Token;
            IsBatchOptimizing = true;
            try
            {
                for (var n = 0; n < candidates.Count; n++)
                {
                    var idx = candidates[n];
                    var unit = units[idx];
                    BatchOptimizeMessage = $"正在 AI 优化 {n + 1}/{candidates.Count}：{unit.Title}（首次使用本地引擎要加载模型，较慢）";
                    BatchOptimizeProgress = n * 100 / candidates.Count;
                    try
                    {
                        var result = await _roleOptimizer.OptimizeAsync(role.Persona, role.Knowledge, unit.Text!, ct);
                        units[idx] = unit with { Text = result };
                        optimized++;
                    }
                    catch (OperationCanceledException)
                    {
                        optimizeSkipped = candidates.Count - n;
                        break;
                    }
                    catch (Exception ex)
                    {
                        optimizeFailed++;
                        firstFailReason ??= ex.Message;
                    }
                }
            }
            finally
            {
                IsBatchOptimizing = false;
                BatchOptimizeProgress = 0;
                BatchOptimizeMessage = "";
                _batchOptimizeCts?.Dispose();
                _batchOptimizeCts = null;
            }
        }

        // ② 套台词模板（瞬时；只对有文案的单元 —— ApplyToUnit 对 Text=null 原样返回）
        if (role is not null)
            for (var i = 0; i < units.Count; i++)
                units[i] = ComposeRoleTemplate.ApplyToUnit(units[i], role);

        foreach (var unit in units)
        {
            var task = ComposeTask.FromFolder(unit, settings, role?.Name, autoTargets, scheduledAt, watermark);
            Attach(task);
            Tasks.Add(task);
        }
        Persist();
        if (units.Count > 0) _signal.Release();

        StatusNote = scan.Units.Count == 0
            ? "没有生成任何任务（原因见下）"
            : $"已加入 {scan.Units.Count} 条";
        if (autoTargets is { Count: > 0 } && scan.Units.Count > 0)
            StatusNote += $"；将在合成后自动发布到 {autoTargets.Count} 个账号"
                + (scheduledAt is { } at ? $"（定时 {at:yyyy-MM-dd HH:mm}）" : "");
        if (role is not null && scan.Units.Count > 0)
        {
            var noText = scan.Units.Count(u => u.Text is null);
            StatusNote += $"；已套用角色「{role.Name}」的台词模板"
                + (noText > 0 ? $"（{noText} 条无文案·自备配音，未套）" : "");
        }
        if (optimized > 0 || optimizeFailed > 0 || optimizeSkipped > 0)
        {
            StatusNote += $"；AI 优化 {optimized} 条";
            if (optimizeFailed > 0) StatusNote += $"，{optimizeFailed} 条失败已用原文（{firstFailReason}）";
            if (optimizeSkipped > 0) StatusNote += $"，已跳过剩余 {optimizeSkipped} 条的 AI 优化（用原文）";
        }
        // 音频被「有 资料.txt」规则收编成背景乐的，明确说出来（2026-10-11 用户实测：静默收编/顶替最容易懵）
        var asBgm = scan.Units.Count(u => u.AudioAsBgm);
        if (asBgm > 0)
            StatusNote += $"；{asBgm} 条把文件夹里的音频当背景乐（有 资料.txt 就按文案配音 + 字幕；想让音频当配音请删掉 资料.txt）";
        if (scan.Skipped.Count > 0)
        {
            var details = string.Join("；", scan.Skipped.Take(3).Select(s => $"{Path.GetFileName(s.Folder)}：{s.Reason}"));
            if (scan.Skipped.Count > 3) details += $"；等共 {scan.Skipped.Count} 个被跳过";
            StatusNote += $"；跳过 {scan.Skipped.Count} 个（{details}）";
        }
        IsStatusError = scan.Units.Count == 0;
        return scan.Units.Count;
    }

    /// <summary>批量优化期间禁用批量按钮（防连点两批并发）；确认条待决策期间也禁用</summary>
    private bool CanPickBatchFolder => !IsBatchOptimizing && !IsConfirmingBatch;

    // ── 批量自动发布确认条（2026-10-11 用户拍板：批量入口单独确认一次）──
    // 开了「合成完成后自动发布」时，从文件夹批量添加前原地确认一次条数与账号数 ——
    // 批量 N 条 × M 账号的误发面太大，不确认就直接发不行；确认条期间入口按钮禁用防连点。

    [ObservableProperty] private bool _isConfirmingBatch;
    [ObservableProperty] private string _batchConfirmText = "";
    private IReadOnlyList<string>? _pendingBatchFolders;
    private IReadOnlyList<AutoPublishTarget>? _pendingBatchTargets;
    private DateTime? _pendingBatchScheduledAt;

    partial void OnIsConfirmingBatchChanged(bool value) => PickBatchFolderCommand.NotifyCanExecuteChanged();

    /// <summary>确认条文案（纯函数，自检钉住口径）。有批次定时 → 「将于 yyyy-MM-dd HH:mm 发布」，
    /// 否则「立即发布」。必须保住被断言的子串：N 条 / M 个账号 / N×M 条平台记录（冒烟 + 自检）。</summary>
    internal static string BuildBatchConfirmText(int units, int accounts, DateTime? scheduledAt = null) =>
        $"本批 {units} 条将在合成完成后自动发布到 {accounts} 个账号"
        + $"（共 {units * accounts} 条平台记录，"
        + (scheduledAt is { } at ? $"将于 {at:yyyy-MM-dd HH:mm} 发布" : "立即发布")
        + "；误发可在「上传队列」撤回未发出的记录）";

    /// <summary>摆出确认条（选择器路径与冒烟直接调共用 —— 冒烟不碰真选择器）。
    /// targets / scheduledAt = 确认那一刻的输入快照（确认入队就用它们，不再取第二遍）。</summary>
    internal void ShowBatchConfirm(IReadOnlyList<string> folders, int units, int accounts,
        IReadOnlyList<AutoPublishTarget>? targets = null, DateTime? scheduledAt = null)
    {
        _pendingBatchFolders = folders;
        _pendingBatchTargets = targets;
        _pendingBatchScheduledAt = scheduledAt;
        BatchConfirmText = BuildBatchConfirmText(units, accounts, scheduledAt);
        IsConfirmingBatch = true;
    }

    /// <summary>确认并自动发布（文件夹重扫在 AddFolderBatchAsync 里自然发生，纯文件系统、毫秒级）</summary>
    [RelayCommand]
    private async Task ConfirmBatchAdd()
    {
        var folders = _pendingBatchFolders;
        var targets = _pendingBatchTargets;
        var scheduledAt = _pendingBatchScheduledAt;
        IsConfirmingBatch = false;
        _pendingBatchFolders = null;
        _pendingBatchTargets = null;
        _pendingBatchScheduledAt = null;
        if (folders is null) return;
        await AddFolderBatchAsync(folders, targets, scheduledAt);
    }

    /// <summary>仅添加（本批不自动发布）：视频照常合成，发布走发布页导入；
    /// 定时照带给每条（定时 = 发布单元「发布时间」，与自动发布开关独立）</summary>
    [RelayCommand]
    private async Task AddBatchWithoutPublish()
    {
        var folders = _pendingBatchFolders;
        var scheduledAt = _pendingBatchScheduledAt;
        IsConfirmingBatch = false;
        _pendingBatchFolders = null;
        _pendingBatchTargets = null;
        _pendingBatchScheduledAt = null;
        if (folders is null) return;
        await AddFolderBatchAsync(folders, null, scheduledAt);
    }

    /// <summary>取消：什么都不加（要重新选文件夹）</summary>
    [RelayCommand]
    private void CancelBatchAdd()
    {
        _pendingBatchFolders = null;
        _pendingBatchTargets = null;
        _pendingBatchScheduledAt = null;
        IsConfirmingBatch = false;
        StatusNote = "已取消本批添加";
        IsStatusError = false;
    }

    [RelayCommand(CanExecute = nameof(CanPickBatchFolder))]
    private async Task PickBatchFolder()
    {
        var folders = PickBatchFoldersAsync == null ? null : await PickBatchFoldersAsync();
        if (folders is null || folders.Count == 0) return;

        // 定时先校验（日期/时间不全永远拦；窗口只在开了自动发布时拦）—— 失败不加任何任务
        await _chips.EnsureLoadedAsync();
        var autoOn = _chips.AutoPublish;
        if (BatchScheduleError(IsScheduled, ScheduledAtOrNull, autoOn, DateTime.Now) is { } schedError)
        {
            StatusNote = schedError;
            IsStatusError = true;
            return;
        }

        // 未开自动发布 → 老路（定时照带：写入发布单元的「发布时间」）
        if (!autoOn)
        {
            await AddFolderBatchAsync(folders, null, ScheduledAtOrNull);
            return;
        }

        // 开了：先扫描预览 + 取勾选账号，摆确认条（都不改任何状态；AI 优化在确认之后才跑）
        var scan = ComposeFolderScanner.Scan(folders);
        var selected = await _chips.SelectedAsync();
        if (scan.Units.Count == 0 || selected.Count == 0)
        {
            // 没东西可加 / 没勾账号 → 回落老路（AddFolderBatchAsync 自己会给状态提示）
            await AddFolderBatchAsync(folders, null, ScheduledAtOrNull);
            if (selected.Count == 0 && scan.Units.Count > 0)
            {
                // 防「以为发了其实没发」：开了开关却没勾账号，明说本批未自动发布
                StatusNote += "；自动发布已开启但没有勾选账号，本批未自动发布";
            }
            return;
        }
        ShowBatchConfirm(folders, scan.Units.Count, selected.Count, selected, ScheduledAtOrNull);
    }

    // ═══ 新建模板（2026-10-11 用户要求：添加按钮像发布页那样先生成文件夹，用户替换素材后整批导入）═══

    /// <summary>最近生成的模板目录（「打开模板文件夹」用；null = 本次会话还没生成过）</summary>
    [ObservableProperty]
    private string? _templateFolder;

    public bool HasTemplateFolder => !string.IsNullOrEmpty(TemplateFolder);
    partial void OnTemplateFolderChanged(string? value) => OnPropertyChanged(nameof(HasTemplateFolder));

    /// <summary>选模板父目录回调（View 挂载时注入 —— StorageProvider 挂在 TopLevel 上，VM 拿不到它）</summary>
    public Func<Task<string?>>? PickTemplateFolderAsync { get; set; }

    [RelayCommand]
    private async Task CreateTemplate()
    {
        var parent = PickTemplateFolderAsync == null ? null : await PickTemplateFolderAsync();
        if (string.IsNullOrWhiteSpace(parent)) return;
        await CreateTemplateAtAsync(parent);
    }

    /// <summary>在 parent 下生成「合成单元模板」（冒烟直接调，绕开选择器）。返回是否成功。</summary>
    public async Task<bool> CreateTemplateAtAsync(string parent)
    {
        if (FeatureGate.BlockReason(_session.IsExpired, _session.Username, App.SmokeMode, "合成功能") is { } block)
        {
            StatusNote = block;
            IsStatusError = true;
            return false;
        }

        var result = await ComposeUnitTemplate.WriteAsync(parent);
        if (result.Error is not null)
        {
            StatusNote = "生成模板失败：" + result.Error;
            IsStatusError = true;
            return false;
        }

        TemplateFolder = result.Folder;
        StatusNote = result.AllKept
            ? $"模板已存在，已为你打开：{result.Folder}"
            : $"已生成模板：{result.Folder} —— 复制一份改名、把示例素材换成你的（资料.txt 里写文案），再用「从文件夹批量添加」整批入队";
        IsStatusError = false;
        ShellLauncher.RevealFolder(result.Folder);   // 打不开不算失败：路径还在状态条里
        return true;
    }

    [RelayCommand]
    private void OpenTemplateFolder()
    {
        if (TemplateFolder is { Length: > 0 } dir) ShellLauncher.RevealFolder(dir);
    }

    // ═══ 任务操作 ═══

    /// <summary>取消：排队中 → 直接标已取消；合成中 → 断给当前运行（ComposeRunner 收到取消会清理收尾）</summary>
    public void Cancel(ComposeTask task)
    {
        switch (task.Status)
        {
            case ComposeTaskStatus.Pending:
                task.StatusMessage = "已取消";
                task.Status = ComposeTaskStatus.Cancelled;
                Persist();
                break;
            case ComposeTaskStatus.Running:
                _runCts?.Cancel();
                break;
        }
    }

    [RelayCommand]
    private void CancelTask(ComposeTask task) => Cancel(task);

    [RelayCommand]
    private void RetryTask(ComposeTask task)
    {
        if (!task.CanRetry) return;
        task.Progress = 0;
        task.StatusMessage = "";
        task.OutputPath = null;
        task.Status = ComposeTaskStatus.Pending;
        Persist();
        _signal.Release();
    }

    [RelayCommand]
    private void RemoveTask(ComposeTask task)
    {
        if (!task.CanRemove) return;
        Tasks.Remove(task);
        Persist();
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var t in Tasks.Where(t => t.Status is ComposeTaskStatus.Done or ComposeTaskStatus.Cancelled).ToList())
            Tasks.Remove(t);
        Persist();
    }

    [RelayCommand]
    private void OpenTaskFolder(ComposeTask task)
    {
        if (string.IsNullOrEmpty(task.OutputPath)) return;
        var dir = Path.GetDirectoryName(task.OutputPath);
        if (dir is null || !ShellLauncher.RevealFolder(dir))
        {
            StatusNote = "打开文件夹失败：文件可能已被移动或删除";
            IsStatusError = true;
        }
    }

    [RelayCommand]
    private void PlayTask(ComposeTask task)
    {
        if (string.IsNullOrEmpty(task.OutputPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = task.OutputPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusNote = "打开播放器失败：" + ex.Message;
            IsStatusError = true;
        }
    }

    // ═══ 批量入口状态条 ═══

    [ObservableProperty]
    private string _statusNote = "";

    [ObservableProperty]
    private bool _isStatusError;

    public bool HasStatusNote => !string.IsNullOrEmpty(StatusNote);
    partial void OnStatusNoteChanged(string value) => OnPropertyChanged(nameof(HasStatusNote));

    // ═══ 消费循环（并发 1：串行跑，跑完一条捡下一条） ═══

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var ran = false;
            try
            {
                await TryCompleteMemberSwitchAsync();   // 换号挂起且已空闲 → 先换档再干活
                ran = await RunNextAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warning(ex, "[ComposeQueue] 消费循环异常（继续跑下一条）");
            }

            // 刚跑完一条就立刻看还有没有下一条 —— 老写法跑完先睡 30s 等信号，
            // 排队几条时每条之间都空转 30 秒（配音队列 2026-10-10 实测发现，两个队列同病同修）
            if (ran) continue;
            try
            {
                // 空转靠入队信号唤醒；30s 兜底轮询（万一有状态变更漏了信号也能捡回来）
                await _signal.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>捡一条待合成并跑完；返回 false = 没活干</summary>
    private async Task<bool> RunNextAsync(CancellationToken ct)
    {
        var picked = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // 许可停摆（App 登出/撤权时置）：不领取新任务
            if (_gateReason is not null) return ((ComposeTask Task, ComposeJob Job)?)null;
            // 换号挂起：不领新任务（取消/移除/清空不受影响 —— 都写老会员的档，正确）
            if (_targetMember is not null) return ((ComposeTask Task, ComposeJob Job)?)null;
            var next = Tasks.FirstOrDefault(t => t.Status == ComposeTaskStatus.Pending);
            if (next is null) return ((ComposeTask Task, ComposeJob Job)?)null;

            // 活体许可再查一道（页面开着、会员中途过期时兜住）：拦下就停摆，任务留队
            if (FeatureGate.BlockReason(_session.IsExpired, _session.Username, App.SmokeMode, "合成功能") is { } block)
            {
                _gateReason = block + "。队列已停止执行 —— 续费激活后自动接着跑";
                StatusNote = _gateReason;
                IsStatusError = false;
                return ((ComposeTask Task, ComposeJob Job)?)null;
            }

            next.Progress = 0;
            next.StatusMessage = "正在开始…";
            next.Status = ComposeTaskStatus.Running;
            return (next, next.ToJob());
        });
        if (picked is null) return false;
        var task = picked.Value.Task;
        var job = picked.Value.Job;

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Persist();   // 先把「合成中」落盘：此刻进程退出 → 下次启动标已中断

        try
        {
            var outcome = await ComposeRunner.RunAsync(job,
                (msg, pct) => Dispatcher.UIThread.Post(() =>
                {
                    if (task.Status != ComposeTaskStatus.Running) return;
                    task.StatusMessage = msg;
                    if (pct > 0) task.Progress = pct;
                }), _runCts.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                task.Progress = 100;
                task.OutputPath = outcome.OutputPath;
                task.StatusMessage = "合成完成";
                task.Status = ComposeTaskStatus.Done;
            });

            // 合成已完成（事实落定）→「自动进上传队列」是它之后独立的一件事：
            // 独立状态轴（AutoPublishState）表达发布段，不污染 ComposeTaskStatus 的恢复规则。
            // 在池线程接着跑 —— 含 SHA256 + 网络，绝不能压 UI 线程。
            if (job.AutoPublishTargets.Count > 0)
            {
                // 上次在提交窗口被中断的任务重试到这里：状态未知，绝不自动重发 —— 人工去上传队列核对
                if (ShouldSkipAutoPublish(task.AutoPublishState))
                {
                    await SetAutoPublishAsync(task, AutoPublishState.Uncertain,
                        "上次在提交发布时被中断 —— 可能已进上传队列，请到「上传队列」核对（不会自动重发）");
                }
                else
                {
                    await AutoPublishAsync(task, job, outcome, _runCts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                task.StatusMessage = "已取消";
                task.Status = ComposeTaskStatus.Cancelled;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                task.StatusMessage = ex.Message;
                task.Status = ComposeTaskStatus.Failed;
            });
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            Persist();
        }
        return true;
    }

    // ═══ 自动进上传队列（合成 Done 之后的独立状态轴） ═══

    /// <summary>
    /// 重试跑到自动发布段时该不该跳过（纯函数，自检钉住）：
    /// Submitting = 本进程内重试（理论上到不了）；Uncertain = 重启恢复规则转换过的 ——
    /// **两个都要挡**：只挡 Submitting 的话「崩溃 → 重启 → 重试」正好绕过守卫、重复提交一次。
    /// </summary>
    internal static bool ShouldSkipAutoPublish(AutoPublishState state) =>
        state is AutoPublishState.Submitting or AutoPublishState.Uncertain;

    /// <summary>
    /// 把刚合成的发布单元无头提交进上传队列（<see cref="PublishUnitSubmitter"/> 走发布页同一条管线）。
    /// **绝不抛**：任何失败（含取消）都落成任务上的 <see cref="AutoPublishState"/> 文案 —— 视频已经
    /// 合成好了是事实，自动发布只是附加动作，不能把整条任务翻成失败/取消。
    /// </summary>
    private async Task AutoPublishAsync(ComposeTask task, ComposeJob job,
        ComposeOutcome outcome, CancellationToken ct)
    {
        // 换号在途（在途任务收尾窗口 / 已检出挂起）：旧会员的任务不给新会员提交 ——
        // 快照里的账号与预设都属老会员，拿新会员的 token 发必然失败/混乱（批次 7）
        var switched = await Dispatcher.UIThread.InvokeAsync(
            () => _targetMember is not null || _loadedMember != _store.CurrentMemberId);
        if (switched)
        {
            await SetAutoPublishAsync(task, AutoPublishState.Skipped,
                "未自动发布：账号已切换，请到「发布视频」页手动发布");
            return;
        }

        // 许可再查一道（合成跑了几分钟，会员可能中途过期；用户规则：功能一律要登录且激活）
        if (FeatureGate.BlockReason(_session.IsExpired, _session.Username, App.SmokeMode, "自动发布") is { } block)
        {
            await SetAutoPublishAsync(task, AutoPublishState.Skipped, "未自动发布：" + block);
            return;
        }

        // 崩溃窗口标记：HTTP 发出前先 **落定盘**（fire-and-forget 的 Persist 在强杀时可能还没写出去），
        // 之后进程没了 → 重启恢复规则看到 Submitting → 标「状态未知」不重发
        await SetAutoPublishAsync(task, AutoPublishState.Submitting,
            $"正在提交发布…（{job.AutoPublishTargets.Count} 个账号）");
        try { await PersistNowAsync(); }
        catch (Exception ex) { Log.Warning(ex, "[ComposeQueue] 提交标记落盘失败（继续提交）"); }

        try
        {
            var result = await _autoSubmitter.SubmitAsync(
                outcome.UnitDir, outcome.OutputPath, job.AutoPublishTargets, ct);

            await SetAutoPublishAsync(task,
                result.Ok ? AutoPublishState.Submitted : AutoPublishState.Failed, result.Message);
        }
        catch (OperationCanceledException)
        {
            // HTTP 不可取消（ApiClient 不带 token），取消只可能来自解析/探测 —— 但标记已在盘上，
            // 无从得知提交发到哪一步，按「状态未知」走最保守
            await SetAutoPublishAsync(task, AutoPublishState.Uncertain,
                "提交被中断 —— 可能已进上传队列，请到「上传队列」核对");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ComposeQueue] 自动发布异常");
            await SetAutoPublishAsync(task, AutoPublishState.Failed, "自动发布失败：" + ex.Message);
        }
    }

    /// <summary>写自动发布状态（UI 线程；InvokeAsync 不用 Post —— 状态要在后续落盘前可见/生效）</summary>
    private async Task SetAutoPublishAsync(ComposeTask task, AutoPublishState state, string text)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            task.AutoPublishState = state;
            task.AutoPublishText = text;
        });
    }

    /// <summary>同步等一次落盘（崩溃窗口标记用 —— 必须确认写出去再发 HTTP）。
    /// 与 fire-and-forget 的 <see cref="Persist"/> 共用同一把 <c>_saveLock</c>，顺序不乱。</summary>
    private async Task PersistNowAsync()
    {
        // 快照与属主在同一个 UI 委托里取（换号换装只在 UI 线程，两者天然一致）
        var (snapshot, member) = await Dispatcher.UIThread.InvokeAsync(
            () => (Tasks.Select(t => t.ToData()).ToList(), _loadedMember));
        await SaveSnapshotAsync(snapshot, member);
    }

    // ═══ 落盘 ═══

    /// <summary>落盘（先在当前线程快照，再异步写；写操作本身串行化防两个写撞同一临时文件）</summary>
    private void Persist()
    {
        if (Dispatcher.UIThread.CheckAccess()) FirePersist();
        else Dispatcher.UIThread.Post(FirePersist);
    }

    private void FirePersist()
    {
        // UI 线程上同时取快照与属主：任务在换号挂起期间收尾 → 仍落老会员的档
        var snapshot = Tasks.Select(t => t.ToData()).ToList();
        _ = SaveSnapshotAsync(snapshot, _loadedMember);
    }

    private async Task SaveSnapshotAsync(List<ComposeTaskData> snapshot, Guid member)
    {
        await _saveLock.WaitAsync();
        try
        {
            await _store.SaveAsync(snapshot, member);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ComposeQueue] 队列落盘失败");
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
