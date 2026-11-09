using System.ComponentModel;
using CreatFlow.NewApp.Models;
using CreatFlow.NewApp.Services;
using CreatFlow.Shared;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 视频管理页（P4）—— 已发布作品档案。
///
/// 一句话：**发出去之后**的那一半。上传队列回答「发出去了没」，这一页回答
/// 「我发过什么、发到哪个号、把发错的那条撤下来」。
///
/// 三个设计要点：
///  ① **每平台一行 + 按天分组**：服务端分页单位就是「平台记录」，按视频聚合会让
///     同一条视频的 4 条记录被切在相邻两页，卡片上显示成「只发了 2 个平台」。
///  ② **筛选与搜索在服务端**：客户端过滤会让「共 N 条」说谎（只统计了当前页）。
///     facet 计数跟关键词、不跟平台筛选（否则点一下抖音，其余芯片的计数全变）。
///  ③ **删除是全页唯一的写操作**，顺序不可反：先删平台、后删记录 ——
///     先删记录 = 平台作品变孤儿，且界面上再也没有入口能重试。
///
/// 视觉/交互基线见 docs/creatflow/newapp-video-manage-page-design.md。
/// </summary>
public sealed partial class VideoManageViewModel : ViewModelBase, INavigationAware
{
    private readonly AccountDockViewModel _dock;

    /// <summary>`sessionId → 账号名`（备注 → 昵称 → 未命名；见 <see cref="AccountLabels"/>）</summary>
    private readonly Dictionary<Guid, string> _accountLabels = [];
    /// <summary>`platformKey → 平台配置`（中文名 + 品牌色）</summary>
    private readonly Dictionary<string, PlatformConfigItem> _platformMeta = [];

    /// <summary>竞态护栏：慢的那次响应回来时，用户可能已经翻页/换筛选了</summary>
    private int _loadSeq;
    private bool _metaLoaded;
    private bool _deleteInFlight;
    private CancellationTokenSource? _searchDebounce;

    /// <summary>
    /// 已乐观摘掉、但平台侧还在删的行（= 服务端那边还没进终态的子项）。
    ///
    /// 为什么必须有：批量是「先摘行、后提交任务」的 —— 用户看到行消失了，可后台还没删完。
    /// 此刻只要重查（翻页、换筛选、切走再切回来）那条记录就会**复活**，
    /// 用户会以为删除没生效。靠这个集合在 <see cref="BuildRows"/> 里过滤掉。
    ///
    /// 集合的权威来源是服务端（<c>/api/async-tasks/in-flight</c>），不是本地记录 ——
    /// 这样 App 重启后照样藏得住，而且子项一旦失败进终态就自动从集合里掉出去，
    /// 失败的行也就自动放回列表了（不需要客户端额外记「哪些失败了」）。
    /// </summary>
    private readonly HashSet<Guid> _pendingDeleteIds = [];

    /// <summary>有在飞的行时定时回查服务端，任务跑完了当场刷新（失败的行放回来）</summary>
    private Timer? _settleTimer;

    /// <summary>回查间隔：消费循环 5 秒一轮，比它稍慢即可</summary>
    private static readonly TimeSpan SettleInterval = TimeSpan.FromSeconds(4);

    public VideoManageViewModel(AccountDockViewModel dock)
    {
        _dock = dock;
        foreach (var size in PageSizeOptions)
            PageSizeChips.Add(new PageSizeChip { Size = size, IsOn = size == PageSize });
    }

    // ═══════════════════════════════════════════════════════════════
    //  列表 / 筛选 / 分页
    // ═══════════════════════════════════════════════════════════════

    /// <summary>天头行 + 记录行混在一条流里（顺序即渲染顺序）</summary>
    public ObservableCollection<VideoArchiveRow> Rows { get; } = [];

    /// <summary>平台筛选芯片（含「全部」，单选）</summary>
    public ObservableCollection<PlatformFilterChip> PlatformFilters { get; } = [];

    /// <summary>每页条数（服务端 clamp 到 1..100）</summary>
    public static IReadOnlyList<int> PageSizeOptions { get; } = [20, 50, 100];

    /// <summary>底栏「每页」三格</summary>
    public ObservableCollection<PageSizeChip> PageSizeChips { get; } = [];

