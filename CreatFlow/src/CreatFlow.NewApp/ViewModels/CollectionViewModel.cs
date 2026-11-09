using System.Collections.Specialized;
using System.ComponentModel;
using CreatFlow.NewApp.Models;
using CreatFlow.NewApp.Services;
using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 合集管理页 ViewModel（老 App CollectionViewModel 业务流移植 + 整页双视图重构，2026-09-09）。
///
/// 双视图：列表（Cards 网格）⇄ 编辑器（ShowEditor，新建/编辑共用一套表单）。
/// 页面语义与老版逐条对齐：
///   - 新建：服务端建合集 + 每平台 Pending 记录 → 立即回落列表 → 后台并行平台 SDK
///     创建合集（CollectionSyncService）→ 逐平台 report 回填 → 完成自动刷新列表。
///   - 编辑：先更新 DB 元数据 → 已有 platformCollectionId 的平台走 SDK Update /
///     无 id（Pending/Failed）走 SDK Create / 本次新勾选的平台先 POST /collections/sync
///     建档再 SDK Create → 后台并行。⚠️ 取消勾选某平台不删除平台侧合集（老语义，
///     服务端无移除端点）。
///   - 删除：先拉 detail → 已同步平台逐平台 SDK 删除 → 再删本地 DB（平台删除失败
///     仅提示仍删本地）。两段式确认防误触。
///   - 重试：detail 中 Pending/Failed 且有会话的平台重新 SDK Create。
///   - 抖音 / 头条必选封面：HasCoverPlatform 勾选时表单封面区提示必选；
///     标题上下限按勾选平台收紧（快手 12 字、头条 5~30 字）。
///
/// 界面约定：零 emoji —— 状态一律色块/色点（PlatformSyncProgress.DotHex 等），
/// 操作反馈走红/绿状态条（IsStatusError 语义与平台页一致）。
/// </summary>
public partial class CollectionViewModel : ViewModelBase, INavigationAware
{
    private readonly CollectionSyncService _syncService = new();

    // ═══ 列表态 ═══

    /// <summary>合集卡（每次刷新重建 —— 服务端记录不可变，卡内仅删除确认态可变）</summary>
    public ObservableCollection<CollectionCard> Cards { get; } = [];

    /// <summary>平台元信息（列表卡 chip 上色/中文名；失败保留空字典走兜底）</summary>
    private Dictionary<string, PlatformLookup> _platformMeta = [];

    /// <summary>会话显示名（会话 ID → 账号备注/昵称；列表卡账号行 + 重试/删除的账号标签）</summary>
    private Dictionary<Guid, string> _accountLabels = [];

    /// <summary>平台 → 已绑定会话 ID（列表卡「未同步·可补建」候选行来源）</summary>
    private Dictionary<string, IReadOnlyList<Guid>> _boundSessions = [];

    /// <summary>卡片平台分组展开态（key = {合集ID}:{平台key}；刷新/同步完成后保持展开状态）</summary>
    private readonly Dictionary<string, bool> _expandedPrefs = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isStatusError;

    /// <summary>顶部汇总（主次两段：合集数 SemiBold / 账号覆盖 muted —— 汇总条内联呈现）</summary>
    public string SummaryCountText => $"共 {Cards.Count} 个合集";
    public string SummaryAccountsText => _summaryAccounts;
    private string _summaryAccounts = "";

    // ═══ 多选（批量删除） ═══

    /// <summary>删除防重入（单卡与批量共用一道闸 —— 同账号并发删是 403 的来源）</summary>
    private bool _deleteInFlight;

    /// <summary>
    /// 正在后台删的合集 ID（= 服务端那边还没进终态的子项所在组）。
    /// 卡片乐观藏起来，集合的权威来源是服务端 —— 见 VideoManageViewModel 里同一段注释。
    /// </summary>
    private readonly HashSet<Guid> _pendingDeleteIds = [];

    /// <summary>
    /// 删除失败未收尾的合集 → 失败条数（卡上挂「删除未完成」警示条）。
    /// 与 <see cref="_pendingDeleteIds"/> 相对：在飞的要藏，失败的反而要**醒目地放回来**
    /// —— 卡看着正常、其实是因为 1 条子项失败被卡住，用户没有任何解释与出路（2026-10-14 实测）。
    /// </summary>
    private readonly Dictionary<Guid, int> _failedDeleteCounts = [];

    /// <summary>卡上「去处理」→ 异步任务页（壳在 Navigate 时注入；页 VM 不认识壳）</summary>
    public Action<string>? OnNavigateRequested { get; set; }

    [RelayCommand]
    private void GoTasks() => OnNavigateRequested?.Invoke("tasks");

    /// <summary>有卡在删时定时回查，任务跑完当场刷新（失败的卡放回来）</summary>
    private Timer? _settleTimer;

    private static readonly TimeSpan SettleInterval = TimeSpan.FromSeconds(4);

    /// <summary>后台还有多少个合集在删（页面顶部的提示行）</summary>
    public bool ShowBackgroundWork => _pendingDeleteIds.Count > 0;
    public string BackgroundWorkText => $"正在后台删除 {_pendingDeleteIds.Count} 个合集…";

    private void NotifyInFlight()
    {
        OnPropertyChanged(nameof(ShowBackgroundWork));
        OnPropertyChanged(nameof(BackgroundWorkText));
    }

    /// <summary>拉一次「还在飞的合集 ID + 删除失败的合集组」，与别的端和重启后的自己对齐</summary>
    private async Task SyncInFlightAsync()
    {
        try
        {
            var (inFlight, failed) = await AsyncTaskApi.CollectionInFlightAsync();
            _pendingDeleteIds.Clear();
            foreach (var id in inFlight) _pendingDeleteIds.Add(id);
            _failedDeleteCounts.Clear();
            foreach (var (id, n) in failed) _failedDeleteCounts[id] = n;
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            /* 未登录：静默 */
        }
        catch (Exception ex)
        {
            // 拉不到就保留旧集合：宁可多藏几张，也别让删到一半的卡全冒出来
            Log.Warning(ex, "[Collection] 拉在飞合集失败（保留旧集合）");
        }
        NotifyInFlight();
        EnsureSettleWatch();
    }

    private void EnsureSettleWatch()
    {
        if (_pendingDeleteIds.Count == 0)
        {
            _settleTimer?.Dispose();
            _settleTimer = null;
            return;
        }
        _settleTimer ??= new Timer(
            _ => Dispatcher.UIThread.Post(() => _ = PollSettleAsync()),
            null, SettleInterval, SettleInterval);
    }

