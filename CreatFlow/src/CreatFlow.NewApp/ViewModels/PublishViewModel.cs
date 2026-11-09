using System.ComponentModel;
using System.Security.Cryptography;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>发布页当前显示哪一屏（三屏互斥，单一事实来源见 <see cref="PublishViewModel.ViewMode"/>）</summary>
public enum PublishViewMode
{
    /// <summary>工作台（导入 + 待发布列表 + 底栏提交）</summary>
    Workbench,
    /// <summary>条目编辑器（也可能是账号预设编辑器，见 IsPresetMode）</summary>
    Editor,
    /// <summary>发布预设列表</summary>
    Presets,
    /// <summary>账号显示顺序（平台分组 + 组内账号，两级上下移）</summary>
    Order,
}

/// <summary>
/// 发布页 —— 整页三视图（工作台 ⇄ 条目编辑器 ⇄ 发布预设），仿合集页的互斥根节点手法。
///
/// 设计基线见 docs/creatflow/newapp-publish-page-design.md。
/// 一句话概括本页要解决的问题：老版可见字段约 50 个，但真正独立的概念只有 11 个 ——
/// 字段数 ≈ Σ(各平台各自实现的概念)。所以：
///   · 一个概念只存一份（<see cref="PublishDraft"/> 全局区 + 每目标 <see cref="PlatformParams"/>）；
///   · 提交时才按平台展开（<see cref="PublishPayloadBuilder"/>）；
///   · 真正省事的是预设 —— 让 11 个里的 8 个只在配置时碰一次（§4.5）。
/// </summary>
public sealed partial class PublishViewModel : ViewModelBase, INavigationAware
{
    private readonly AccountDockViewModel _dock;
    private readonly AccountChipsService _chips;
    private readonly AccountGroupsService _groups;
    // 预设/草稿与顺序都是**会员级**存档（DI 单例注入 → 与平台页/账号坞/提交器共享同一实例）
    private readonly PublishDraftStore _store;
    private readonly VideoMetaProbe _probe = new();
    private readonly PublishResolver _resolver = new();
    private readonly PublishPipeline _pipeline;

    /// <summary>账号级预设：key = {platformKey}:{sessionId}</summary>
    private readonly Dictionary<string, PlatformParams> _defaults = [];

    private bool _loaded;

