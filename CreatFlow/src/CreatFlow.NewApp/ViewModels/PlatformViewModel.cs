using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 平台账号页 ViewModel（§5.3 账号总览列表 · 两栏：左平台 → 右账号卡）。
/// 阶段 A：本地存储层（微信式会员隔离）+ 服务端驱动全管理操作；
/// 「添加账号 / 重新登录」为 B 阶段 WebView2 多档案登录入口 —— 现给出引导提示。
/// 端点：/api/config/platforms + platform-sessions list/rename/set-primary/delete/probe。
/// 数据刷新：进入页面 INavigationAware 拉取；操作后局部更新。
/// </summary>
public partial class PlatformViewModel : ViewModelBase, INavigationAware
{
    private readonly PlatformAccountStore _localStore;
    private readonly AccountDockViewModel _dock;
    private readonly UploadQueueViewModel _queue;
    private readonly AccountGroupsService _groupService;

    /// <summary>账号显示顺序（与发布页排序页共用同一份**会员级**存档，DI 单例注入，见 <see cref="AccountOrder"/>）</summary>
    private readonly AccountOrderStore _orderStore;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private PlatformGroup? _selectedGroup;

    /// <summary>平台分组（左栏数据源 —— 必须 ObservableCollection：
    /// Load 中 Clear/Add 才通知 UI，List 无 INotifyCollectionChanged → 左列永远空，踩坑）</summary>
    public ObservableCollection<PlatformGroup> Groups { get; } = [];

    /// <summary>是否选中了某个平台（右栏可见）</summary>
    public bool HasSelection => SelectedGroup != null;

    /// <summary>页头图标（矢量）</summary>
    public MaterialIconKind HeaderIcon => MaterialIconKind.AccountGroup;

    /// <summary>本地账号库目录（微信式会员隔离，页脚展示）</summary>
    public string LocalStoreDir => _localStore.MemberDir;

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnSelectedGroupChanged(PlatformGroup? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        RebuildTabItems();          // 切平台 → 标签条换该平台的账号集
        NotifyActiveVisibility();
    }

    public PlatformViewModel(
        PlatformAccountStore localStore, AccountDockViewModel dock, UploadQueueViewModel queue,
        AccountGroupsService groups, AccountOrderStore orderStore)
    {
        _localStore = localStore;
        _dock = dock;
        _queue = queue;
        _groupService = groups;
        _orderStore = orderStore;
        dock.Tabs.CollectionChanged += (_, _) => RebuildTabItems();
        dock.PropertyChanged += OnDockPropertyChanged;
        dock.SessionSynced += OnDockSessionSynced; // 坞内同步成功 → 账号卡静默刷新(单例坞随壳,VM 页缓存,不摘)
    }

    // ═══ 账号分组（2026-10-14 用户需求；本页是唯一的管理面）══════

    /// <summary>分组卡的行列表（增删改组后重建/局部更新）</summary>
    public ObservableCollection<AccountGroupRow> GroupRows { get; } = [];

    public bool HasGroupRows => GroupRows.Count > 0;