    private async Task PollSettleAsync()
    {
        var before = _pendingDeleteIds.Count;
        await SyncInFlightAsync();
        // 条数变了 = 有合集跑完了 → 重载列表：成功的不见了，失败的卡会被放回来
        if (_pendingDeleteIds.Count != before) await LoadAsync(quiet: true);
    }

    public int SelectedCount => Cards.Count(c => c.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public string SelectionText => HasSelection ? $"已选 {SelectedCount} 个" : "";
    public bool AllSelected => Cards.Count > 0 && Cards.All(c => c.IsSelected);
    public string SelectAllText => AllSelected ? "取消全选" : "全选";
    public string DeleteSelectedText => HasSelection ? $"删除选中 {SelectedCount} 个" : "删除选中";

    /// <summary>选中态散在每张卡上，页面级计数是算出来的 —— 卡片播报后才刷新</summary>
    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(SelectAllText));
        OnPropertyChanged(nameof(DeleteSelectedText));
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectionCard.IsSelected)) NotifySelection();
    }

    /// <summary>全选 / 取消全选（同一个按钮两态，所以不另开一个「清空选择」）</summary>
    [RelayCommand]
    private void SelectAll()
    {
        var on = !AllSelected;
        foreach (var c in Cards) c.IsSelected = on;
        NotifySelection();
    }

    /// <summary>
    /// 批量删除选中的合集。卡间**串行**（每张卡内部已经是并行的多账号删除）——
    /// 多张卡同时删会在同一个账号上撞车，那正是同账号并发 403 的成因。
    /// </summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (IsSyncing || _deleteInFlight) return;
        var cards = Cards.Where(c => c.IsSelected).ToList();
        if (cards.Count == 0) return;
        foreach (var c in cards) c.IsSelected = false;
        NotifySelection();
        await DeleteManyAsync(cards);
    }

    /// <summary>账号覆盖率（0-100，汇总条细进度条数据源；无目标账号 → 0）</summary>
    public double SyncRatioValue => _summaryCovered == 0
        ? 0
        : Math.Round(_summarySynced * 100.0 / _summaryCovered);
    private int _summarySynced;
    private int _summaryCovered;

    /// <summary>是否有合集（空态可见性）</summary>
    public bool HasCollections => Cards.Count > 0;

    /// <summary>卡片网格列数：仅 1 个合集时整行铺满（避免单卡孤悬半屏、右侧大片空白）</summary>
    public int CardColumns => Cards.Count > 1 ? 2 : 1;

    /// <summary>空态可见（非加载、无错误、无合集）</summary>
    public bool ShowEmpty => !IsLoading && !HasError && !HasCollections;

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(HasCollections));
    }
    partial void OnErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowEmpty));
    }
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    // ═══ 编辑器态 ═══

    /// <summary>true = 编辑器视图（新建/编辑）；false = 列表视图</summary>
    [ObservableProperty]
    private bool _showEditor;

    /// <summary>是否编辑模式（false = 新建）</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>
    /// 合集内容类型：0=视频 1=文章（服务端 CollectionContentType）。
    /// 文章合集仅头条；新建时可选，编辑时只读（视频 AlbumId 与文章 collection_id 是两个对象）。
    /// </summary>
    [ObservableProperty]
    private int _currentContentType;

    public bool IsArticleCollection => CollectionContentTypeRules.IsArticle(CurrentContentType);

    public bool IsVideoCollection => !IsArticleCollection;

    /// <summary>编辑模式类型只读（卡片/详情里回显）</summary>
    public bool IsContentTypeLocked => IsEditing;

    /// <summary>新建模式显示视频/文章切换</summary>
    public bool ShowContentTypePicker => !IsEditing;

    public string ContentTypeLabel => CollectionContentTypeRules.Label(CurrentContentType);

    /// <summary>「发布到」区域的说明文案（文章模式明说只支持头条）</summary>
    public string PlatformPickerHint => IsArticleCollection
        ? "文章合集仅支持头条 —— 头条每个账号各建一个文章合集"
        : "勾选平台与账号 —— 每账号独立创建合集，可一次多选";

    partial void OnCurrentContentTypeChanged(int value)
    {
        ApplyContentTypeConstraints();
        NotifyContentTypeProps();
    }

    partial void OnIsEditingChanged(bool value) => NotifyContentTypeProps();

    private void NotifyContentTypeProps()
    {
        OnPropertyChanged(nameof(IsArticleCollection));
        OnPropertyChanged(nameof(IsVideoCollection));
        OnPropertyChanged(nameof(IsContentTypeLocked));
        OnPropertyChanged(nameof(ShowContentTypePicker));
        OnPropertyChanged(nameof(ContentTypeLabel));
        OnPropertyChanged(nameof(PlatformPickerHint));
    }

    /// <summary>视频/文章类型切换（新建时）</summary>
    [RelayCommand]
    private void SetVideoType() => CurrentContentType = (int)CollectionContentType.Video;

    [RelayCommand]
    private void SetArticleType()
    {
        if (IsContentTypeLocked) return;
        CurrentContentType = (int)CollectionContentType.Article;
    }

    /// <summary>
    /// 按当前内容类型收敛平台勾选：文章 → 强制勾头条、其余平台取消并禁用；
    /// 视频 → 全部恢复可选。幂等，可在平台列表重建后重复调用。
    /// </summary>
    private void ApplyContentTypeConstraints()
    {
        foreach (var target in PlatformTargets)
        {
            var canSelect = CollectionContentTypeRules.CanSelectPlatform(
                CurrentContentType, target.PlatformKey);
            target.IsEnabled = canSelect;

            if (!canSelect && target.IsSelected)
                target.IsSelected = false;
            else if (canSelect && IsArticleCollection
                     && target.PlatformKey == PlatformNames.Toutiao
                     && !target.IsSelected)
                target.IsSelected = true;   // 文章合集必须挂头条（内部会全选账号）
        }

        UpdatePlatformRequirements();
    }

    /// <summary>编辑器标题（新建合集 / 编辑合集）</summary>
    [ObservableProperty]
    private string _editorTitle = "";

    /// <summary>合集标题（双向绑定）</summary>
    [ObservableProperty]
    private string _collectionTitle = "";

    /// <summary>合集简介（双向绑定）</summary>
    [ObservableProperty]
    private string _collectionDescription = "";

    /// <summary>本次勾选的平台（标题上下限按这些平台取交集）</summary>
    private List<string> SelectedPlatformKeys =>
        PlatformTargets.Where(t => t.IsSelected).Select(t => t.PlatformKey).ToList();

    /// <summary>生效标题上限：勾选快手时 12、单勾头条时 30，其余 20（多平台取最小）</summary>
    public int EffectiveTitleMaxLength => CollectionTitleRules.EffectiveMaxLength(SelectedPlatformKeys);

    /// <summary>生效标题下限：勾选头条时 5，其余 1（多平台取最大）</summary>
    public int EffectiveTitleMinLength => CollectionTitleRules.EffectiveMinLength(SelectedPlatformKeys);

    /// <summary>标题字数计数（表单右侧实时显示；上限按勾选平台变化）</summary>
    public string TitleCountText => $"{CollectionTitle.Length}/{EffectiveTitleMaxLength}";

    /// <summary>标题实时问题提示（含符号 / 超长 / 过短）；空 = 没问题</summary>
    public string TitleHintText
    {
        get
        {
            if (string.IsNullOrEmpty(CollectionTitle)) return "";
            var text = CollectionTitleRules.Normalize(CollectionTitle);
            if (CollectionTitleRules.HasInvalidChar(text))
                return "合集标题不能用符号，只能使用文字、数字和空格";

            var keys = SelectedPlatformKeys;
            var max = CollectionTitleRules.EffectiveMaxLength(keys);
            if (text.Length > max)
                return $"合集标题最多 {max} 个字（当前 {text.Length} 个）";
            var min = CollectionTitleRules.EffectiveMinLength(keys);
            return text.Length < min
                ? $"合集标题至少 {min} 个字（当前 {text.Length} 个）"
                : "";
        }
    }

    public bool HasTitleHint => TitleHintText.Length > 0;

    /// <summary>简介字数计数（同上；上限对齐 x_collections.description VARCHAR(500)）</summary>
    public string DescCountText => $"{CollectionDescription.Length}/500";

    partial void OnCollectionTitleChanged(string value)
    {
        OnPropertyChanged(nameof(TitleCountText));
        OnPropertyChanged(nameof(TitleHintText));
        OnPropertyChanged(nameof(HasTitleHint));
    }

    partial void OnCollectionDescriptionChanged(string value) => OnPropertyChanged(nameof(DescCountText));

    /// <summary>封面图本地路径</summary>
    [ObservableProperty]
    private string _coverImagePath = "";

    /// <summary>编辑器内验证/加载错误（就地红条，不进底部状态条）</summary>
    [ObservableProperty]
    private string _formError = "";

    /// <summary>平台目标加载中（编辑器右侧骨架行）</summary>
    [ObservableProperty]
    private bool _isTargetsLoading;

    /// <summary>勾选了需要封面的平台（抖音 / 今日头条）</summary>
    [ObservableProperty]
    private bool _hasCoverPlatform;

    /// <summary>保存中（按钮禁用 + 文案「正在保存…」）</summary>
    [ObservableProperty]
    private bool _isSaving;

    /// <summary>后台平台同步中（顶部 chips 面板 / 操作防重入）</summary>
    [ObservableProperty]
    private bool _isSyncing;

    /// <summary>可勾选平台目标列表（编辑器右侧数据源）</summary>
    public ObservableCollection<PlatformTargetItem> PlatformTargets { get; } = [];

    /// <summary>每平台实时同步进度（列表顶部 chips 面板数据源）</summary>
    public ObservableCollection<PlatformSyncProgress> SyncProgressList { get; } = [];

    /// <summary>同步进度面板可见</summary>
    public bool HasSyncProgress => SyncProgressList.Count > 0;

    /// <summary>封面选图回调（由 View 注入，需 TopLevel.StorageProvider）</summary>
    public Func<Task<string?>>? BrowseCoverImageAsync { get; set; }

    /// <summary>编辑中的合集 ID / 服务端已建档的平台记录（编辑预选与 update/create 分类用）</summary>
    private Guid? _editingId;
    private List<CollectionPlatformItem> _editingPlatformRecords = [];

    public CollectionViewModel()
    {
        PlatformTargets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPlatformTargets));
        SyncProgressList.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove)
                OnPropertyChanged(nameof(HasSyncProgress));
        };
    }

    /// <summary>页头图标（矢量）</summary>
    public MaterialIconKind HeaderIcon => MaterialIconKind.PlaylistMusic;

    public bool HasPlatformTargets => PlatformTargets.Count > 0;

    public void OnNavigatedTo()
    {
        // 每次进页刷新（后台同步等可能在离开期间完成）；列表/编辑器字段互不影响，
        // LoadAsync 有 IsLoading 防重入。401 会话失效由壳统一处理。
        // 先对齐「哪些合集还在后台删」，再拉列表 —— 顺序反了会先渲染出已经删掉的卡
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        await SyncInFlightAsync();
        await LoadAsync();
    }

    // ═══ 列表加载 ═══

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    /// <summary>拉平台配置（元信息）+ 合集列表，重建卡片。401 会话失效由壳统一处理，静默。</summary>
    private async Task LoadAsync(bool quiet = false)
    {
        if (IsLoading) return;
        IsLoading = true;
        if (!quiet)
        {
            Error = "";
            StatusMessage = "";
        }
        try
        {
            // 平台元信息（chips 中文名/品牌色）—— 失败静默保留旧值走兜底
            try
            {
                var configs = await ApiClient.GetAsync<List<PlatformConfigItem>>("/api/config/platforms");
                if (configs is { Count: > 0 })
                {
                    _platformMeta = configs.ToDictionary(
                        c => c.PlatformKey,
                        c => new PlatformLookup(c.DisplayName, c.BrandColorHex));
                }
            }
            catch (ApiException ex) when (ex.StatusCode == 401) { }
            catch { }

            // 会话镜像（列表卡账号行：记录账号标签 + 绑定但未建记录的「可补建」候选）
            try
            {
                var sessions = await ApiClient.PostAsync<PlatformSessionListResponse>(
                    "/api/platform-sessions/list");
                if (sessions?.Sessions != null)
                {
                    _accountLabels = sessions.Sessions
                        .Where(s => s.Id != Guid.Empty)
                        .ToDictionary(s => s.Id, SessionDisplayName);
                    _boundSessions = sessions.Sessions
                        .Where(s => s.Id != Guid.Empty && !string.IsNullOrEmpty(s.PlatformKey))
                        .GroupBy(s => s.PlatformKey)
                        .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(s => s.Id).ToList());
                }
            }
            catch (ApiException ex) when (ex.StatusCode == 401) { }
            catch { }

            CollectionListResponse? resp;
            try
            {
                resp = await ApiClient.PostAsync<CollectionListResponse>("/api/collections/list");
            }
            catch (ApiException ex) when (ex.StatusCode == 401)
            {
                return;
            }

            Cards.Clear();
            foreach (var c in resp?.Collections ?? [])
            {
                // 正在后台删的合集**不给它建卡** —— 服务端那边还没删完，
                // 照常建的话它会在列表里原地复活（视频页同一段逻辑见 BuildRows）
                if (_pendingDeleteIds.Contains(c.Id)) continue;

                var card = new CollectionCard(c, _platformMeta, _boundSessions, _accountLabels, _expandedPrefs);
                card.FailedDeleteCount = _failedDeleteCounts.GetValueOrDefault(c.Id);
                card.PropertyChanged += OnCardPropertyChanged;
                Cards.Add(card);
            }
            NotifySelection();   // 卡片整批重建，选中计数跟着归零
            NotifyInFlight();
            UpdateSummary();
        }
        catch (Exception ex)
        {
            Error = $"加载失败：{ex.Message}";
        }
        finally
        {
            OnPropertyChanged(nameof(HasCollections));
            IsLoading = false;
        }
    }

    private void UpdateSummary()
    {
        var synced = Cards.Sum(c => c.SyncedCountForSummary);
        var covered = Cards.Sum(c => c.TotalCountForSummary);
        _summarySynced = synced;
        _summaryCovered = covered;
        _summaryAccounts = covered == 0
            ? "尚未同步到任何账号"
            : synced == covered
                ? $"{synced} 个账号已同步"
                : $"{synced}/{covered} 个账号已同步";
        OnPropertyChanged(nameof(SummaryCountText));
        OnPropertyChanged(nameof(SummaryAccountsText));
        OnPropertyChanged(nameof(SyncRatioValue));
        OnPropertyChanged(nameof(CardColumns));
    }

    /// <summary>会话显示名（备注 > 平台昵称 > 未命名账号）—— 口径收在 <see cref="AccountLabels"/></summary>
    private static string SessionDisplayName(PlatformSessionItem s) => AccountLabels.DisplayName(s);

    // ═══ 打开新建 / 编辑 ═══

    [RelayCommand]
    private Task ShowCreateDialog() => OpenEditorAsync(isEditing: false, card: null);

    [RelayCommand]
    private Task EditCollection(CollectionCard? card) =>
        card == null ? Task.CompletedTask : OpenEditorAsync(isEditing: true, card);

    /// <summary>切换到编辑器视图并加载平台目标；编辑模式附带 detail 预选。</summary>
    private async Task OpenEditorAsync(bool isEditing, CollectionCard? card)
    {
        if (IsSyncing) return; // 后台同步中不允许编辑（防与列表刷新/记录状态打架，老语义同）
        IsEditing = isEditing;
        _editingId = card?.Id;
        // 编辑：类型从卡片带出来（列表 DTO 已含 ContentType），详情返回后再校准；
        // 新建：默认视频。必须在 LoadPlatformTargetsAsync 之前设好 —— 平台列表重建后
        // 要按类型立刻收敛可选范围。
        CurrentContentType = isEditing ? card?.ContentType ?? 0 : 0;
        EditorTitle = isEditing ? "编辑合集" : "新建合集";
        CollectionTitle = card?.Title ?? "";
        CollectionDescription = card?.Description ?? "";
        CoverImagePath = card?.CoverImagePath ?? "";
        FormError = "";
        IsStatusError = false;
        StatusMessage = "";
        ShowEditor = true;

        // 老合集可能超过新上限/带符号：明确提示要改，别等保存时才发现（也不静默改标题）
        if (isEditing && card is not null
            && CollectionTitleRules.Validate(card.Title) is { } legacyTitleError)
        {
            FormError = $"原合集标题不符合新规则：{legacyTitleError}，请修改后再保存";
        }

        await LoadPlatformTargetsAsync();

        if (isEditing && card != null)
        {
            // 拉合集详情 → 预选已有平台记录（会话已被解绑时 target 无此会话 → 保持未选，表单提示）
            try
            {
                var detail = await ApiClient.PostAsync<CollectionDetail>("/api/collections/detail",
                    new GetCollectionDetailRequest(card.Id));
                if (detail?.Platforms != null)
                {
                    _editingPlatformRecords = detail.Platforms;
                    // 详情是类型权威来源（列表卡可能是旧数据/被编辑过）
                    CurrentContentType = detail.ContentType;
                    // 预选 = 勾选有记录的平台，账号按记录精确勾选（记录含已解绑会话时该账号不在
                    // 列表中 → 自然不勾；记录会话已被服务端级联清引用时 PlatformSessionId 为 null）
                    var recordKeys = detail.Platforms.Select(p => p.PlatformKey).ToHashSet();
                    foreach (var target in PlatformTargets.Where(t => recordKeys.Contains(t.PlatformKey)))
                    {
                        target.IsSelected = true; // 内部自动全选 → 下方按记录精修覆盖
                        foreach (var session in target.Sessions)
                        {
                            session.Selected = detail.Platforms.Any(p =>
                                p.PlatformKey == target.PlatformKey
                                && p.PlatformSessionId == session.SessionId);
                        }
                        target.NotifySessionSelectionChanged();
                    }
                }
            }
            catch { /* 详情拉取失败不阻塞编辑 — 记录将按新建目标处理 */ }
        }
        UpdatePlatformRequirements();
    }

    /// <summary>返回列表视图（不保存。后台若仍有同步会继续并刷新列表）</summary>
    [RelayCommand]
    private void BackToList()
    {
        ShowEditor = false;
        FormError = "";
    }

    /// <summary>
    /// 加载启用平台配置 + 会员会话列表 → 构建 PlatformTargets。
    /// 会话只读快照（与服务端一致）；勾选单会话自动选中、多会话待选。
    /// </summary>
    private async Task LoadPlatformTargetsAsync()
    {
        PlatformTargets.Clear();
        _editingPlatformRecords = [];
        IsTargetsLoading = true;
        try
        {
            List<PlatformConfigItem>? configs;
            try
            {
                configs = await ApiClient.GetAsync<List<PlatformConfigItem>>("/api/config/platforms");
            }
            catch (ApiException ex) when (ex.StatusCode == 401) { configs = null; }

            if (configs is not { Count: > 0 })
            {
                FormError = "未能获取平台配置，请检查网络后重试";
                return;
            }

            // 会话列表（未登录任何平台也正常 —— 勾选时引导）
            PlatformSessionListResponse? sessionsResp = null;
            try
            {
                sessionsResp = await ApiClient.PostAsync<PlatformSessionListResponse>(
                    "/api/platform-sessions/list");
            }
            catch (ApiException ex) when (ex.StatusCode == 401) { }
            catch { }

            // 合集链路白名单（2026-11-05 起含头条；B站暂未接，见 PlatformNames.Collections）
            // —— 别用 Publishing，那份是发布链路，头条能建合集但还不能发视频。
            foreach (var config in configs.Where(c => PlatformNames.SupportsCollections(c.PlatformKey)))
            {
                var item = new PlatformTargetItem
                {
                    PlatformKey = config.PlatformKey,
                    DisplayName = config.DisplayName,
                    BrandColorHex = config.BrandColorHex,
                };
                foreach (var s in sessionsResp?.Sessions
                             .Where(s => s.PlatformKey == config.PlatformKey) ?? [])
                {
                    var option = new SessionOption
                    {
                        SessionId = s.Id,
                        Label = SessionDisplayName(s),
                        Status = s.Status,
                    };
                    // 账号勾选变化 → 刷新平台行尾计数/保存校验（平台行勾选 → 自动全选）
                    option.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(SessionOption.Selected))
                        {
                            item.NotifySessionSelectionChanged();
                            UpdatePlatformRequirements(); // 勾选变化不影响封面，仅防漏通知，成本可忽略
                        }
                    };
                    item.Sessions.Add(option);
                }
                // 多账号体系：平台勾选时自动全选该平台账号（PlatformTargetItem.OnIsSelectedChanged），
                // 此处不做预选 —— 编辑模式的预选按已有记录精修（见 OpenEditorAsync）

                item.PropertyChanged += (_, _) => UpdatePlatformRequirements();
                PlatformTargets.Add(item);
            }
        }
        catch (Exception ex)
        {
            FormError = $"加载平台失败：{ex.Message}";
        }
        finally
        {
            IsTargetsLoading = false;
            // 平台列表重建后按当前类型收敛（文章 → 仅头条可选；内部含 UpdatePlatformRequirements）
            ApplyContentTypeConstraints();
        }
    }

    /// <summary>按会话 ID 反查账号名（会话镜像 _accountLabels；查不到返回空 —— 显示退化为仅平台名）</summary>
    private string SessionLabelFor(Guid? sessionId)
        => sessionId != null ? _accountLabels.GetValueOrDefault(sessionId.Value, "") : "";

    /// <summary>
    /// 平台勾选变化后的统一刷新：封面区可见性 + 标题上下限（快手 12 / 头条 5~30）。
    /// 标题计数与红字提示都依赖勾选平台，所以每次勾选/取消都要重播通知。
    /// </summary>
    private void UpdatePlatformRequirements()
    {
        HasCoverPlatform = PlatformTargets.Any(t => t.IsSelected && t.NeedsCover);
        OnPropertyChanged(nameof(EffectiveTitleMaxLength));
        OnPropertyChanged(nameof(EffectiveTitleMinLength));
        OnPropertyChanged(nameof(TitleCountText));
        OnPropertyChanged(nameof(TitleHintText));
        OnPropertyChanged(nameof(HasTitleHint));
    }

    // ═══ 选择封面图（View 注入 StorageProvider 回调） ═══

    [RelayCommand]
    private async Task BrowseCoverImage()
    {
        if (BrowseCoverImageAsync == null) return;
        var path = await BrowseCoverImageAsync();
        if (!string.IsNullOrWhiteSpace(path))
            CoverImagePath = path;
    }

    // ═══ 保存（新建 / 编辑） ═══

    /// <summary>编辑器保存 —— 校验 → 服务端建档/更新 → 回落列表 → 后台并行平台同步。</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsSaving || IsSyncing) return;

        // ── 校验（2026-10-22：标题最多 20 字、不能含符号；2026-11-05：按勾选平台收紧 ——
        //    快手 12 字、头条 5~30 字。与表单实时提示同一口径） ──
        var selectedPlatformKeys = SelectedPlatformKeys;
        if (CollectionTitleRules.Validate(CollectionTitle, selectedPlatformKeys) is { } titleError)
        {
            FormError = titleError;
            return;
        }
        if (HasCoverPlatform && string.IsNullOrWhiteSpace(CoverImagePath))
        {
            var coverPlatforms = PlatformTargets
                .Where(t => t.IsSelected && t.NeedsCover)
                .Select(t => t.DisplayName)
                .ToList();
            FormError = $"{string.Join("、", coverPlatforms)}合集需要选择封面图";
            return;
        }
        // 文章合集：类型规则兜底（UI 已禁用其他平台，这里防状态残留/脚本调用）
        if (CollectionContentTypeRules.ValidateTargets(CurrentContentType, selectedPlatformKeys)
            is { } typeError)
        {
            FormError = typeError;
            return;
        }
        if (IsArticleCollection
            && !PlatformTargets.Any(t =>
                t.PlatformKey == PlatformNames.Toutiao && t.IsSelected))
        {
            FormError = "文章合集必须选择头条账号";
            return;
        }

        // 勾选了尚未绑定账号的平台 → 拦截（否则服务端只建空壳记录且永不可同步）
        var needLogin = PlatformTargets.Where(t => t.IsSelected && !t.HasSessions).ToList();
        if (needLogin.Count > 0)
        {
            FormError = $"「{string.Join("、", needLogin.Select(t => t.DisplayName))}」尚未绑定账号 —— 请先在「平台账号」页登录，或取消勾选该平台";
            return;
        }
        var noAccount = PlatformTargets.Where(t => t.NeedsAccount).ToList();
        if (noAccount.Count > 0)
        {
            FormError = $"请为以下平台至少选择一个账号：{string.Join("、", noAccount.Select(t => t.DisplayName))}";
            return;
        }
        FormError = "";

        var title = CollectionTitleRules.Normalize(CollectionTitle);
        // 文章合集平台侧没有简介字段 —— 本地也不存（服务端同样会清空）
        var desc = IsArticleCollection || string.IsNullOrWhiteSpace(CollectionDescription)
            ? null
            : CollectionDescription.Trim();
        var cover = string.IsNullOrWhiteSpace(CoverImagePath) ? null : CoverImagePath.Trim();

        IsSaving = true;
        try
        {
            if (IsEditing && _editingId.HasValue)
            {
                await SaveEditingAsync(_editingId.Value, title, desc, cover);
            }
            else
            {
                await SaveCreatingAsync(title, desc, cover);
            }
        }
        catch (ApiException ex)
        {
            FormError = $"保存失败：{ex.Message}"; // 停留在表单，就地红条提示
        }
        catch (Exception ex)
        {
            FormError = $"保存失败：{ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>新建：服务端建档（每勾选平台 × 每勾选账号一条记录）→ 回落列表 → 后台逐账号同步。</summary>
    private async Task SaveCreatingAsync(string title, string? desc, string? cover)
    {
        var targets = PlatformTargets
            .Where(t => t.IsSelected
                && CollectionContentTypeRules.CanSelectPlatform(CurrentContentType, t.PlatformKey))
            .SelectMany(t => t.SelectedSessions.Select(s =>
                new CreateTargetRequest(t.PlatformKey, s.SessionId, null)))
            .ToList();

        var detail = await ApiClient.PostAsync<CollectionDetail>("/api/collections/create",
            new CreateCollectionRequest(title, desc, cover,
                targets.Count > 0 ? targets : null, CurrentContentType));

        if (detail == null)
        {
            FormError = "创建合集失败：服务端无响应，请稍后重试";
            return;
        }

        IsStatusError = false;
        StatusMessage = $"合集「{title}」已创建，正在同步到平台…";

        // 回落列表（同步期间列表行显示 Pending 状态 + 顶部进度 chips）
        ShowEditor = false;
        await LoadAsync();

        // 后台并行 SDK 调用（fire-and-forget，回调走 Dispatcher 更新进度面板）
        var syncTargets = detail.Platforms
            .Where(p => p.PlatformSessionId.HasValue)
            .Select(p =>
            {
                var pt = PlatformTargets.FirstOrDefault(t => t.PlatformKey == p.PlatformKey);
                var accountLabel = pt?.Sessions
                    .FirstOrDefault(s => s.SessionId == p.PlatformSessionId)?.Label ?? "";
                return new SyncTarget(
                    p.Id, p.PlatformKey, p.PlatformDisplayName,
                    accountLabel, p.PlatformSessionId!.Value,
                    CollectionContentTypeRules.IsArticle(detail.ContentType));
            })
            .ToList();

        if (syncTargets.Count > 0)
            _ = SyncToPlatformsAsync(detail.Id, syncTargets, cover, title, desc);
    }

    /// <summary>
    /// 编辑保存：① 更新 DB 元数据 ② 目标分类（Update / Create / 本次新勾选 → 先
    /// POST /collections/sync 建档）③ 回落列表 ④ 后台并行同步。老语义同款。
    /// </summary>
    private async Task SaveEditingAsync(Guid id, string title, string? desc, string? cover)
    {
        await ApiClient.PostAsync<object>("/api/collections/update",
            new UpdateCollectionRequest(id, title, desc, cover));

        // 分类（多账号语义：按 平台 × 会话 匹配记录 —— 同平台每个勾选账号独立成条）
        var updateTargets = new List<UpdateTarget>();
        var createTargets = new List<SyncTarget>();
        var newPlatforms = new List<(PlatformTargetItem pt, SessionOption session)>();

        foreach (var pt in PlatformTargets.Where(t => t.IsSelected
                     && CollectionContentTypeRules.CanSelectPlatform(
                         CurrentContentType, t.PlatformKey)))
        {
            foreach (var session in pt.SelectedSessions)
            {
                var existing = _editingPlatformRecords.FirstOrDefault(p =>
                    p.PlatformKey == pt.PlatformKey && p.PlatformSessionId == session.SessionId);

                if (existing != null && !string.IsNullOrWhiteSpace(existing.PlatformCollectionId))
                    updateTargets.Add(new UpdateTarget(
                        existing.Id, pt.PlatformKey, pt.DisplayName,
                        session.Label, existing.PlatformCollectionId, session.SessionId,
                        IsArticleCollection));
                else if (existing != null)
                    createTargets.Add(new SyncTarget(
                        existing.Id, pt.PlatformKey, pt.DisplayName,
                        session.Label, session.SessionId,
                        IsArticleCollection));
                else
                    newPlatforms.Add((pt, session)); // 编辑时新勾选的账号（同平台多账号互不影响）
            }
        }

        // 编辑新增的账号：先建档（服务端按 平台+会话 匹配，见 CollectionService.SyncAsync）
        if (newPlatforms.Count > 0)
        {
            try
            {
                var syncResult = await ApiClient.PostAsync<CollectionSyncResult>(
                    "/api/collections/sync",
                    new SyncCollectionRequest(id,
                        newPlatforms.Select(np => new SyncTargetRequest(
                            np.pt.PlatformKey, np.session.SessionId,
                            title, desc, cover, null)).ToList()));
                if (syncResult != null)
                {
                    foreach (var np in newPlatforms)
                    {
                        var created = syncResult.Results
                            .FirstOrDefault(r => r.PlatformKey == np.pt.PlatformKey
                                && r.PlatformSessionId == np.session.SessionId);
                        if (created != null)
                            createTargets.Add(new SyncTarget(
                                created.Id, np.pt.PlatformKey, np.pt.DisplayName,
                                np.session.Label, np.session.SessionId,
                                IsArticleCollection));
                    }
                }
            }
            catch (Exception ex)
            {
                IsStatusError = true;
                StatusMessage = $"新增账号建档失败：{ex.Message}";
            }
        }

        IsStatusError = false;
        StatusMessage = $"合集已更新{(updateTargets.Count + createTargets.Count > 0 ? "，正在同步平台变更…" : "")}";

        ShowEditor = false;
        await LoadAsync();

        if (updateTargets.Count > 0 || createTargets.Count > 0)
            _ = SyncEditToPlatformsAsync(id, updateTargets, createTargets, cover, title, desc);
    }

    // ═══ 后台同步（新建后创建 / 编辑后 更新+创建 双轨） ═══

    /// <summary>并行调各平台 SDK 创建合集，逐平台回报服务端；进度上进度面板 + 完成刷新列表。</summary>
    private async Task SyncToPlatformsAsync(
        Guid collectionId,
        List<SyncTarget> targets,
        string? coverImagePath,
        string title,
        string? description)
    {
        if (IsSyncing) return;
        IsSyncing = true;
        SyncProgressList.Clear();
        try
        {
            StatusMessage = "正在同步到平台…";
            IsStatusError = false;

            var results = await _syncService.SyncToPlatformsAsync(
                targets, coverImagePath, title, description,
                onProgressChanged: progress => Dispatcher.UIThread.Post(() => MergeSyncProgress(progress)));

            ApplySyncSummary(results, results.Count(r => r.Step == PlatformSyncStep.Created));
            // quiet：刷新列表不清状态条 —— 否则「N 个平台失败｜失败：小红书…：原因」刚写完就被抹掉
            await LoadAsync(quiet: true);
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = $"同步异常：{ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>编辑后台同步：更新队列与创建队列并行跑，进度合并到同一面板。</summary>
    private async Task SyncEditToPlatformsAsync(
        Guid collectionId,
        List<UpdateTarget> updateTargets,
        List<SyncTarget> createTargets,
        string? coverImagePath,
        string title,
        string? description)
    {
        if (IsSyncing) return;
        IsSyncing = true;
        SyncProgressList.Clear();
        try
        {
            StatusMessage = "正在同步平台变更…";
            IsStatusError = false;

            var tasks = new List<Task>();
            if (updateTargets.Count > 0)
            {
                tasks.Add(_syncService.SyncUpdateToPlatformsAsync(
                    updateTargets, coverImagePath, title, description,
                    onProgressChanged: progress =>
                        Dispatcher.UIThread.Post(() => MergeSyncProgress(progress))));
            }
            if (createTargets.Count > 0)
            {
                tasks.Add(_syncService.SyncToPlatformsAsync(
                    createTargets, coverImagePath, title, description,
                    onProgressChanged: progress =>
                        Dispatcher.UIThread.Post(() => MergeSyncProgress(progress))));
            }
            await Task.WhenAll(tasks);

            var synced = SyncProgressList.Count(r => r.Step == PlatformSyncStep.Created);
            ApplySyncSummary(SyncProgressList.ToList(), synced);
            await LoadAsync(quiet: true); // quiet：保留同步汇总（含失败原因）
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = $"同步异常：{ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>合并/新增一条进度（多账号语义：去重键 = 平台 + 会话 —— 同平台多个账号各占一行；
    /// AccountName 建项时已定不可改，合并仅推进状态）。
    /// 本方法经 Dispatcher 在 UI 线程执行 —— 顺带补发 chip 通知（后台线程只改字段不发通知，
    /// 否则绑定更新跨线程；同实例也照样发，否则「同一条进度推进到失败」永远不刷新）。</summary>
    private void MergeSyncProgress(PlatformSyncProgress progress)
    {
        var existing = SyncProgressList.FirstOrDefault(p =>
            p.PlatformKey == progress.PlatformKey && p.SessionId == progress.SessionId);
        if (existing == null)
        {
            SyncProgressList.Add(progress);
            return;
        }

        existing.Step = progress.Step;
        existing.PlatformCollectionId = progress.PlatformCollectionId;
        existing.ErrorMsg = progress.ErrorMsg;
        existing.NotifyProgressChanged();
    }

    /// <summary>汇总同步结果 → 状态条（成功绿 / 有失败红并附平台与原因）。</summary>
    private void ApplySyncSummary(List<PlatformSyncProgress> results, int synced)
    {
        var failed = results.Count(r => r.Step == PlatformSyncStep.Failed);
        var skipped = results.Count(r => r.Step == PlatformSyncStep.Skipped);

        var parts = new List<string> { $"{synced} 个平台已同步" };
        if (failed > 0) parts.Add($"{failed} 个平台失败");
        if (skipped > 0) parts.Add($"{skipped} 个平台已跳过");
        var message = $"同步完成：{string.Join("，", parts)}";
        if (failed > 0)
        {
            var failedList = results
                .Where(r => r.Step == PlatformSyncStep.Failed)
                .Select(r => $"{r.Label}：{r.ErrorMsg}");
            message += $"｜失败：{string.Join("；", failedList)}";
            IsStatusError = true;
        }
        else
        {
            IsStatusError = false;
        }
        StatusMessage = message;
    }

    // ═══ 重试失败平台（卡片「重试」） ═══

    [RelayCommand]
    private async Task RetryFailedSync(CollectionCard? card)
    {
        if (card == null || IsSyncing) return;
        IsSyncing = true;
        SyncProgressList.Clear();
        try
        {
            StatusMessage = "正在重试未同步平台…";
            IsStatusError = false;

            var detail = await ApiClient.PostAsync<CollectionDetail>("/api/collections/detail",
                new GetCollectionDetailRequest(card.Id));
            if (detail == null)
            {
                IsStatusError = true;
                StatusMessage = "重试失败：无法获取合集详情";
                return;
            }

            // Pending(0)/Failed(2) 且会话仍绑定的记录 → 重新 SDK Create
            //（孤儿记录 = 会话已解绑/被清引用，无凭据必然失败 → 不参与重试）
            var isArticle = CollectionContentTypeRules.IsArticle(detail.ContentType);
            var retryTargets = detail.Platforms
                .Where(p => p.Status is 0 or 2
                    && p.PlatformSessionId.HasValue
                    && _accountLabels.ContainsKey(p.PlatformSessionId.Value))
                .Select(p =>
                {
                    var meta = _platformMeta.GetValueOrDefault(p.PlatformKey, PlatformLookup.Fallback);
                    return new SyncTarget(
                        p.Id, p.PlatformKey,
                        meta.DisplayName.Length > 0 ? meta.DisplayName : PlatformNames.GetDisplayName(p.PlatformKey),
                        SessionLabelFor(p.PlatformSessionId), p.PlatformSessionId!.Value,
                        isArticle);
                })
                .ToList();

            if (retryTargets.Count == 0)
            {
                IsStatusError = false;
                StatusMessage = "没有需要重试的平台";
                return;
            }

            // 重试必须带上封面 —— 抖音/头条合集创建都要（2026-11-05 之前这里恒传 null，
            // 头条重试直接报「需要选择封面图」）。封面是创建时存在合集上的**本机路径**；
            // 文件被移动/删除或合集是在别的电脑建的，就拿不到了，只能引导重新选图。
            var coverPath = string.IsNullOrWhiteSpace(detail.CoverImagePath)
                || !File.Exists(detail.CoverImagePath)
                    ? null
                    : detail.CoverImagePath;
            if (coverPath is null && retryTargets.Any(t => t.PlatformKey is
                    PlatformNames.Douyin or PlatformNames.Toutiao))
            {
                IsStatusError = true;
                StatusMessage = "缺少封面图（本地文件已移动或删除，或该合集是在其他电脑创建的），"
                    + "请点「编辑」重新选择封面图后再重试";
                return;
            }

            StatusMessage = $"正在重试 {retryTargets.Count} 个平台…";
            var results = await _syncService.SyncToPlatformsAsync(
                retryTargets, coverPath, detail.Title, detail.Description,
                onProgressChanged: progress =>
                    Dispatcher.UIThread.Post(() => MergeSyncProgress(progress)));

            ApplySyncSummary(results, results.Count(r => r.Step == PlatformSyncStep.Created));
            await LoadAsync(quiet: true); // quiet：保留重试汇总（含失败原因）
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusMessage = $"重试异常：{ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    // ═══ 删除（两段确认：卡片确认条 → 本命令执行） ═══

    [RelayCommand]
    private void DeleteBegin(CollectionCard? card)
    {
        if (card == null) return;
        foreach (var c in Cards) c.IsConfirmingDelete = c == card;
    }

    [RelayCommand]
    private void DeleteCancel(CollectionCard? card)
    {
        if (card != null) card.IsConfirmingDelete = false;
    }

    /// <summary>单卡删除（卡片确认条 → 本命令）。走与批量同一条核。</summary>
    [RelayCommand]
    private async Task DeleteConfirmed(CollectionCard? card)
    {
        if (card == null || IsSyncing || _deleteInFlight) return;
        card.IsConfirmingDelete = false;
        await DeleteManyAsync([card]);
    }

    /// <summary>
    /// 删一批合集：拉详情 → 组装子项 → 交给后台任务。
    ///
    /// **语义：平台侧全部账号都删干净了，才删本地记录。** 判定在服务端
    /// （`AsyncTaskService.CleanLocalAsync`）—— 客户端可能根本不在这里，
    /// 而且「最后一条是谁报的」也不确定，只有服务端判得了。
    ///
    /// 这与视频页**故意不同**（视频页一行 = 一条记录，失败就保留那一行）；
    /// 合集一张卡 = N 条平台记录，本地记录一删，平台侧没删掉的那些就再也没人管、
    /// 也没了重试入口 —— 所以宁可整张卡留着。
    ///
    /// 反过来说，**「删不掉」的记录不能算失败**：会话已解绑、或状态还是 Pending/Failed
    /// 从没在平台上建过的，平台侧本来就没有东西可删，一律算已清 ——
    /// 否则 8 个账号里只要有 1 个解绑过，这条合集就永远删不掉。
    /// </summary>
    private async Task DeleteManyAsync(IReadOnlyList<CollectionCard> cards)
    {
        if (cards.Count == 0) return;

        _deleteInFlight = true;
        IsSyncing = true;
        SyncProgressList.Clear();
        Error = "";
        IsStatusError = false;

        // 乐观藏卡：点完就消失，免得用户盯着 8 个账号一个个变绿
        var ids = cards.Select(c => c.Id).ToHashSet();
        foreach (var card in cards) card.IsSelected = false;
        foreach (var id in ids) _pendingDeleteIds.Add(id);
        NotifyInFlight();
        NotifySelection();
        ApplyCardFilter();          // 立刻把藏起来的卡从列表拿掉
        EnsureSettleWatch();
        StatusMessage = cards.Count == 1
            ? $"正在删除合集「{cards[0].Title}」…"
            : $"正在删除 {cards.Count} 个合集…";

        try
        {
            var targets = new List<AsyncDeleteTargetRequest>();
            foreach (var card in cards)
            {
                var detail = await ApiClient.PostAsync<CollectionDetail>("/api/collections/detail",
                    new GetCollectionDetailRequest(card.Id));
                if (detail is null)
                {
                    // 详情拉不到就没法组装子项 —— 整批退回去，别提交半截任务
                    await UndoOptimisticRemovalAsync(ids);
                    IsStatusError = true;
                    StatusMessage = $"无法获取合集「{card.Title}」的详情，删除已取消";
                    return;
                }

                // 所有平台记录都入队 —— 包括「从没在平台上建过」的那些：
                // 它们在平台侧本来就没有东西可删，服务端按 UnboundCleaned 算已清，
                // 但**数量要对得上**，否则「8 个账号全删光」这句话在界面上对不齐
                foreach (var p in detail.Platforms)
                {
                    var meta = _platformMeta.GetValueOrDefault(p.PlatformKey, PlatformLookup.Fallback);
                    var name = meta.DisplayName.Length > 0
                        ? meta.DisplayName : PlatformNames.GetDisplayName(p.PlatformKey);
                    targets.Add(new AsyncDeleteTargetRequest(
                        p.Id,
                        AsyncTaskFormat.TargetCollectionPlatform,
                        card.Id,                                   // 同组键 = 合集 ID
                        p.PlatformKey,
                        p.PlatformSessionId,
                        SessionLabelFor(p.PlatformSessionId),      // 账号名快照
                        p.PlatformCollectionId,                    // 平台侧合集 ID 快照
                        detail.ContentType));                      // 头条文章/视频删除接口不同
                }
            }

            if (targets.Count == 0)
            {
                StatusMessage = "这些合集在平台上没有已同步的账号，无可删除";
                await UndoOptimisticRemovalAsync(ids);
                return;
            }

            var result = await AsyncTaskApi.SubmitAsync(new SubmitAsyncDeleteRequest(
                AsyncTaskFormat.KindCollectionDelete,
                cards.Count == 1
                    ? AsyncTaskFormat.SubmitTitle("删除合集", targets.Count, cards[0].Title)
                    : $"删除 {cards.Count} 个合集（{targets.Count} 个账号）",
                targets));

            if (result is null)
            {
                await UndoOptimisticRemovalAsync(ids);
                IsStatusError = true;
                StatusMessage = "提交删除任务失败，请重试";
                return;
            }

            StatusMessage = $"已提交删除任务（{cards.Count} 个合集 · {targets.Count} 个账号），后台正在处理";
        }
        catch (Exception ex)
        {
            await UndoOptimisticRemovalAsync(ids);
            IsStatusError = true;
            StatusMessage = "提交删除任务失败：" + ex.Message;
        }
        finally
        {
            _deleteInFlight = false;
            IsSyncing = false;
        }
    }

    /// <summary>提交失败时的回退：把藏起来的卡放回来（不重载的话它们就凭空消失了）</summary>
    private async Task UndoOptimisticRemovalAsync(HashSet<Guid> ids)
    {
        foreach (var id in ids) _pendingDeleteIds.Remove(id);
        NotifyInFlight();
        await LoadAsync(quiet: true);
    }

    /// <summary>把正在后台删的卡从列表里摘掉（服务端那边还没删完，重查会「复活」）</summary>
    private void ApplyCardFilter()
    {
        if (_pendingDeleteIds.Count == 0) return;
        foreach (var card in Cards.Where(c => _pendingDeleteIds.Contains(c.Id)).ToList())
            Cards.Remove(card);
        UpdateSummary();
        NotifyCollectionChangedPublic();
    }

    private void NotifyCollectionChangedPublic()
    {
        OnPropertyChanged(nameof(HasCollections));
        NotifySelection();
    }

    // ═══ 进度面板关闭 ═══

    [RelayCommand]
    private void ClearSyncProgress() => SyncProgressList.Clear();
}