    [ObservableProperty] private string _keyword = "";
    /// <summary>选中的平台 key（空串 = 全部）</summary>
    [ObservableProperty] private string _selectedPlatform = "";
    [ObservableProperty] private int _pageSize = 20;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _total;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _error = "";
    /// <summary>一次性操作结果（「已删除」这类），不是错误</summary>
    [ObservableProperty] private string _statusText = "";

    public bool HasError => Error.Length > 0;
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>
    /// 「一条都没有」—— 只看服务端给的 Total，**不看 Rows**。
    ///
    /// 批量删除是乐观摘行的：一整页都在飞时 Rows 会被 `_pendingDeleteIds` 清空，
    /// 但服务端明明还有 108 条 —— 这时若按 Rows 判空，页面就会出现
    /// 「共 108 条已发布记录」+「还没有已发布的作品」的自相矛盾（2026-11-04 用户截图）。
    /// </summary>
    public bool IsEmpty => !IsLoading && !HasError && Total == 0;

    /// <summary>本页没有可显示的行，但服务端还有记录 —— 全都被「正在删」藏起来了</summary>
    public bool IsPageAllPending =>
        !IsLoading && !HasError && Total > 0 && !HasRows && _pendingDeleteIds.Count > 0;

    /// <summary>本页有没有记录行（天头不算）—— 工具条与空态都按它显示</summary>
    public bool HasRows => Rows.Any(r => r.IsRecord);

    /// <summary>筛选生效时把平台名写进汇总条 —— 否则「共 27 条」和「全部 108」看着像对不上</summary>
    public string SummaryText => VideoArchiveFormat.SummaryText(Total, SelectedPlatformName);

    private string? SelectedPlatformName =>
        SelectedPlatform.Length == 0
            ? null
            : PlatformFilters.FirstOrDefault(
                  c => string.Equals(c.Key, SelectedPlatform, StringComparison.OrdinalIgnoreCase))?.Name
              ?? SelectedPlatform;
    public string PageText => VideoArchiveFormat.PageText(Page, Total, PageSize);
    public bool HasPrev => !IsDeleting && VideoArchiveFormat.HasPrev(Page);
    public bool HasNext => !IsDeleting && VideoArchiveFormat.HasNext(Page, Total, PageSize);
    /// <summary>单条删除进行中：翻页/换筛选一律禁用（避免删到一半列表被换掉）</summary>
    [ObservableProperty] private bool _isDeleting;

    // ═══ 多选 / 批量删除 ═══

    /// <summary>正在提交任务（提交本身很快，只是别让用户连点两次）。**不拦翻页**</summary>
    [ObservableProperty] private bool _isBatchRunning;

    /// <summary>
    /// 提交期间显示一行进度，**提交一结束就收起**：后台执行进度归「异步任务」页。
    /// 这一页不再永久挂着「正在后台删除 N 项」——它会从提交一路挂到所有子项跑完
    ///（删除按账号有 60 秒冷却），看起来像永远不结束（2026-10-20 用户反馈）。
    /// </summary>
    public bool ShowBackgroundWork => IsBatchRunning;
    public string BackgroundWorkText => "正在提交删除任务…";

    partial void OnIsBatchRunningChanged(bool value)
        => OnPropertyChanged(nameof(ShowBackgroundWork));

    /// <summary>写操作能不能开始（单条与批量互斥：同账号并发删是 403 的来源）</summary>
    private bool CanStartDelete => !IsDeleting && !IsBatchRunning;