    public PublishViewModel(AccountDockViewModel dock, AccountChipsService chips,
        AccountGroupsService groups, PublishDraftStore draftStore, AccountOrderStore orderStore)
    {
        _dock = dock;
        _chips = chips;
        _groups = groups;
        _store = draftStore;
        _orderStore = orderStore;
        _pipeline = new PublishPipeline { MetaProbe = _probe };
        _pipeline.OnPlatformProgress += OnProgress;
        _pipeline.OnPlatformComplete += OnPlatformComplete;

        DeclarationOptions = BuildDeclarationOptions();
        VisibilityOptions = BuildVisibilityOptions();

        // 芯片进场即接线：勾选变化 → 摘要计数播报。
        // 挂在集合上而不是「建芯片的那一处」，是为了不依赖「谁把芯片放进来」——
        // 少接一根线就是「点了没反应」，而这类漏线只会在用户手里暴露。
        GlobalTargets.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null) return;
            foreach (AccountChipViewModel chip in e.NewItems)
                chip.PropertyChanged += OnChipPropertyChanged;
        };
    }

    // ═══════════════════════════════════════════════════════════════
    //  视图切换
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 当前显示哪一屏 —— **单一事实来源**。
    ///
    /// 为什么不是三个互斥的 bool：那样每个 setter 都要去关掉另外两个，而关掉的动作
    /// 又会回调进同一个互斥函数，形成**重入自噬** —— 实测 `ShowPresets = true` 的调用链是
    /// 「置 Presets=true → 关 Workbench → 关 Workbench 的回调里发现 Presets 还开着 →
    /// 把 Presets 关掉」→ 三个标志全假 → **整页白屏**（用户 2026-09-17 报的正是这个）。
    /// 一个枚举字段从结构上杜绝这一类 bug：不存在「两个同时为真」，也不存在「全为假」。
    /// </summary>
    [ObservableProperty] private PublishViewMode _viewMode = PublishViewMode.Workbench;

    public bool ShowWorkbench => ViewMode == PublishViewMode.Workbench;
    public bool ShowEditor => ViewMode == PublishViewMode.Editor;
    public bool ShowPresets => ViewMode == PublishViewMode.Presets;
    public bool ShowOrder => ViewMode == PublishViewMode.Order;

    partial void OnViewModeChanged(PublishViewMode value)
    {
        OnPropertyChanged(nameof(ShowWorkbench));
        OnPropertyChanged(nameof(ShowEditor));
        OnPropertyChanged(nameof(ShowPresets));
        OnPropertyChanged(nameof(ShowOrder));
        OnPropertyChanged(nameof(ShowVerifyPrompt));   // 提示条只在工作台/编辑器两屏显示
    }

    [RelayCommand]
    private void BackToList()
    {
        // 从预设模式返回 = 回预设列表；从条目编辑器返回 = 回工作台
        if (IsPresetMode) { IsPresetMode = false; SelectedDraft = null; ViewMode = PublishViewMode.Presets; }
        else { SelectedDraft = null; ViewMode = PublishViewMode.Workbench; }
        RefreshSummaryCounts();
    }

    [RelayCommand]
    private void ShowPresetsView()
    {
        BuildPresetRows();
        ViewMode = PublishViewMode.Presets;
    }

    // ═══════════════════════════════════════════════════════════════
    //  页头 / 底栏
    // ═══════════════════════════════════════════════════════════════

    [ObservableProperty] private string _editorTitle = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _submitProgress;

    /// <summary>底栏计数文案</summary>
    [ObservableProperty] private string _footerText = "";

    /// <summary>预设模式：编辑器编的是账号预设而不是某条草稿</summary>
    [ObservableProperty] private bool _isPresetMode;

    // ═══════════════════════════════════════════════════════════════
    //  账号（工作台的「发布到」）
    // ═══════════════════════════════════════════════════════════════

    public ObservableCollection<AccountChipViewModel> GlobalTargets { get; } = [];

    /// <summary>账号芯片的勾选摘要：`3 / 5 个账号`（只算已接发布的平台 —— B站/头条芯片展示但不可勾）</summary>
    public string TargetCountText =>
        $"{GlobalTargets.Count(t => t.IsOn && t.CanPublish)} / {GlobalTargets.Count(t => t.CanPublish)} 个账号";

    // 「显式关掉的账号」集合（全关是合法选择、不能与首轮混淆）已移居共享服务
    // AccountChipsService —— 合成页「自动发布」的账号芯片读写的是同一份（在哪边改都生效，
    // 且跨重启保留）。本页只负责把勾选变化转报过去。

    [ObservableProperty] private bool _hasAccounts = true;

    /// <summary>某账号登录失效时的横幅（最高优先级交互，设计文档 §2.4 ①）</summary>
    public ObservableCollection<AccountAlertViewModel> AccountAlerts { get; } = [];
    public bool HasAccountAlerts => AccountAlerts.Count > 0;

    /// <summary>
    /// 拉平台配置 + 已绑定会话，建账号芯片（装配与勾选存档在 <see cref="AccountChipsService"/>，
    /// 与合成页「自动发布」共用同一份「上次勾选」）。
    /// 只在首次进页与账号坞同步事件后重建（勾选态由共享服务保留）。
    /// </summary>
    private async Task LoadAccountsAsync()
    {
        var chips = await _chips.BuildAsync();
        if (chips is null) return;   // 接口失败：保留现有列表

        GlobalTargets.Clear();
        foreach (var chip in chips) GlobalTargets.Add(chip);

        HasAccounts = GlobalTargets.Count > 0;
        OnPropertyChanged(nameof(TargetCountText));
        RefreshAccountAlerts();
        SortAllDraftTargets();
        await RefreshGroupChoicesAsync();   // 组条的成员存活/分母都跟着这次刷新走
    }

    private void RefreshAccountAlerts()
    {
        AccountAlerts.Clear();
        // B站/头条不参与发布，过期了也不该在发布页刷存在感（平台页照常提示）
        foreach (var chip in GlobalTargets.Where(t => !t.IsLoggedIn && t.CanPublish))
        {
            AccountAlerts.Add(new AccountAlertViewModel
            {
                SessionId = chip.SessionId,
                PlatformKey = chip.PlatformKey,
                Text = $"{chip.PlatformName} · @{chip.AccountLabel} 登录已过期，相关任务将无法执行",
            });
        }
        OnPropertyChanged(nameof(HasAccountAlerts));
    }

    /// <summary>去登录 → 打开账号坞对应标签（登录成功后 SessionSynced 会自动刷新）</summary>
    [RelayCommand]
    private void GoLogin(AccountAlertViewModel? alert)
    {
        if (alert is null) return;
        var chip = GlobalTargets.FirstOrDefault(t => t.SessionId == alert.SessionId);
        if (chip is null) return;
        _dock.OpenOrActivate(alert.SessionId, chip.PlatformKey, chip.PlatformName,
            chip.BrandHex, "", chip.AccountLabel, forceLogin: true);
    }

    // ── 账号窗口未打开提示（纯建议：不拦提交；打开过即消失）──

    /// <summary>本次运行还没打开过窗口、被选中草稿要用到的账号（四个平台都要）</summary>
    [ObservableProperty] private bool _hasVerifyPrompt;

    [ObservableProperty] private string _verifyPromptText = "";

    /// <summary>提示条只在「工作台 / 编辑器」两屏显示（预设/排序是配置屏，不打扰）</summary>
    public bool ShowVerifyPrompt => HasVerifyPrompt && (ShowWorkbench || ShowEditor);

    partial void OnHasVerifyPromptChanged(bool value) => OnPropertyChanged(nameof(ShowVerifyPrompt));

    /// <summary>
    /// 重算提示。**依据是「打开过」不是「验证完成」**——后者要页面流量 + 跨安全闸，
    /// 卡住时用户明明打开了窗口提示也不走（2026-10-09 实测「已经登录了还提示」）；
    /// 打开过就消失（切回本页/刷新账号时重算）。登录已过期的账号不在此列
    /// （那是 AccountAlerts 的事，两边都报就成了重复唠叨）。
    /// </summary>
    private void RefreshVerifyPrompt()
    {
        var chipsOk = GlobalTargets.ToDictionary(c => c.SessionId, c => c.IsLoggedIn);
        var need = new List<string>();
        var seen = new HashSet<Guid>();
        foreach (var d in Drafts.Where(d => d.IsSelected))
        foreach (var t in d.EnabledTargets)
        {
            if (!AccountDockViewModel.NeedsAccountWindow(t.PlatformKey)) continue;
            // 登录过期/已解绑的账号归账号异常横幅管（打开窗口也救不了，得先重新登录）
            if (!chipsOk.TryGetValue(t.SessionId, out var loggedIn) || !loggedIn) continue;
            if (_dock.IsSessionOpened(t.SessionId)) continue;
            if (!seen.Add(t.SessionId)) continue;
            need.Add($"{t.PlatformDisplayName} · @{t.AccountLabel}");
        }

        HasVerifyPrompt = need.Count > 0;
        VerifyPromptText = need.Count > 0
            ? $"{string.Join("、", need)}：建议先到「平台账号」点开这些账号窗口刷新登录凭证 —— "
              + "平台风控严，凭证未刷新时发布可能被拒；提示过期时请重新扫码。打开过本提示即消失"
            : "";
    }

    /// <summary>提示条「去平台账号」—— 页 VM 不认识壳，导航能力由壳注入（与首页卡片同范式）。
    /// **不代开窗口**：三家都有扫码/风控，必须用户自己在「平台账号」里点开。</summary>
    public Action<string>? OnNavigateRequested { get; set; }

    [RelayCommand]
    private void GoOpenAccountWindows() => OnNavigateRequested?.Invoke("platform");

    /// <summary>账号芯片勾选变化 —— 新导入的条目按它建目标</summary>
    public void OnTargetChipToggled() => OnPropertyChanged(nameof(TargetCountText));

    /// <summary>
    /// 芯片勾选变化 → 右上角那个 `2 / 8 个账号` 跟着走。
    ///
    /// <see cref="TargetCountText"/> 是从 <see cref="GlobalTargets"/> 现算的**计算属性**，
    /// 不订阅就没人替它播报：点了芯片，芯片自己会变紫，数字却一直停在旧值 ——
    /// 要等下一次进页（OnNavigatedTo → RefreshAccountsAsync → RefreshSummaryCounts）
    /// 才补上。用户看到的就是「在当前页面怎么点都不动，切走再回来才对」（2026-09-20 报）。
    /// </summary>
    private void OnChipPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AccountChipViewModel.IsOn)
            || sender is not AccountChipViewModel chip) return;

        // 一键勾选（组条）期间：逐颗记账由整批的 ReplaceSelectionAsync 接管 ——
        // 计数/高亮也在批量结束后补一次（见 ApplyGroupAsync），别在这里抢跑
        if (_applyingGroup) return;

        // 共享服务统一记账（内存 + 串行落盘）—— 合成页自动发布的账号芯片读同一份
        _chips.RecordToggle(chip.SessionId, chip.IsOn);

        OnTargetChipToggled();
        RefreshActiveGroup();   // 手动微调后「哪一组正被应用」的高亮跟着走
    }

    // ═══════════════════════════════════════════════════════════════
    //  账号分组组条（2026-10-14 用户需求：点组名 = 一键只勾选该组）
    //  —— 与合成页 TargetChips 那份同款（组条 XAML 亦然），改一处要同步另一处
    // ═══════════════════════════════════════════════════════════════

    /// <summary>组条数据源（第 0 格恒为「全部」哨兵；没有真分组时整条隐藏，现有 UI 不变）</summary>
    public ObservableCollection<AccountGroupChoiceViewModel> GroupChoices { get; } = [];

    /// <summary>有没有真分组（只有「全部」一格 = 没分组）</summary>
    public bool HasGroupChoices => GroupChoices.Count > 1;

    /// <summary>一键勾选进行中（循环内短路逐颗记账；循环结束后统一补计数/高亮）</summary>
    private bool _applyingGroup;

    /// <summary>点一格：把勾选整体替换成「该格成员 / 全部」。所有芯片保持可见，可再手动微调。</summary>
    [RelayCommand]
    private async Task ApplyGroupAsync(AccountGroupChoiceViewModel? choice)
    {
        if (choice is null || choice.IsEmpty) return;

        var all = GlobalTargets.Where(c => c.CanPublish).Select(c => c.SessionId).ToList();
        HashSet<Guid> on;
        if (choice.Id == AccountGroups.AllGroupsId)
        {
            on = [.. all];
        }
        else
        {
            var group = _groups.Groups.FirstOrDefault(g => g.Id == choice.Id);
            if (group is null) return;   // 组刚在平台页被删（组条还没刷新）——静默忽略，下次刷新即消失
            on = AccountGroups.Members(group, all);
        }

        // 同步循环改 IsOn（循环内**不得 await** —— ReplaceSelectionAsync 的契约）
        _applyingGroup = true;
        try
        {
            foreach (var c in GlobalTargets.Where(c => c.CanPublish)) c.IsOn = on.Contains(c.SessionId);
        }
        finally { _applyingGroup = false; }

        await _chips.ReplaceSelectionAsync(on, all);
        OnTargetChipToggled();     // 「N / M 个账号」播报（与手动点芯片同一条路径）
        RefreshActiveGroup();
    }

    /// <summary>组条重建（进页/账号列表刷新时：分母与成员存活都变了）。空组也列出（灰着）。
    /// internal：冒烟特判直接调（离线时不会被 LoadAccountsAsync 失败挡在半路）。</summary>
    internal async Task RefreshGroupChoicesAsync()
    {
        await _groups.EnsureLoadedAsync();
        var all = GlobalTargets.Where(c => c.CanPublish).Select(c => c.SessionId).ToList();

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
        var all = GlobalTargets.Where(c => c.CanPublish).Select(c => c.SessionId).ToList();
        var on = GlobalTargets.Where(c => c.IsOn && c.CanPublish).Select(c => c.SessionId).ToList();
        var active = AccountGroups.ActiveGroupId(_groups.Groups, on, all);
        foreach (var ch in GroupChoices) ch.IsActive = ch.Id == active;
    }

    // ═══════════════════════════════════════════════════════════════
    //  草稿
    // ═══════════════════════════════════════════════════════════════

    public ObservableCollection<PublishDraft> Drafts { get; } = [];

    [ObservableProperty] private string _draftCountText = "待发布 0 条";
    [ObservableProperty] private bool _hasDrafts;

    private void RefreshSummaryCounts()
    {
        var enabled = Drafts.Where(d => d.IsSelected).ToList();
        var submittable = enabled.Count(d => d.IsSubmittable);
        var blocked = enabled.Count - submittable;
        var taskCount = enabled.Sum(d => d.EnabledTargets.Count());

        DraftCountText = $"待发布 {Drafts.Count} 条";
        HasDrafts = Drafts.Count > 0;

        FooterText = Drafts.Count == 0
            ? "还没有待发布条目 —— 先导入一个文件夹"
            : $"已选 {enabled.Count} 条 → 共 {taskCount} 个发布任务"
              + (blocked > 0 ? $"　　{blocked} 条需处理" : "");

        OnPropertyChanged(nameof(TargetCountText));
        OnPropertyChanged(nameof(SelectedDraftCount));
        OnPropertyChanged(nameof(HasSelectedDrafts));
        OnPropertyChanged(nameof(RemoveSelectedText));
        SyncAllDraftsSelected();
        RefreshVerifyPrompt();   // 「账号窗口未打开」提示跟着勾选/目标/账号刷新走
    }

    /// <summary>勾选的条数（工作台工具条用 —— 与提交同一口径：勾选 = 要处理的条目）</summary>
    public int SelectedDraftCount => Drafts.Count(d => d.IsSelected);
    public bool HasSelectedDrafts => SelectedDraftCount > 0;
    public string RemoveSelectedText =>
        SelectedDraftCount > 0 ? $"移除选中 {SelectedDraftCount} 条" : "移除选中";

    /// <summary>
    /// 「待发布 N 条」右侧的全选开关（2026-10-22 用户需求）—— 与每条 <see cref="PublishDraft.IsSelected"/>
    /// 双向联动，重入用 <see cref="_syncingDraftSelection"/> 挡（与上传队列 AllSelected 同款）。
    /// </summary>
    [ObservableProperty] private bool _allDraftsSelected;
    private bool _syncingDraftSelection;

    partial void OnAllDraftsSelectedChanged(bool value)
    {
        if (_syncingDraftSelection) return;
        _syncingDraftSelection = true;
        try
        {
            PublishSelection.SetAll(Drafts, value);
        }
        finally
        {
            _syncingDraftSelection = false;
        }
        RefreshSummaryCounts();
    }

    /// <summary>逐条勾选反推全选框（全部勾上才亮；空列表恒 false）</summary>
    private void SyncAllDraftsSelected()
    {
        if (_syncingDraftSelection) return;   // 全选/取消全选正在批量改行，别被中间态打回去
        var all = PublishSelection.AllSelected(Drafts);
        if (AllDraftsSelected == all) return;

        _syncingDraftSelection = true;
        try
        {
            AllDraftsSelected = all;
        }
        finally
        {
            _syncingDraftSelection = false;
        }
    }

    /// <summary>草稿集合内的任一变化都要重算计数（勾选/目标/预检）</summary>
    private void AttachDraft(PublishDraft d)
    {
        d.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PublishDraft.IsSelected) or nameof(PublishDraft.Issues)
                or nameof(PublishDraft.Title) or nameof(PublishDraft.Meta))
            {
                RunPreflight(d);
                RefreshSummaryCounts();
            }

            // 编辑器里从草稿算出来的字段（计数器 / 封面预览 / 定时……）不会自己通知 ——
            // 面板常驻，漏播报就是「敲字时界面不跟着走」（2026-09-19 实测计数器恒 0 字）
            if (ReferenceEquals(d, SelectedDraft)
                && e.PropertyName is nameof(PublishDraft.Title) or nameof(PublishDraft.Description)
                    or nameof(PublishDraft.CoverPath) or nameof(PublishDraft.Meta)
                    or nameof(PublishDraft.IsScheduled) or nameof(PublishDraft.ScheduledAt)
                    or nameof(PublishDraft.IsOriginal))
            {
                NotifyEditorComputed();
            }
        };
    }

    // ═══════════════════════════════════════════════════════════════
    //  导入
    // ═══════════════════════════════════════════════════════════════

    /// <summary>上次导入根目录（设计文档 §9：记住上次，不每次弹选择器）</summary>
    [ObservableProperty] private string? _lastImportRoot;

    /// <summary>选文件夹回调（View 挂载时注入 —— 需要 Window 的 StorageProvider）。
    /// 返回**多个**：选择器允许多选，条数上限在展开之后统一卡（见 <see cref="PublishImportPlanner"/>）。</summary>
    public Func<Task<IReadOnlyList<string>>>? FolderPicker { get; set; }
    /// <summary>选视频文件回调（同样可多选）</summary>
    public Func<Task<IReadOnlyList<string>>>? VideoPicker { get; set; }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        if (FolderPicker is null) return;
        var picked = await FolderPicker();
        if (picked.Count == 0) return;
        await ImportPickedAsync(picked);
    }

    [RelayCommand]
    private async Task AddVideosAsync()
    {
        if (VideoPicker is null) return;
        var picked = await VideoPicker();
        if (picked.Count == 0) return;
        await ImportPickedAsync(picked);
    }

    /// <summary>
    /// 待导入队列（队首 = 下一批）。**不落盘**：候选是磁盘素材的短暂快照，
    /// 视频改名/删除就失效，为它再维护一套持久化与裁剪不划算 ——
    /// 重开软件重选一次即可（去重池会挡掉已经导进来的）。
    /// </summary>
    private readonly List<ImportCandidate> _pending = [];

    /// <summary>最近一次展开的统计（跳过 / 重复 / 提示）。「继续导入」时没有新的展开，摘要复用它</summary>
    private ImportPlan _lastPlan = new([], 0, 0, 0, []);

    /// <summary>待导入条数（绑定用；队列本身不暴露给 UI，免得误绑内部状态）</summary>
    public int PendingCount => _pending.Count;
    public bool HasPending => _pending.Count > 0;

    /// <summary>待导入条上的主文案</summary>
    public string PendingText =>
        $"还有 {PendingCount} 条待导入（每次最多 {PublishImportPlanner.ImportLimit} 条）";

    /// <summary>「继续导入」按钮文案（不足一批时显示实际条数）</summary>
    public string ImportMoreLabel =>
        $"继续导入 {Math.Min(PendingCount, PublishImportPlanner.ImportLimit)} 条";

    /// <summary>导入提示条（params.txt 哪一行没看懂 / 哪个目录没视频 / 重复了几条），有内容才显示</summary>
    [ObservableProperty] private string _importNotice = "";
    public bool HasImportNotice => ImportNotice.Length > 0;

    /// <summary>目录约定说明面板是否展开（**页内折叠**，不新开窗口 —— 不引入新的视图枚举值，四视图互斥不变量不受影响）</summary>
    [ObservableProperty] private bool _showImportHelp;

    /// <summary>最近生成的模板目录（「打开模板文件夹」用；null = 本次会话还没生成过）</summary>
    [ObservableProperty] private string? _templateFolder;
    public bool HasTemplateFolder => !string.IsNullOrEmpty(TemplateFolder);

    partial void OnImportNoticeChanged(string value) => OnPropertyChanged(nameof(HasImportNotice));
    partial void OnTemplateFolderChanged(string? value) => OnPropertyChanged(nameof(HasTemplateFolder));

    /// <summary>选父目录回调（View 挂载时注入 —— 「新建模板」用）</summary>
    public Func<Task<string?>>? ParentFolderPicker { get; set; }

    /// <summary>
    /// 导入的唯一入口 —— 「导入文件夹」「添加视频」、拖放、探针全走这里。
    ///
    /// 展开（<see cref="PublishImportPlanner.Expand"/>，纯函数、只读磁盘）与落地
    /// （<see cref="ApplyPlanAsync"/>，写草稿）在这里分界：展开阶段一个用户状态都不碰，
    /// 所以它在 <c>--selftest</c> 里可测。
    ///
    /// 选中的东西**先进队列再消费一批** —— 一次导入最多落地
    /// <see cref="PublishImportPlanner.ImportLimit"/> 条，多出来的留在队列里等「继续导入」，
    /// 否则选一个含 102 个子目录的根目录会一次吞掉 102 条。
    /// </summary>
    public async Task ImportPickedAsync(IReadOnlyList<string> picked)
    {
        if (picked.Count == 0) return;

        // 记住一个起始目录，供下次选择器定位（§9：日更场景是高频入口）
        if (picked.FirstOrDefault(Directory.Exists) is { } start) LastImportRoot = start;

        IsBusy = true;
        StatusText = "正在导入…";
        try
        {
            // 去重池 = 工作台里的 + 待导入队列里的 —— 重复选同一批不该再进一遍
            var exclude = Drafts.Select(d => d.VideoPath)
                .Concat(_pending.Select(p => p.VideoPath)).ToList();

            _lastPlan = PublishImportPlanner.Expand(picked, exclude);
            SetNotice(_lastPlan);

            // 追加到队尾（不是替换）：用户再选 3 个时，已有的「还剩 92 条」不能被冲掉
            _pending.AddRange(_lastPlan.Candidates);
            NotifyPending();

            await ConsumePendingAsync();
            StatusText = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Publish] 导入失败");
            StatusText = "导入失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>探针/旧调用点用的单目录入口</summary>
    public Task ImportFolderAsync(string root) => ImportPickedAsync([root]);

    /// <summary>
    /// 拖进来的东西 —— 文件夹与视频文件可以混着拖，展开规则与选择器完全一致
    /// （共用 <see cref="ImportPickedAsync"/>，所以 10 条上限、去重、摘要都是同一套）。
    /// </summary>
    public Task ImportDroppedAsync(IReadOnlyList<string> paths) => ImportPickedAsync(paths);

    /// <summary>「继续导入」—— 不重新选文件就接着导，这是队列存在的唯一理由</summary>
    [RelayCommand]
    private async Task ImportMoreAsync()
    {
        if (_pending.Count == 0) return;
        IsBusy = true;
        StatusText = "正在导入…";
        try
        {
            await ConsumePendingAsync();
            StatusText = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Publish] 继续导入失败");
            StatusText = "导入失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>放弃队列里剩下的（素材还在磁盘上，重新选一次就能回来）</summary>
    [RelayCommand]
    private void ClearPending()
    {
        if (_pending.Count == 0) return;
        var dropped = _pending.Count;
        _pending.Clear();
        NotifyPending();
        SubmitMessage = $"已放弃 {dropped} 条待导入条目";
    }

    /// <summary>消费队首一批（≤ 上限）→ 落地 → 刷新底栏摘要。队列空时只刷新摘要。</summary>
    private async Task ConsumePendingAsync()
    {
        var batch = PublishImportPlanner.TakeBatch(_pending);
        if (batch.Count > 0)
        {
            NotifyPending();
            await ApplyPlanAsync(batch);
        }
        SubmitMessage = SummarizeImport(_lastPlan, batch.Count, _pending.Count);
    }

    private void NotifyPending()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(ImportMoreLabel));
    }

    /// <summary>提示条：展开阶段的细节逐条列出（最多 8 条，多的折成一行）</summary>
    private void SetNotice(ImportPlan plan)
    {
        if (plan.Notices.Count == 0) { ImportNotice = ""; return; }

        var lines = plan.Notices.Take(8).ToList();
        if (plan.Notices.Count > 8) lines.Add($"…还有 {plan.Notices.Count - 8} 条提示");
        ImportNotice = string.Join("\n", lines);
    }

    // ═══ 新建模板 / 说明 ═══

    [RelayCommand]
    private void ToggleImportHelp() => ShowImportHelp = !ShowImportHelp;

    /// <summary>
    /// 生成一个可以直接复制改名的「发布单元模板」文件夹，并告诉用户怎么用。
    ///
    /// 「告诉用户」是三件事一起做：底栏报路径 + 说明面板自动展开 + 打开资源管理器。
    /// 打不开资源管理器**不算失败** —— 面板里还留着「打开模板文件夹」按钮，路径也在底栏上。
    /// </summary>
    [RelayCommand]
    private async Task CreateTemplateAsync()
    {
        if (ParentFolderPicker is null) return;
        var parent = await ParentFolderPicker();
        if (string.IsNullOrWhiteSpace(parent)) return;

        var result = PublishUnitTemplate.Write(parent);
        if (result.Error is not null)
        {
            StatusText = "生成模板失败：" + result.Error;
            return;
        }

        TemplateFolder = result.Folder;
        ShowImportHelp = true;
        StatusText = "";
        SubmitMessage = result.AllKept
            ? $"模板已存在，已为你打开：{result.Folder}"
            : $"已生成模板：{result.Folder} —— 复制一份改名，把视频放进去就能导入";

        ShellLauncher.RevealFolder(result.Folder);
    }

    [RelayCommand]
    private void OpenTemplateFolder()
    {
        if (TemplateFolder is { Length: > 0 } dir) ShellLauncher.RevealFolder(dir);
    }

    /// <summary>
    /// 导入落地的**唯一副作用出口**：候选 → 草稿 → 落盘 → 探测 → 后台解析话题。
    /// </summary>
    private async Task ApplyPlanAsync(IReadOnlyList<ImportCandidate> batch)
    {
        if (batch.Count == 0) return;

        var added = new List<PublishDraft>(batch.Count);
        var located = new List<(PublishDraft Draft, string Keyword)>();
        var collected = new List<(PublishDraft Draft, string Name)>();
        foreach (var c in batch)
        {
            // 标题 / 简介 / 封面在展开阶段就按回退链算好了（params.txt → 目录名），
            // 回退链只保留一处，别在这里再写一遍
            var draft = new PublishDraft(c.VideoPath, c.SourceFolder)
            {
                Title = c.Title,
                Description = c.Description ?? "",
                CoverPath = c.CoverPath,
            };
            ApplyGlobalTargetsTo(draft);        // 先播种账号预设
            ApplyUnitParams(draft, c.Params);   // 再让 params.txt 覆盖它

            // 位置要联网解析（一次搜索 → 各平台各拿自己的 POI），落地后统一跑
            if (c.Params.Location is { Length: > 0 } keyword) located.Add((draft, keyword));
            // 合集是按名字查本地已同步的映射（不联网搜平台），同样落地后统一跑
            if (c.Params.Collection is { Length: > 0 } collection) collected.Add((draft, collection));
            added.Add(draft);
        }

        AddDrafts(added);
        await PersistDraftsAsync();
        await ProbeAllAsync(added);
        // 小红书签名宿主提前热 —— 导入的条目常带话题/位置，冷启动那几十秒不该压在搜索那一步
        if (added.FirstOrDefault(d => d.Targets.Any(t => t.PlatformKey == PlatformNames.Xiaohongshu)) is { } xhsDraft)
            WarmXhsSigner(xhsDraft);
        _ = ResolveImportedAsync(added, located, collected);  // fire-and-forget，不挡导入
    }

    /// <summary>
    /// params.txt 的字段 → 草稿。**必须在 <see cref="ApplyGlobalTargetsTo"/> 之后调**：
    /// 后者会把账号预设 clone 进 <c>Overrides</c>，顺序反了预设会把 params 盖掉，
    /// 而且 UI 上完全看不出来。
    /// 标题/简介/封面不在这里 —— 它们在展开阶段就并进候选了。
    /// 实现搬去 <see cref="PublishDraftFactory.ApplyUnitParams"/>（与合成自动发布共用同一份；
    /// 含「原创只写小红书且与预设不同才写」的克制，见工厂里的注释）。
    /// </summary>
    private void ApplyUnitParams(PublishDraft draft, UnitParams p) =>
        PublishDraftFactory.ApplyUnitParams(draft, p);

    /// <summary>
    /// 导入后台补解析（串行，别把解析接口打爆）：话题 → 位置 → 合集。
    ///
    /// 没有它，导入的带话题/带位置/带合集的条目在提交时会被**静默忽略** —— 组装 payload 读的是
    /// <c>Params.Topics</c> / <c>Params.Location</c> / <c>Params.CollectionId</c>（带平台 ID 的
    /// 解析结果），而写回只发生在编辑器里点「添加话题」「搜索」「选合集」的时候。
    /// </summary>
    private async Task ResolveImportedAsync(
        IReadOnlyList<PublishDraft> drafts,
        IReadOnlyList<(PublishDraft Draft, string Keyword)> located,
        IReadOnlyList<(PublishDraft Draft, string Name)> collected)
    {
        var anyResolved = false;

        foreach (var draft in drafts)
        {
            if (draft.Topics.Count == 0) continue;
            try
            {
                await ResolveTopicsCoreAsync(draft);
                anyResolved = true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Publish] 导入后解析话题失败 {File}", draft.FileName);
            }
        }

        // 位置按 (平台 + 关键词) 缓存 —— 一批里同一个位置关键词只搜 4 次，不会每条都打
        foreach (var (draft, keyword) in located)
        {
            try
            {
                await ResolveLocationCoreAsync(draft, keyword);
                anyResolved = true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Publish] 导入后解析位置失败 {File}", draft.FileName);
            }
        }

        // 合集：一批里同一个名字只查一次映射，再逐条铺（一个素材目录一个合集是常态）
        var trouble = new List<string>();
        foreach (var group in collected.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var found = await FindCollectionAsync(group.Key);
            if (found.Item is null)
            {
                trouble.Add(found.Error!);
                continue;
            }

            foreach (var (draft, _) in group)
            {
                try
                {
                    if (ApplyCollection(draft, found.Item) is { } why) trouble.Add(why);
                    else anyResolved = true;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Publish] 导入后铺合集失败 {File}", draft.FileName);
                }
            }
        }

        // 解析结果要落盘，否则重启后又变回「没解析过」
        if (anyResolved) await PersistDraftsAsync();

        // 合集没铺上的原因要说出来：页面上「合集那栏是空的」和「预设里本来就没配」
        // 长得一模一样，不提示就等于没做（提示条本来就是「导入时的各种没看懂」的家）
        if (trouble.Count > 0)
        {
            var lines = trouble.Distinct().Take(3).ToList();
            ImportNotice = ImportNotice.Length == 0
                ? string.Join("\n", lines)
                : ImportNotice + "\n" + string.Join("\n", lines);
        }
    }

    /// <summary>
    /// 按名字找逻辑合集（返回服务端「合集」页同步下来的那份映射）。
    ///
    /// 名字对不上时**不动草稿**（保持账号预设里的合集）：写错一个字就把用户配好的
    /// 合集清掉，比不生效更糟。名字匹配按去空白 + 忽略大小写，与编辑器回填选中态同一口径。
    /// </summary>
    private async Task<(CollectionItem? Item, string? Error)> FindCollectionAsync(string name)
    {
        try
        {
            var resp = await ApiClient.PostAsync<CollectionListResponse>("/api/collections/list");
            var item = resp?.Collections.FirstOrDefault(c =>
                string.Equals(c.Title?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (item is null)
                return (null, $"没找到叫「{name}」的合集 → 这一批保持账号预设里的合集（在「合集」页建好并同步后再重导）");
            // 文章合集不能挂到视频发布（头条文章 collection_id ≠ 视频 pSeries）
            if (item.ContentType == 1)
                return (null, $"「{name}」是文章合集，不能挂到视频发布 → 这一批保持账号预设里的合集（文章合集仅用于图文发布）");
            return (item, null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 合集列表查询失败");
            return (null, $"合集列表没取到（{ex.Message}）→ 这一批保持账号预设里的合集");
        }
    }

    /// <summary>
    /// 把一个逻辑合集铺到草稿的每个目标上（与编辑器里点一次合集下拉**同一份实现**）。
    /// 返回 null = 全部铺上；非 null = 给用户看的原因（哪些账号没同步过）。
    /// </summary>
    private string? ApplyCollection(PublishDraft draft, CollectionItem item)
    {
        var rows = MapCollectionToTargets(draft, item.Title, item.Platforms ?? []);

        // 正在编辑这条就把平台卡、下拉、每账号状态一起刷了 —— 否则导入明明铺上了，
        // 界面上还是空的（2026-09-19：封面就是这么「看起来没填」的）
        if (ReferenceEquals(draft, SelectedDraft))
        {
            RefreshTargetSummaries();   // 平台卡摘要 + 下拉回填
            ShowCollectionRows(rows);   // 每账号一行，刷完平台卡再贴（顺序反了会被清掉）
        }

        var missed = rows.Where(r => r.IsMissing).Select(r => r.AccountLabel).ToList();
        if (missed.Count == rows.Count)
            return $"合集「{item.Title}」没有任何账号同步过 → 这一批不带合集（去「合集」页同步一次）";
        if (missed.Count > 0)
            return $"合集「{item.Title}」有 {missed.Count} 个账号没同步过 → 那些账号不带合集："
                 + string.Join("、", missed.Take(3));
        return null;
    }

    /// <summary>
    /// 导入结果摘要（设计文档 §5.1）—— 跳过、警告、重复、剩余都要说出来，不能装作全都成功。
    /// 0 条时**必须给原因**：页面上什么都不会变，只说「导入 0 条」等于没说。
    /// </summary>
    internal static string SummarizeImport(ImportPlan plan, int imported, int remaining = 0)
    {
        var parts = new List<string> { $"导入 {imported} 条" };
        if (remaining > 0) parts.Add($"还有 {remaining} 条未导入，再点一次导入继续");
        if (plan.SkippedFolders > 0) parts.Add($"跳过 {plan.SkippedFolders} 项（没有视频）");
        if (plan.MultipleVideoWarnings > 0)
            parts.Add($"{plan.MultipleVideoWarnings} 条有警告（目录里有多个视频，取了第一个）");
        if (plan.Duplicates > 0) parts.Add($"跳过 {plan.Duplicates} 条重复");

        // 展开阶段的人话细节（params.txt 哪一行没看懂、哪个目录没视频）走**提示条**，不挤进这一行
        if (imported == 0)
            parts.Add("—— 没找到视频：视频可以直接放在所选目录里，或放进子文件夹（一个子文件夹 = 一条发布单元）");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 把一批草稿加入工作台（导入路径与外部构造的草稿共用同一条落地路径：
    /// 订阅变更 → 插到最前 → 跑预检）。新加的排在最前，符合「刚加的就在眼前」。
    ///
    /// **整批按素材原顺序插到队首**：逐条 <c>Insert(0)</c> 会把一批 10 条倒过来 ——
    /// 用户是按文件夹顺序选的，列表里就该是那个顺序。
    /// </summary>
    public void AddDrafts(IReadOnlyList<PublishDraft> drafts)
    {
        for (var i = drafts.Count - 1; i >= 0; i--)
        {
            AttachDraft(drafts[i]);
            Drafts.Insert(0, drafts[i]);
            RunPreflight(drafts[i]);
        }
        RefreshSummaryCounts();
    }

    /// <summary>单条加入（编辑器复制等路径用）</summary>
    public void AddDraft(PublishDraft draft) => AddDrafts([draft]);

    /// <summary>把当前勾选的账号写进草稿的目标，并按账号预设播种参数
    /// （播种核心在 <see cref="PublishDraftFactory.SeedTargets"/>，与合成自动发布共用）</summary>
    private void ApplyGlobalTargetsTo(PublishDraft draft)
    {
        var targets = GlobalTargets.Where(t => t.IsOn && t.CanPublish)
            .Select(chip => new PublishTarget(chip.PlatformKey, chip.SessionId, chip.AccountLabel)
            {
                IsLoggedIn = chip.IsLoggedIn,
            })
            .ToList();
        PublishDraftFactory.SeedTargets(draft, targets, _defaults);
    }

    /// <summary>后台串行探测元数据 —— 卡片先出「读取视频信息…」再填实</summary>
    private async Task ProbeAllAsync(IReadOnlyList<PublishDraft> drafts)
    {
        foreach (var d in drafts)
        {
            d.IsProbing = true;
            try
            {
                d.Meta = await _probe.ProbeAsync(d.VideoPath);
                RunPreflight(d);       // 元数据到位后有些规则才能判（小红书 ≥10 秒）
            }
            catch (Exception ex) { Log.Warning(ex, "[Publish] 探测失败 {Path}", d.VideoPath); }
            finally { d.IsProbing = false; }
        }
        await PersistDraftsAsync();
        RefreshSummaryCounts();
    }

    // ═══════════════════════════════════════════════════════════════
    //  清空 / 删除
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task ClearDraftsAsync()
    {
        Drafts.Clear();
        await PersistDraftsAsync();
        RefreshSummaryCounts();
    }

    [RelayCommand]
    private async Task RemoveDraftAsync(PublishDraft? draft)
    {
        if (draft is null) return;
        Drafts.Remove(draft);
        await PersistDraftsAsync();
        RefreshSummaryCounts();
    }

    /// <summary>
    /// 批量移除勾选的条目（2026-10-22 用户要求：一条条点「移除」太慢）。
    /// 只删本机草稿 —— 不动视频文件，也不碰任何平台上的内容（与单条「移除」同口径）。
    /// </summary>
    [RelayCommand]
    private async Task RemoveSelectedDraftsAsync()
    {
        var selected = Drafts.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0) return;

        // 正在编辑的那条也在里面：编辑器对象一并放下，免得回退后还挂着一份已删草稿
        if (SelectedDraft is { } editing && selected.Contains(editing)) SelectedDraft = null;

        foreach (var d in selected) Drafts.Remove(d);
        await PersistDraftsAsync();
        RefreshSummaryCounts();
        SubmitMessage = $"已移除 {selected.Count} 条待发布条目（视频文件保留）";
    }

    private async Task PersistDraftsAsync()
    {
        try { await _store.SaveDraftsAsync(Drafts); }
        catch (Exception ex) { Log.Warning(ex, "[Publish] 草稿保存失败"); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  预检
    // ═══════════════════════════════════════════════════════════════

    /// <summary>对单条草稿跑预检（导入后 / 编辑后 / 字段变化时）</summary>
    private void RunPreflight(PublishDraft draft)
    {
        var issues = PublishPreflight.Validate(draft);
        draft.Issues.Clear();
        foreach (var i in issues) draft.Issues.Add(i);
    }

    /// <summary>对全部草稿跑预检（提交前 / 账号状态变化后）</summary>
    private void RunPreflightAll()
    {
        foreach (var d in Drafts) RunPreflight(d);
    }

    // ═══════════════════════════════════════════════════════════════
    //  提交
    // ═══════════════════════════════════════════════════════════════

    /// <summary>提交状态提示（底栏 / toast）</summary>
    [ObservableProperty] private string? _submitMessage;

    /// <summary>
    /// 提交到队列：预检 → 逐条 submit → 从工作台移除（队列页接管）。
    /// **不跳页**（设计文档 D6）—— 原地给结果，底栏继续显示进度。
    /// </summary>
    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsBusy) return;

        RunPreflightAll();
        var selected = Drafts.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SubmitMessage = "没有勾选任何条目";
            return;
        }

        // 有 Error 的条目默认拦下，但**列出原因**并允许用户处理（不静默丢弃）
        var blocked = selected.Where(d => !d.IsSubmittable).ToList();
        var ready = selected.Where(d => d.IsSubmittable).ToList();
        if (ready.Count == 0)
        {
            SubmitMessage = $"{blocked.Count} 条都有待处理问题，无法提交";
            return;
        }

        IsBusy = true;
        SubmitProgress = 0;
        SubmitMessage = null;
        var ok = 0;
        var failed = 0;

        try
        {
            for (var i = 0; i < ready.Count; i++)
            {
                var draft = ready[i];
                StatusText = $"提交中 {i + 1}/{ready.Count}：{draft.FileName}";
                SubmitProgress = (i + 1) * 100.0 / ready.Count;

                // 算哈希要读整个视频文件，放在这里 await（不占 UI 线程）——
                // 进度条与「提交中 i/N」照常推进
                var request = await BuildSubmitRequestAsync(draft);
                if (request is null) { failed++; continue; }

                try
                {
                    var resp = await _pipeline.SubmitAsync(request);
                    if (resp is null) { failed++; continue; }

                    // 提交成功的条目从工作台移除 —— 之后由队列页接管
                    Drafts.Remove(draft);
                    ok++;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Publish] 提交失败 {File}", draft.FileName);
                    failed++;
                }
            }

            await PersistDraftsAsync();
            RefreshSummaryCounts();

            SubmitMessage = failed == 0
                ? $"已加入队列 · {ok} 条视频"
                : $"已加入队列 {ok} 条，失败 {failed} 条";
            StatusText = SubmitMessage;
        }
        finally
        {
            IsBusy = false;
            SubmitProgress = 0;
        }
    }

    /// <summary>
    /// 把一条草稿组装成 submit 请求 —— 每个启用目标一份 wire 载荷。
    /// 这里**不做网络解析**：话题/位置已经在编辑期解析好并存进了 PlatformParams，
    /// 提交只是本地组装 + 算一次视频哈希。
    /// 实现搬去 <see cref="PublishDraftFactory.BuildSubmitRequestAsync"/>（与合成自动发布共用同一份）。
    /// </summary>
    private Task<SubmitPublishRequest?> BuildSubmitRequestAsync(PublishDraft draft) =>
        PublishDraftFactory.BuildSubmitRequestAsync(draft, _defaults);

    private void OnProgress(string platformKey, string stage, int pct, string? message)
    {
        if (string.IsNullOrWhiteSpace(platformKey) || string.IsNullOrWhiteSpace(message)) return;
        Dispatcher.UIThread.Post(() => StatusText = message);
    }

    private void OnPlatformComplete(string platformKey, bool success, string? videoId, string? error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var name = PlatformNames.GetDisplayName(platformKey);
            StatusText = success ? $"{name} 发布成功" : $"{name} 发布失败：{error}";
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //  预设
    // ═══════════════════════════════════════════════════════════════

    public ObservableCollection<PresetRowViewModel> PresetRows { get; } = [];

    private void BuildPresetRows()
    {
        PresetRows.Clear();
        foreach (var chip in GlobalTargets)
        {
            var key = PublishDraftStore.KeyOf(chip.PlatformKey, chip.SessionId);
            _defaults.TryGetValue(key, out var preset);
            PresetRows.Add(new PresetRowViewModel
            {
                SessionKey = key,
                SessionId = chip.SessionId,
                PlatformKey = chip.PlatformKey,
                PlatformName = chip.PlatformName,
                AccountLabel = chip.AccountLabel,
                BrandHex = chip.BrandHex,
                SummaryText = PresetSummary(preset),
            });
        }
    }

    private static string PresetSummary(PlatformParams? p)
    {
        if (p is null) return "尚未配置 —— 跟随全局默认";

        var parts = new List<string>();
        parts.Add(p.CollectionName is { Length: > 0 } c ? $"合集 {c}" : "合集 —");
        parts.Add(p.Location is { } loc ? $"位置 {loc.Name}" : "位置 —");
        parts.Add(p.Visibility is { } v ? $"可见性 {VisibilityText(v)}" : "可见性 公开");
        parts.Add(p.AllowDownload ?? true ? "允许下载 ✓" : "禁止下载");
        if (p.ShowInNearby) parts.Add("同城可见 ✓");
        if (p.IsOriginal == true) parts.Add("原创声明 ✓");
        if (p.Topics.Count > 0) parts.Add($"话题 {p.Topics.Count} 个");
        return string.Join(" · ", parts);
    }

    private static string VisibilityText(PublishVisibility v) => v switch
    {
        PublishVisibility.Private => "仅自己",
        PublishVisibility.MutualFriends => "仅互关好友",
        PublishVisibility.OnlyUsers => "只给谁看",
        PublishVisibility.ExcludeUsers => "不给谁看",
        _ => "公开",
    };

    /// <summary>账号解绑后清理不再存在的预设（在服务端 recreate 同 id 时尤其重要）</summary>
    private async Task PruneDefaultsAsync()
    {
        try
        {
            var alive = GlobalTargets.Select(t => t.SessionId).ToList();
            if (await _store.PruneAsync(alive) > 0)
            {
                _defaults.Clear();
                foreach (var (k, v) in await _store.LoadDefaultsAsync()) _defaults[k] = v;
            }
            // 勾选遗骸同批清理（合成页自动发布读的是同一份存档）
            await _chips.PruneAsync(alive);
        }
        catch (Exception ex) { Log.Warning(ex, "[Publish] 预设清理失败"); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  生命周期
    // ═══════════════════════════════════════════════════════════════

    /// <summary>进页加载的在飞任务（首次 = LoadAsync，之后 = RefreshAccountsAsync）</summary>
    private Task _navigatedTask = Task.CompletedTask;

    /// <summary>
    /// 排干进页加载。给 <c>--smoke</c>：造景假芯片前必须等它落定 ——
    /// 进页是 fire-and-forget，晚到的账号芯片重建会把造景洗掉，
    /// 组条断言就会间歇性炸「点组名没把勾选换成该组」（2026-10-26 打包门禁实测）。
    /// 永不抛：进页失败不该把调用方带崩。
    /// </summary>
    internal async Task DrainNavigatedAsync()
    {
        try { await _navigatedTask; } catch { /* 见上 */ }
    }

    /// <summary>首次进页做重活；之后每次进页只刷账号（保留草稿与编辑态）</summary>
    public void OnNavigatedTo()
    {
        if (_loaded) { _navigatedTask = RefreshAccountsAsync(); return; }
        _loaded = true;
        _navigatedTask = LoadAsync();
    }

    private async Task RefreshAccountsAsync()
    {
        await LoadAccountsAsync();
        await PruneDefaultsAsync();
        RunPreflightAll();
        RefreshSummaryCounts();
        if (ShowPresets) BuildPresetRows();
        if (ShowOrder) BuildOrderRows();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        StatusText = "正在加载…";
        try
        {
            foreach (var (k, v) in await _store.LoadDefaultsAsync()) _defaults[k] = v;
            // 账号顺序必须在 LoadAccountsAsync 之前读出来 —— 它要拿这份顺序排账号芯片
            _userOrder = await _orderStore.LoadAsync();
            await LoadAccountsAsync();
            await PruneDefaultsAsync();
            await PruneOrderAsync();
            await LoadCollectionOptionsAsync();
            await RestoreDraftsAsync();
            RefreshSummaryCounts();
            StatusText = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Publish] 加载失败");
            StatusText = "加载失败：" + ex.Message;
        }
        finally { IsBusy = false; }

        // 账号坞同步（用户去登录完成）→ 重拉账号 + 重跑预检，让过期横幅自动消失
        _dock.SessionSynced += OnSessionSynced;
    }

    private void OnSessionSynced(Guid sessionId)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            await RefreshAccountsAsync();
            SubmitMessage = "账号已更新，可以重新提交了";
        });
    }

    private async Task RestoreDraftsAsync()
    {
        var restored = await _store.LoadDraftsAsync();

        // 按视频路径去重：启动加载是异步的（前面还排着网络请求），用户完全可能
        // 在它跑完之前就点了「导入文件夹」—— 那批草稿刚落盘、又被这里读回来，
        // 就会变成一份重复条目（实测：导入 13 条后恢复又把同样 13 条加了一遍）。
        var existing = Drafts.Select(d => d.VideoPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var d in restored)
        {
            if (!existing.Add(d.VideoPath)) continue;
            // 恢复的草稿目标可能已失效（账号解绑）—— 保留但预检会报出来
            AttachDraft(d);
            Drafts.Add(d);
        }
        // 草稿存的是**保存当时**的目标顺序。不重排的话，用户排好账号顺序后
        // 会看到「工作台整齐、点进草稿还是乱的」。
        SortAllDraftTargets();
        if (restored.Count > 0) await ProbeAllAsync(restored);
    }
}