    /// <summary>分组操作的状态行（保存/删除成功或失败）</summary>
    [ObservableProperty] private string _groupStatusMessage = "";
    [ObservableProperty] private bool _isGroupStatusError;
    public bool HasGroupStatus => !string.IsNullOrEmpty(GroupStatusMessage);
    partial void OnGroupStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasGroupStatus));

    /// <summary>新建分组：取消其它行的编辑态（编辑是草稿、未保存即丢弃），追加一行空草稿</summary>
    [RelayCommand]
    private void AddGroup()
    {
        foreach (var r in GroupRows) r.CancelEdit();
        var row = AccountGroupRow.NewDraft();
        row.BeginEdit(Groups);
        GroupRows.Add(row);
        OnPropertyChanged(nameof(HasGroupRows));
        SetGroupStatus("填好名称、勾上账号，再点「保存」");
        IsGroupStatusError = false;
    }

    [RelayCommand]
    private void BeginEditGroup(AccountGroupRow? row)
    {
        if (row is null) return;
        foreach (var r in GroupRows)
            if (!ReferenceEquals(r, row)) r.CancelEdit();
        row.BeginEdit(Groups);
    }

    /// <summary>取消：新建行整行移除（没保存过）；已有行退回展示态</summary>
    [RelayCommand]
    private void CancelEditGroup(AccountGroupRow? row)
    {
        if (row is null) return;
        row.CancelEdit();
        if (row.IsNew)
        {
            GroupRows.Remove(row);
            OnPropertyChanged(nameof(HasGroupRows));
        }
    }

    [RelayCommand]
    private async Task SaveGroupAsync(AccountGroupRow? row)
    {
        if (row is null) return;
        if (!row.TryCommit(_groupService.Groups, out var group)) return; // 校验错误已落行内红字
        try
        {
            await _groupService.SaveAsync(group);
            row.Commit(group, Groups);
            SetGroupStatus(group.Sessions.Count == 0
                ? $"已保存分组「{group.Name}」（还没勾账号，组条上会先灰着）"
                : $"已保存分组「{group.Name}」（{group.Sessions.Count} 个账号）");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Platform] 保存账号分组失败");
            SetGroupStatus("保存失败，请重试", isError: true);
        }
    }

    /// <summary>删除两段式：先摆行内确认条（同一时刻只开一条），确认才真删。</summary>
    [RelayCommand]
    private void BeginDeleteGroup(AccountGroupRow? row)
    {
        if (row is null) return;
        foreach (var r in GroupRows) r.IsConfirmingDelete = ReferenceEquals(r, row);
    }

    [RelayCommand]
    private void CancelDeleteGroup(AccountGroupRow? row)
    {
        if (row is not null) row.IsConfirmingDelete = false;
    }

    /// <summary>确认删除：**只删分组定义** —— 不碰账号、不碰 account-selection.json、
    /// 不碰已入队任务的快照（快照早已展开成 (平台, 会话, 标签) 三元组）。</summary>
    [RelayCommand]
    private async Task ConfirmDeleteGroupAsync(AccountGroupRow? row)
    {
        if (row is null) return;
        row.IsConfirmingDelete = false;
        try
        {
            await _groupService.DeleteAsync(row.Id);
            GroupRows.Remove(row);
            OnPropertyChanged(nameof(HasGroupRows));
            SetGroupStatus($"已删除分组「{row.Name}」");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Platform] 删除账号分组失败");
            SetGroupStatus("删除失败，请重试", isError: true);
        }
    }

    private void SetGroupStatus(string message, bool isError = false)
    {
        GroupStatusMessage = message;
        IsGroupStatusError = isError;
    }

    /// <summary>加载分组 + （拉到权威账号列表时）把已解绑成员的遗骸剪掉 —— 本页是剪除的
    /// **唯一写者**：会话列表没拉到（401/失败）时绝不能剪，否则一次网络抖动就把所有
    /// 分组成员清成空。GroupRows 摘要永远按「组 ∩ 当前在册」现算，天然是活的。
    /// internal：冒烟特判直接调（离线时 LoadAsync 早退到不了这里）。</summary>
    internal async Task RefreshGroupsAsync(bool pruneAllowed)
    {
        try
        {
            await _groupService.EnsureLoadedAsync();
            if (pruneAllowed)
            {
                var alive = Groups.SelectMany(g => g.Sessions).Select(s => s.Id).ToList();
                await _groupService.PruneAsync(alive);
            }
            GroupRows.Clear();
            foreach (var g in _groupService.Groups)
                GroupRows.Add(AccountGroupRow.FromGroup(g, Groups));
            OnPropertyChanged(nameof(HasGroupRows));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Platform] 加载账号分组失败");
        }
    }

    /// <summary>账号坞（公开给平台页标签行/宿主/交互）</summary>
    public AccountDockViewModel Dock => _dock;

    /// <summary>当前平台（左栏选中）的账号标签（顶部标签行数据源）</summary>
    public ObservableCollection<AccountDockTabViewModel> TabItems { get; } = [];

    /// <summary>当前平台是否有账号标签（标签行可见）</summary>
    public bool HasTabItems => TabItems.Count > 0;

    /// <summary>活动账号属于当前选中平台（内容区显示 WebView 宿主）</summary>
    public bool ActiveInPlatform =>
        _dock.ActiveTab != null && _dock.ActiveTab.PlatformKey == SelectedGroup?.PlatformKey;

    private void OnDockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountDockViewModel.ActiveTab)
            or nameof(AccountDockViewModel.ShowTabHost))
        {
            NotifyActiveVisibility();
        }
    }

    private void RebuildTabItems()
    {
        var key = SelectedGroup?.PlatformKey;
        TabItems.Clear();
        foreach (var tab in _dock.Tabs.Where(t => t.PlatformKey == key))
            TabItems.Add(tab);
        OnPropertyChanged(nameof(HasTabItems));
        NotifyActiveVisibility();
    }

    private void NotifyActiveVisibility() => OnPropertyChanged(nameof(ActiveInPlatform));

    public void OnNavigatedTo()
    {
        if (!IsLoading) LoadCommand.Execute(null);
    }

    /// <summary>坞内某账号登录收尾同步成功 → 后台静默重拉列表(账号卡展示最新昵称/状态;选中平台与状态条文案保留)</summary>
    private void OnDockSessionSynced(Guid sessionId)
    {
        if (IsLoaded && !IsLoading) _ = LoadAsync(quiet: true);
    }

    // ═══ 拉取平台配置 + 会话列表，构建分组 ═══

    [RelayCommand]
    private Task Load() => LoadAsync();

    /// <summary>拉平台配置 + 会话列表构建分组。quiet=true(坞内同步触发)不清错误/状态文案,仅刷新数据。</summary>
    private async Task LoadAsync(bool quiet = false)
    {
        IsLoading = true;
        if (!quiet)
        {
            Error = "";
            StatusMessage = "";
        }
        try
        {
            var configs = await ApiClient.GetAsync<List<PlatformConfigItem>>("/api/config/platforms");
            if (configs == null || configs.Count == 0)
            {
                Error = "未能获取平台配置，请检查网络连接";
                return;
            }

            PlatformSessionListResponse? sessionsResp = null;
            try
            {
                sessionsResp = await ApiClient.PostAsync<PlatformSessionListResponse>(
                    "/api/platform-sessions/list");
            }
            catch (ApiException ex) when (ex.StatusCode == 401)
            {
                // 会话失效由壳统一处理，此处按无会话对待
            }

            // 账号顺序与发布页同源（默认序 + 用户自定义序）。本页是「平台分组 → 组内账号」
            // 两层结构，所以**两层次序都要跟**：组的先后 + 组内账号的先后。
            var order = await _orderStore.LoadAsync();
            var ordered = AccountOrder.Apply(
                AccountOrder.Default(sessionsResp?.Sessions ?? [], configs), order);

            // 组次序从排好的会话里推：先出现的平台在前。还没登录过任何账号的平台
            // 不在序列里，按配置序稳定地追加在后面（OrderBy 是稳定排序）。
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in ordered) rank.TryAdd(s.PlatformKey, rank.Count);

            Groups.Clear();
            foreach (var config in configs.OrderBy(c => rank.GetValueOrDefault(c.PlatformKey, int.MaxValue)))
            {
                var group = new PlatformGroup
                {
                    PlatformKey = config.PlatformKey,
                    DisplayName = config.DisplayName,
                    BrandColorHex = config.BrandColorHex,
                    LoginUrl = config.LoginUrl,
                };
                foreach (var item in ordered.Where(s => s.PlatformKey == config.PlatformKey))
                {
                    group.Sessions.Add(PlatformSession.FromApi(item, config));
                }
                group.RefreshStats();
                Groups.Add(group);
            }

            SelectedGroup = SelectedGroup is { } keep
                && Groups.FirstOrDefault(g => g.PlatformKey == keep.PlatformKey) is { } match
                ? match
                : Groups.FirstOrDefault();

            // 账号分组：进页顺带拉云端收敛多设备差异（云端权威；本地 dirty 时编排器改为先推；
            // 失败静默 —— RefreshGroupsAsync 读本地缓存照常，离线也有内容）。冒烟整体被闸。
            await AccountGroupCloudSync.PullAsync(_groupService);

            // 账号分组卡（本地存档，离线也有内容）；会话列表拉到了才允许剪除
            await RefreshGroupsAsync(pruneAllowed: sessionsResp != null);

            IsLoaded = true;
        }
        catch (Exception ex)
        {
            Error = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ═══ 添加账号 / 重新登录 / 打开（标签坞：常驻 WebView，扫码登录在坞内完成） ═══

    [RelayCommand]
    private async Task AddAccount(PlatformGroup? group)
    {
        if (group == null) return;
        IsLoading = true;
        IsStatusError = false;
        try
        {
            var err = await _dock.AddNewAccountAsync(
                group.PlatformKey, group.DisplayName, group.BrandColorHex, group.LoginUrl);
            if (err != null)
            {
                IsStatusError = true;
                StatusMessage = err;
                return;
            }
            StatusMessage = $"已在标签坞打开「{group.DisplayName}」登录页 — 扫码完成后自动同步账号";
        }
        catch (ApiException ex)
        {
            IsStatusError = true;
            StatusMessage = $"添加失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>重新登录 = 开/切到该账号标签并武装重载登录页(会话有效秒回已登录态,失效落扫码页)</summary>
    [RelayCommand]
    private void ReLogin(PlatformSession? session)
    {
        if (session == null) return;
        var group = Groups.FirstOrDefault(g => g.PlatformKey == session.PlatformKey);
        if (group == null) return;
        _dock.OpenOrActivate(
            session.Id, session.PlatformKey, group.DisplayName, group.BrandColorHex,
            group.LoginUrl, session.PlatformName ?? session.AccountLabel,
            isPreCreated: false, forceLogin: true);
        IsStatusError = false;
        StatusMessage = $"已切换到「{session.DisplayLabel}」标签 — 若会话已失效请在页面重新扫码";
    }

    /// <summary>打开账号标签(坞内常驻 WebView;账号卡「打开」;重登语义 P3 归并后此处切换)</summary>
    [RelayCommand]
    private void OpenInDock(PlatformSession? session)
    {
        if (session == null) return;
        var group = Groups.FirstOrDefault(g => g.PlatformKey == session.PlatformKey);
        if (group == null) return;
        _dock.OpenOrActivate(
            session.Id, session.PlatformKey, group.DisplayName, group.BrandColorHex,
            group.LoginUrl, session.PlatformName ?? session.AccountLabel, isPreCreated: false);
    }

    // ═══ 改名 ═══

    [RelayCommand]
    private void RenameBegin(PlatformSession? session)
    {
        if (session == null) return;
        session.LabelDraft = session.AccountLabel ?? "";
        session.IsRenaming = true;
    }

    [RelayCommand]
    private void RenameCancel(PlatformSession? session)
    {
        if (session != null) session.IsRenaming = false;
    }

    [RelayCommand]
    private async Task RenameSave(PlatformSession? session)
    {
        if (session == null) return;
        var label = session.LabelDraft.Trim();
        if (label.Length == 0)
        {
            IsStatusError = true;
            StatusMessage = "备注不能为空";
            return;
        }
        if (label == session.AccountLabel) { session.IsRenaming = false; return; }

        IsLoading = true;
        try
        {
            await ApiClient.PostAsync<object>("/api/platform-sessions/rename",
                new RenameSessionRequest(session.Id, label));
            session.AccountLabel = label;
            session.IsRenaming = false;
            _dock.ApplyAccountLabel(session.Id, label); // 同步标签坞标题(备注优先)
            RefreshGroup(session);
            IsStatusError = false;
            StatusMessage = $"已将「{session.DisplayLabel}」重命名为「{label}」";
        }
        catch (ApiException ex)
        {
            IsStatusError = true;
            StatusMessage = $"保存失败：{ex.Message}";
        }
        catch (Exception)
        {
            IsStatusError = true;
            StatusMessage = "网络错误，请检查连接后重试";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ═══ 设为默认 ═══

    [RelayCommand]
    private async Task SetPrimary(PlatformSession? session)
    {
        if (session == null || session.IsPrimary) return;
        try
        {
            await ApiClient.PostAsync<object>("/api/platform-sessions/set-primary",
                new SessionIdRequest(session.Id));
            var group = Groups.FirstOrDefault(g => g.PlatformKey == session.PlatformKey);
            foreach (var s in group?.Sessions ?? [])
                s.IsPrimary = s.Id == session.Id;
            IsStatusError = false;
            StatusMessage = $"已将「{session.DisplayLabel}」设为默认账号";
        }
        catch (ApiException ex)
        {
            IsStatusError = true;
            StatusMessage = $"操作失败：{ex.Message}";
        }
    }

    // ═══ 解绑（两段式确认:先「解绑」展开卡片内确认条 → 「确认解绑」执行 Delete） ═══

    /// <summary>
    /// 点「解绑」→ 展开该卡片确认条（同时收起其它卡片的确认条）。
    /// 单机版先数一遍该账号在本端的记录数：没有服务端副本，删掉就永远失去删除入口 ——
    /// 确认条必须把「要删掉几条」说清楚（联网版沿用原文案，不加这趟查询）。
    /// </summary>
    [RelayCommand]
    private async Task UnbindBegin(PlatformSession? session)
    {
        if (session == null) return;
        if (AppConfig.IsStandalone)
            session.UnbindRecordCount = await CountSessionRecordsAsync(session.Id);
        foreach (var s in Groups.SelectMany(g => g.Sessions))
            s.IsConfirmingUnbind = s == session;
    }

    /// <summary>该会话在本端的发布记录 + 合集记录数（解绑确认条计数用；失败按 0 显示通用文案）</summary>
    private static async Task<int> CountSessionRecordsAsync(Guid sessionId)
    {
        try
        {
            var platforms = await ApiClient.PostAsync<PlatformListResponse>("/api/publish/platforms");
            var publish = platforms?.Platforms?.Count(p => p.PlatformSessionId == sessionId) ?? 0;

            var collections = await ApiClient.PostAsync<CollectionListResponse>("/api/collections/list");
            var collection = collections?.Collections?
                .Sum(c => c.Platforms?.Count(p => p.PlatformSessionId == sessionId) ?? 0) ?? 0;

            return publish + collection;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Platform] 统计解绑记录数失败（显示通用文案）");
            return 0;
        }
    }

    /// <summary>点「取消」→ 收起确认条</summary>
    [RelayCommand]
    private void UnbindCancel(PlatformSession? session)
    {
        if (session != null) session.IsConfirmingUnbind = false;
    }

    // ═══ 解绑执行（服务端删除 + 本地镜像与档案清理） ═══

    [RelayCommand]
    private async Task Delete(PlatformSession? session)
    {
        if (session == null) return;
        try
        {
            // 顺序纪律(标签坞常驻 + 解绑硬删记录后):
            //   ① 先关该账号标签(释放 WebView 才删得掉档案)
            //   ② 队列里该账号的行先停稳 —— 取消在跑的、等执行线程退出;服务端马上要删记录,
            //      执行线程迟到的回报会对不上（等完再发删除请求）
            //   ③ 服务端删(硬删该会话的本端发布/合集记录 + 会话行;失败即中止,本地回滚「停稳」)
            //   ④ 本地清行/清镜像 + 等锁/重试删档案(失败写 marker)
            // 平台上的作品与合集一律不碰 —— 服务端这条路径不调任何平台 SDK
            var tab = _dock.Find(session.Id);
            if (tab != null) await _dock.CloseTabAsync(tab);

            await _queue.StopSessionAsync(session.Id);

            PlatformSessionDeleteResult? result;
            var alreadyGone = false;
            try
            {
                result = await PlatformSessionApi.DeleteSessionAsync(session.Id);
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                // 服务端已无此会话（上次解绑成功但响应丢了 / 别的端删过）→ 按已解绑收尾
                result = null;
                alreadyGone = true;
            }

            if (result is null && !alreadyGone)
            {
                // 网络失败/空响应（PostAsync 对断网返回 null 而不抛）—— 本地必须不动，
                // 否则服务端没删成、本地卡先消失了（旧实现静默继续，顺手修掉）
                _queue.AbortSessionStop(session.Id);
                IsStatusError = true;
                StatusMessage = "解绑失败：网络错误，请重试";
                return;
            }

            _queue.PurgeSession(session.Id, session.PlatformKey);

            var group = Groups.FirstOrDefault(g => g.PlatformKey == session.PlatformKey);
            group?.Sessions.Remove(session);
            group?.RefreshStats();

            // 微信式：同步清本地镜像;档案目录清理含渲染进程等锁 + marker 兜底
            var deleted = await RemoveProfileGuardedAsync(session.Id);
            IsStatusError = false;
            var cleanup = result is null or { PublishRecordsDeleted: 0, CollectionRecordsDeleted: 0 }
                ? "本端无遗留记录"
                : $"清理了 {result.PublishRecordsDeleted} 条发布记录、{result.CollectionRecordsDeleted} 条合集记录";
            StatusMessage = $"已解绑「{session.DisplayLabel}」，{cleanup}，" +
                (deleted ? "本地档案已清理" : "档案将在下次启动补清理");
        }
        catch (ApiException ex)
        {
            _queue.AbortSessionStop(session.Id);
            IsStatusError = true;
            StatusMessage = $"解绑失败：{ex.Message}";
        }
    }

    /// <summary>清本地镜像 + 档案目录(等锁/重试/marker 由 store 内完成);返回目录是否已删</summary>
    private async Task<bool> RemoveProfileGuardedAsync(Guid sessionId)
    {
        var store = _localStore;
        var profileDir = store.ProfileDir(sessionId);
        await store.RemoveAsync(sessionId); // 镜像先删 + 后台尝试删目录
        return !Directory.Exists(profileDir);
    }

    // ═══ 状态探活 ═══

    [RelayCommand]
    private async Task Probe()
    {
        IsLoading = true;
        StatusMessage = "";
        try
        {
            var resp = await ApiClient.PostAsync<ProbeResponse>("/api/platform-sessions/probe");
            if (resp?.Results != null)
            {
                foreach (var result in resp.Results)
                {
                    var session = Groups
                        .SelectMany(g => g.Sessions)
                        .FirstOrDefault(s => s.Id == result.Id);
                    if (session == null) continue;
                    session.Status = result.Status;
                    session.StatusText = result.ProbedResult ?? session.StatusText;
                    session.ProbedAt = DateTime.Now;
                    session.ProbedResult = result.ProbedResult;
                }
            }
            foreach (var group in Groups) group.RefreshStats();
            IsStatusError = false;
            StatusMessage = "会话状态检测完成";
        }
        catch (ApiException ex)
        {
            IsStatusError = true;
            StatusMessage = $"检测失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RefreshGroup(PlatformSession session)
    {
        Groups.FirstOrDefault(g => g.PlatformKey == session.PlatformKey)?.RefreshStats();
    }
}