    public int SelectedCount => Rows.Count(r => r.IsRecord && r.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public string SelectionText => HasSelection ? $"已选 {SelectedCount} 项" : "";
    /// <summary>本页记录行**全部**被选中（天头行不计）</summary>
    public bool AllOnPageSelected =>
        Rows.Any(r => r.IsRecord) && Rows.Where(r => r.IsRecord).All(r => r.IsSelected);
    public string SelectAllText => AllOnPageSelected ? "取消全选" : "全选本页";
    public string DeleteSelectedText => HasSelection ? $"删除选中 {SelectedCount} 项" : "删除选中";

    /// <summary>
    /// 选中态散在每一行上，VM 这边是**算出来的** —— 不手动播报就永远停在旧值
    /// （这个项目里到处是这个坑：多视图常驻可视树，属性不通知就不会刷新）。
    /// </summary>
    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AllOnPageSelected));
        OnPropertyChanged(nameof(SelectAllText));
        OnPropertyChanged(nameof(DeleteSelectedText));
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoArchiveRow.IsSelected)) NotifySelection();
    }


    /// <summary>「每页 20 / 50 / 100」三个小按钮各自的选中态</summary>
    partial void OnPageSizeChanged(int value)
    {
        foreach (var chip in PageSizeChips) chip.IsOn = chip.Size == value;
    }

    partial void OnKeywordChanged(string value)
    {
        // 防抖 300ms：每敲一个字都发一次请求，服务端要按关键词做两次 LIKE，
        // 而用户打字时中间态全是废请求。搜索一律回到第 1 页。
        _searchDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounce = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, cts.Token);
                if (cts.IsCancellationRequested) return;
                await Dispatcher.UIThread.InvokeAsync(() => _ = LoadAsync(1));
            }
            catch (TaskCanceledException) { }
        });
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(HasPrev));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsPageAllPending));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasStatus));
    }

    // ═══════════════════════════════════════════════════════════════
    //  生命周期
    // ═══════════════════════════════════════════════════════════════

    public void OnNavigatedTo()
    {
        // 每次进页都刷：队列页里刚发出去的记录要能马上在这里看到。
        // 账号名/平台配置也重拉一次（换了备注、加了账号都要跟着走）。
        _metaLoaded = false;
        _ = ReloadAsync();
    }

    /// <summary>刷新当前页（保持页码与筛选）</summary>
    private async Task ReloadAsync()
    {
        await EnsureMetaAsync();
        await SyncInFlightAsync();       // 先对齐「哪些还在删」，再决定本页显示什么
        await LoadAsync(Math.Max(1, Page));
    }

    /// <summary>
    /// 拉一次「还在飞的目标 ID」。
    ///
    /// 走服务端而不是本地集合，是为了**跟别的端和重启后的自己对齐**：
    /// 本地只知道「这一次点的」，服务端知道「这个人所有还没删完的」。
    /// </summary>
    private async Task SyncInFlightAsync()
    {
        try
        {
            var inFlight = await AsyncTaskApi.InFlightTargetIdsAsync();
            _pendingDeleteIds.Clear();
            foreach (var id in inFlight) _pendingDeleteIds.Add(id);
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            /* 未登录：静默，让主查询去报错 */
        }
        catch (Exception ex)
        {
            // 拉不到就保留旧集合：宁可多藏几条（下次刷新自会修正），
            // 也别因为一次网络抖动让删到一半的行全冒出来
            Log.Warning(ex, "[VideoArchive] 拉在飞目标失败（保留旧集合）");
        }
        EnsureSettleWatch();
    }

    /// <summary>
    /// 还有在飞的行就定时回查，跑完了当场刷新。
    ///
    /// 不回查的话，用户停在页面上时：行一直藏着（对），但**失败的行也一直不出现**（错）——
    /// 得等他自己切走再回来才知道有东西没删掉。
    /// </summary>
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
        // 提交还在路上：服务端此刻还没有这批 ID，现在同步会把刚摘的行放回来
        if (IsBatchRunning) return;

        var before = _pendingDeleteIds.Count;
        await SyncInFlightAsync();
        // 条数变了 = 有子项进终态了 → 重载本页：成功的行早已不在服务端，
        // 失败的行会被放回来（它已不在在飞集合里，BuildRows 不再过滤它）
        if (_pendingDeleteIds.Count != before) await LoadAsync(Math.Max(1, Page));
    }

    private async Task EnsureMetaAsync()
    {
        if (_metaLoaded) return;
        try
        {
            var platforms = await VideoArchiveApi.PlatformsAsync();
            foreach (var p in platforms ?? [])
                _platformMeta[p.PlatformKey] = p;

            var labels = await VideoArchiveApi.AccountLabelsAsync();
            _accountLabels.Clear();
            foreach (var (k, v) in labels) _accountLabels[k] = v;

            _metaLoaded = true;
        }
        catch (ApiException ex) when (ex.StatusCode == 401) { /* 未登录：静默，让主查询去报错 */ }
        catch (Exception ex) { Log.Warning(ex, "[VideoArchive] 拉平台配置/账号失败（用兜底名）"); }
    }

    private async Task LoadAsync(int page)
    {
        var seq = ++_loadSeq;
        IsLoading = true;
        Error = "";
        NotifyState();
        try
        {
            var kw = Keyword.Trim();
            var data = await VideoArchiveApi.PageAsync(
                page, PageSize,
                SelectedPlatform.Length == 0 ? null : SelectedPlatform,
                kw.Length == 0 ? null : kw);

            if (seq != _loadSeq) return;              // 陈旧响应：丢弃（用户已经翻页/换筛选了）
            if (data is null)
            {
                Error = "服务端没有返回数据";
                return;
            }

            // 删空当前页后可能停在「超出末页」的页码上（批量删除不做逐页回退）：
            // 服务端会老实返回一页空的 Total>0，页面上就成了白页 —— 退到新的末页
            var lastPage = VideoArchiveFormat.TotalPages(data.Total, PageSize);
            if (data.Page > lastPage)
            {
                await LoadAsync(Math.Max(1, lastPage));
                return;
            }

            Page = data.Page;
            Total = data.Total;
            BuildRows(data.Items);
            SyncChips(data.PlatformCounts);
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            // 未登录：静默（--smoke 在没有服务端/未登录时也会走到这一页，不能炸）
        }
        catch (Exception ex)
        {
            if (seq == _loadSeq) Error = "加载失败：" + ex.Message;
        }
        finally
        {
            if (seq == _loadSeq)
            {
                IsLoading = false;
                NotifyState();
            }
        }
    }

    /// <summary>
    /// 服务端那一页 → 天头 + 记录行（服务端已按 CreatedAt desc 排好，同一天必然相邻）。
    ///
    /// **先滤掉正在删的那些**：批量是乐观摘行的，服务端此刻还没删完，
    /// 不滤的话翻页/切走再回来，用户会看见刚删掉的行又回来了。
    /// 天头计数在过滤之后算 —— 否则会出现「今天 · 3 条」但只画了 1 行。
    ///
    /// 按 <see cref="VideoArchiveRow.RowKey"/> **就地更新已有行**（不 Clear 重加）：
    /// 后台删除每 4 秒会重查一次，换对象的话用户勾好的那几条每 4 秒被清一次
    /// （2026-09-22 实测）。翻页/换筛选时旧行自然落到目标序列之外，一样会被移除。
    /// </summary>
    private void BuildRows(IReadOnlyList<VideoArchiveItem> items)
    {
        var now = Clock.Now;
        var visible = items.Where(i => !_pendingDeleteIds.Contains(i.PlatformId));
        var byKey = Rows.ToDictionary(r => r.RowKey);
        var ordered = new List<VideoArchiveRow>();

        foreach (var group in visible.GroupBy(i => VideoArchiveFormat.DayLabel(i.CreatedAt, now)))
        {
            if (byKey.TryGetValue(group.Key, out var header) && header.IsDayHeader)
                header.ApplyHeader(group.Count());      // 条数会变（刚删掉几条）
            else
                header = VideoArchiveRow.Header(group.Key, group.Count());
            ordered.Add(header);

            foreach (var item in group)
            {
                var (name, brand) = PlatformMetaOf(item.PlatformKey, item.PlatformDisplayName);
                var label = AccountLabels.LabelOf(_accountLabels, item.PlatformSessionId);
                var key = item.PlatformId.ToString("N");

                VideoArchiveRow row;
                if (byKey.TryGetValue(key, out var existing) && existing.IsRecord)
                {
                    // 会话解析不出账号名 = 会话已不在服务端（确认条文案要跟着变）
                    existing.Apply(item, label, name, brand, sessionKnown: label is not null);
                    row = existing;
                }
                else
                {
                    row = VideoArchiveRow.Record(item, label, name, brand, sessionKnown: label is not null);
                    row.PropertyChanged += OnRowPropertyChanged;
                }
                ordered.Add(row);
            }
        }

        CollectionSync.Sync(Rows, ordered);
        NotifySelection();
    }

    private (string Name, string BrandHex) PlatformMetaOf(string platformKey, string serverName)
    {
        if (_platformMeta.TryGetValue(platformKey, out var cfg))
            return (string.IsNullOrWhiteSpace(cfg.DisplayName) ? serverName : cfg.DisplayName,
                    string.IsNullOrWhiteSpace(cfg.BrandColorHex) ? "#6C5CE7" : cfg.BrandColorHex);
        // 平台被停用但历史记录还在：用服务端给的中文名 + 中性色兜底
        return (string.IsNullOrWhiteSpace(serverName) ? PlatformNames.GetDisplayName(platformKey) : serverName,
                "#6C5CE7");
    }

    private void SyncChips(IReadOnlyList<PlatformCountItem> counts)
    {
        var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in counts) byKey[c.PlatformKey] = c.Count;

        PlatformFilters.Clear();
        PlatformFilters.Add(new PlatformFilterChip
        {
            Key = "",
            Name = "全部",
            BrandHex = "#6C5CE7",
            // 各平台 facet 之和 —— 不能用 Total（它是当前平台筛选下的条数，
            // 选中「视频号」时会显示成「全部 13 / 快手 14」）
            Count = VideoArchiveFormat.AllChipCount(counts, Total),
            IsOn = SelectedPlatform.Length == 0,
        });

        // 已知平台（配置里的四家，顺序稳定）在前，本页出现过的未知 key 补在后面
        var keys = _platformMeta.Keys
            .Concat(byKey.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var key in keys)
        {
            var (name, brand) = PlatformMetaOf(key, "");
            PlatformFilters.Add(new PlatformFilterChip
            {
                Key = key,
                Name = name,
                BrandHex = brand,
                Count = byKey.GetValueOrDefault(key),
                IsOn = string.Equals(SelectedPlatform, key, StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  命令
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task RefreshAsync()
    {
        StatusText = "";
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task SelectPlatformAsync(PlatformFilterChip? chip)
    {
        if (chip is null || IsDeleting) return;
        SelectedPlatform = chip.Key;
        foreach (var c in PlatformFilters) c.IsOn = ReferenceEquals(c, chip);
        await LoadAsync(1);
    }

    /// <summary>
    /// ⚠️ 参数类型必须是 int：XAML 的 CommandParameter 绑的是 <see cref="PageSizeChip.Size"/>（int），
    /// 而 CommunityToolkit 的 <c>RelayCommand&lt;T&gt;</c> **不做类型转换** —— 写成 string?
    /// 点「50 / 100」会抛 ArgumentException，每页条数永远停在 20
    /// （2026-11-04 用户截图红框圈的正是这两个按钮）。
    /// </summary>
    [RelayCommand]
    private async Task SetPageSizeAsync(int size)
    {
        if (IsDeleting || size == PageSize || !PageSizeOptions.Contains(size)) return;
        PageSize = size;
        await LoadAsync(1);      // 每页条数变了，页码必须回到第 1 页
    }

    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (IsDeleting || !VideoArchiveFormat.HasPrev(Page)) return;
        await LoadAsync(Page - 1);
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (IsDeleting || !VideoArchiveFormat.HasNext(Page, Total, PageSize)) return;
        await LoadAsync(Page + 1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  删除（本页唯一的写操作）
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private void DeleteBegin(VideoArchiveRow? row)
    {
        if (row?.Item is null || !CanStartDelete) return;
        foreach (var r in Rows)
        {
            r.IsConfirmingDelete = ReferenceEquals(r, row);   // 单开一张卡
            r.IsConfirmingWholeVideo = false;                 // 两条确认条互斥
        }
    }

    [RelayCommand]
    private void DeleteCancel(VideoArchiveRow? row)
    {
        if (row is not null)
        {
            row.IsConfirmingDelete = false;
            row.IsConfirmingWholeVideo = false;
        }
    }

    // ═══ 多选 ═══

    [RelayCommand]
    private void SelectAllOnPage()
    {
        var on = !AllOnPageSelected;
        foreach (var r in Rows.Where(r => r.IsRecord)) r.IsSelected = on;
        NotifySelection();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in Rows.Where(r => r.IsRecord)) r.IsSelected = false;
        NotifySelection();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (!CanStartDelete) return;
        var items = Rows.Where(r => r.IsRecord && r.IsSelected && r.Item is not null)
                        .Select(r => r.Item!).ToList();
        if (items.Count == 0) return;
        await SubmitBatchAsync(items);
    }

    // ═══ 删除这条视频的全部副本（跨页）═══

    /// <summary>
    /// 「全部副本」第一步：查这条视频在平台上到底有几个副本。
    ///
    /// 不能拿本页的行数充数 —— 分页单位是平台记录，同一个视频的副本会被切在相邻两页，
    /// 只按本页说「共 3 条」就是在骗人（老版 V2 那个「静默少删」的坑正是这么来的）。
    /// </summary>
    [RelayCommand]
    private async Task DeleteWholeVideoAsync(VideoArchiveRow? row)
    {
        if (row?.Item is not { } item || !CanStartDelete) return;
        foreach (var r in Rows)
        {
            r.IsConfirmingDelete = false;
            r.IsConfirmingWholeVideo = ReferenceEquals(r, row);
        }
        row.IsCountingWholeVideo = true;
        row.WholeVideoText = "正在统计副本…";
        try
        {
            var all = await VideoArchiveApi.ByTaskAsync(item.TaskId);
            row.WholeVideoCount = all.Count;
            row.WholeVideoText = VideoArchiveFormat.WholeVideoStripText(all.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VideoArchive] 统计副本失败 taskId={Id}", item.TaskId);
            row.IsConfirmingWholeVideo = false;
            Error = "统计副本失败：" + ex.Message;
        }
        finally
        {
            row.IsCountingWholeVideo = false;
        }
    }

    /// <summary>
    /// 「全部副本」第二步：确认删除。**重新查一次**而不是复用上一步的结果 ——
    /// 用户可能在确认条上停留了一会儿，期间别的端可能又发了新副本；
    /// 列表页的一行不该是删除范围的唯一真相。
    /// </summary>
    [RelayCommand]
    private async Task DeleteWholeVideoConfirmedAsync(VideoArchiveRow? row)
    {
        if (row?.Item is not { } item || !CanStartDelete) return;
        row.IsConfirmingWholeVideo = false;
        try
        {
            var all = await VideoArchiveApi.ByTaskAsync(item.TaskId);
            if (all.Count == 0)
            {
                StatusText = "这条视频在平台上已经没有副本了";
                NotifyState();
                return;
            }
            await SubmitBatchAsync(all, subject: item.CommonTitle ?? item.VideoFileName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VideoArchive] 拉取副本失败 taskId={Id}", item.TaskId);
            Error = "删除失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 确认删除。顺序**不可反**：先平台侧、后本地记录。
    ///
    /// 六种结果的处理见 <see cref="VideoArchiveFormat.RowRemoved"/> / <see cref="VideoArchiveFormat.IsError"/>：
    /// 只有「平台上确实没有了」才摘行 —— 登录过期/平台拒绝一律保留记录，让用户能重试。
    /// </summary>
    [RelayCommand]
    private async Task DeleteConfirmedAsync(VideoArchiveRow? row)
    {
        if (row?.Item is not { } item || _deleteInFlight || !CanStartDelete) return;
        _deleteInFlight = true;
        IsDeleting = true;
        row.IsDeleting = true;
        row.IsConfirmingDelete = false;
        Error = "";
        StatusText = "";
        NotifyState();
        try
        {
            var outcome = await DeleteOneItemAsync(item, CancellationToken.None);
            ReportOutcome(outcome, item);
            if (VideoArchiveFormat.RowRemoved(outcome))
            {
                RemoveRowLocally(row);
                // 本页删空了且不在第一页 → 回退一页（否则停在一张白页上）
                if (Rows.All(r => r.IsDayHeader) && Page > 1)
                    await LoadAsync(Page - 1);
            }
        }
        finally
        {
            row.IsDeleting = false;
            _deleteInFlight = false;
            IsDeleting = false;
            NotifySelection();
            NotifyState();
        }
    }

    /// <summary>
    /// 删一条平台记录（平台侧 → 本地记录）。
    ///
    /// **只返回结果，不碰任何 UI 状态** —— 单条删除与批量共用这一段，语义只有一份。
    /// 顺序不可反：平台侧删掉了才清本地记录；先清记录 = 平台作品变孤儿，界面上再没有入口重试。
    /// </summary>
    private async Task<DeleteOutcome> DeleteOneItemAsync(VideoArchiveItem item, CancellationToken ct)
    {
        try
        {
            var scope = VideoArchiveFormat.DeleteMode(
                item.PlatformKey, item.PlatformSessionId, item.PlatformVideoId);
            DeleteOutcome outcome;

            if (scope == DeleteScope.PlatformAndLocal)
            {
                var result = await VideoArchiveDeleteService.DeleteOnPlatformAsync(
                    item.PlatformKey, item.PlatformSessionId!.Value, item.PlatformVideoId!,
                    // 快手：ID 是早期 mediaId 时靠封面 Key 回查；其他平台忽略
                    VideoArchiveFormat.KuaishouCoverKey(item.ExtraDataJson),
                    item.Title ?? item.CommonTitle, ct);
                outcome = result switch
                {
                    PlatformDeleteResult.Success => DeleteOutcome.Deleted,
                    PlatformDeleteResult.NotFound => DeleteOutcome.AlreadyGone,
                    PlatformDeleteResult.SessionGone => DeleteOutcome.UnboundCleaned,
                    PlatformDeleteResult.Unauthorized => DeleteOutcome.NeedLogin,
                    _ => DeleteOutcome.PlatformFailed,
                };
            }
            else
            {
                // 账号已解绑（或没有作品 ID / 平台不接删除）：平台侧无从下手，只清本地记录。
                // 老版在这里**静默**只删记录，用户以为平台上的也没了 —— 文案里说清楚。
                // B站单独一种结果：不是「账号没了」，而是「平台要人机验证、我们不删」。
                outcome = scope == DeleteScope.LocalOnlyPlatformUnsupported
                    ? DeleteOutcome.LocalOnlyCleaned
                    : DeleteOutcome.UnboundCleaned;
            }

            // 平台侧没删掉 → 记录必须留着，否则平台上的作品再也没人管
            if (!VideoArchiveFormat.RowRemoved(outcome)) return outcome;

            // 平台侧已删（或无需删）→ 清本地记录
            try
            {
                await VideoArchiveApi.DeleteRecordAsync(item.PlatformId);
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                // 别的端/别处已经删过了：当成功（服务端删除不幂等，重复删就是 404）
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[VideoArchive] 记录删除失败 platformId={Id}", item.PlatformId);
                outcome = DeleteOutcome.RecordCleanupFailed;
            }
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;   // 停机路径，向上传（别当成「平台删除失败」保留一堆其实没试过的行）
        }
        catch (Exception ex)
        {
            // 保守归到 PlatformFailed：记录保留、可重试。SDK 内部基本已把异常收成返回值，
            // 走到这儿的多半是真意外，宁可让用户看见「失败」也别假装删掉了。
            Log.Warning(ex, "[VideoArchive] 删除异常 platformId={Id}", item.PlatformId);
            return DeleteOutcome.PlatformFailed;
        }
    }

    /// <summary>
    /// 批量删除。**乐观摘行**：先把行从列表摘掉（并记入 <see cref="_pendingDeleteIds"/>），
    /// 再按 (平台,会话) 分组后台跑。
    ///
    /// 分组见 <see cref="VideoArchiveFormat.GroupKey"/>：组内串行（同账号并发删会 403）、
    /// 组间并行、同平台最多 <see cref="MaxGroupsPerPlatform"/> 组同时跑。
    /// 组体包在 <see cref="Task.Run(Func{Task})"/> 里 —— 不包的话每个 await 都会回到 UI 线程，
    /// 32 条会一条条排队跑完，界面反而更卡。
    /// </summary>
    private async Task SubmitBatchAsync(IReadOnlyList<VideoArchiveItem> items, string? subject = null)
    {
        if (items.Count == 0) return;

        // 乐观摘行：立刻从列表消失，并记入在飞集合（翻页/切走回来都不会复活）
        var ids = items.Select(i => i.PlatformId).ToHashSet();
        foreach (var row in Rows.Where(r => r.Item is { } it && ids.Contains(it.PlatformId)).ToList())
        {
            row.IsSelected = false;
            row.IsConfirmingDelete = false;
            row.IsConfirmingWholeVideo = false;
            RemoveRowLocally(row);
        }
        foreach (var id in ids) _pendingDeleteIds.Add(id);
        EnsureSettleWatch();

        IsBatchRunning = true;
        Error = "";
        StatusText = "";
        NotifyState();

        try
        {
            var result = await AsyncTaskApi.SubmitAsync(new SubmitAsyncDeleteRequest(
                AsyncTaskFormat.KindVideoDelete,
                AsyncTaskFormat.SubmitTitle("删除", items.Count, subject),
                items.Select(i => new AsyncDeleteTargetRequest(
                    i.PlatformId,
                    AsyncTaskFormat.TargetPublishPlatform,
                    i.TaskId,                                    // 同组键 = 发布任务 = 一条视频
                    i.PlatformKey,
                    i.PlatformSessionId,
                    AccountLabels.LabelOf(_accountLabels, i.PlatformSessionId),   // 账号名快照
                    i.PlatformVideoId)).ToList()));              // 平台侧作品 ID 快照

            if (result is null)
            {
                // 提交失败：行已经摘掉了，必须放回来 —— 否则用户以为删了，其实什么都没发生
                await UndoOptimisticRemovalAsync(ids);
                Error = "提交删除任务失败，请重试";
                return;
            }

            StatusText = $"已提交删除任务（{items.Count} 项），进度见「异步任务」";
        }
        catch (Exception ex)
        {
            await UndoOptimisticRemovalAsync(ids);
            Error = "提交删除任务失败：" + ex.Message;
        }
        finally
        {
            IsBatchRunning = false;
            NotifySelection();
            NotifyState();
        }
    }

    /// <summary>提交失败时的回退：把乐观摘掉的行放回来（不重载的话它们就凭空消失了）</summary>
    private async Task UndoOptimisticRemovalAsync(HashSet<Guid> ids)
    {
        foreach (var id in ids) _pendingDeleteIds.Remove(id);
        await LoadAsync(Math.Max(1, Page));
    }

    private void ReportOutcome(DeleteOutcome outcome, VideoArchiveItem item)
    {
        var text = VideoArchiveFormat.DeleteOutcomeText(outcome);
        StatusText = text;
        if (VideoArchiveFormat.IsError(outcome))
        {
            Error = text;
            // 登录过期 → 顺手把「去登录」的入口给上（账号坞对应标签）
            if (outcome == DeleteOutcome.NeedLogin) OpenLoginDock(item);
        }
    }

    /// <summary>把账号坞切到这个账号的登录页（只开入口，不动页面文案 —— 批量要留着汇总）</summary>
    private void OpenLoginDock(VideoArchiveItem item)
    {
        if (item.PlatformSessionId is not { } sid) return;
        var (name, brand) = PlatformMetaOf(item.PlatformKey, item.PlatformDisplayName);
        _dock.OpenOrActivate(sid, item.PlatformKey, name, brand, "",
            AccountLabels.LabelOf(_accountLabels, sid) ?? "", forceLogin: true);
    }

    /// <summary>本地摘行 + 同步计数（服务端那边已经没有了；Counts 用本地减，避免整页重拉闪一下）</summary>
    private void RemoveRowLocally(VideoArchiveRow row)
    {
        Rows.Remove(row);
        if (Total > 0) Total--;
        var key = row.Item?.PlatformKey;
        if (key is not null)
        {
            var chip = PlatformFilters.FirstOrDefault(
                c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
            if (chip is { Count: > 0 }) chip.Count--;
        }
        // 「全部」是各平台之和，不是 Total —— 每摘一行也要跟着减一
        var all = PlatformFilters.FirstOrDefault(c => c.Key.Length == 0);
        if (all is { Count: > 0 }) all.Count--;
        NotifySelection();   // 摘掉的可能是被勾选的行，计数要跟着变
        NotifyState();
    }
}
