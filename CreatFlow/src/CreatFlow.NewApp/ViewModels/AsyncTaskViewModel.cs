using System.ComponentModel;
using CreatFlow.NewApp.Models;
using CreatFlow.NewApp.Services;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 异步任务页 —— 批量删除的**可见面**。
///
/// 与上传队列并列的独立一页：那边是「发出去」，这边是「撤回来」。
/// 两页都是「服务端权威状态 + 桌面端执行」，但队列不同、失败的含义也不同，各看各的最清楚。
///
/// 本页**只读 + 重试/删记录**，绝不自己执行删除 —— 真正干活的是
/// <see cref="DeleteTaskConsumer"/>（应用级单例，跟这一页在不在没关系）：
/// 用户在这一页看着，或者压根没打开这一页，后台都在跑。
/// </summary>
public sealed partial class AsyncTaskViewModel : ViewModelBase, INavigationAware
{
    private readonly Dictionary<string, string> _brandByPlatform = [];
    private bool _metaLoaded;

    /// <summary>正在拉明细的批次（重入闸：5 秒轮询与用户点击可能撞上同一条）</summary>
    private readonly HashSet<Guid> _detailLoading = [];

    private Timer? _timer;

    /// <summary>与消费者同节拍（5 秒），页面看起来才是「活的」</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    public ObservableCollection<DeleteTaskRow> Tasks { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isStatusError;
    [ObservableProperty] private bool _onlyFailed;

    public bool HasError => Error.Length > 0;
    public bool HasStatus => StatusMessage.Length > 0;

    /// <summary>顶部那条操作反馈的颜色（失败红 / 成功绿）—— 与平台页 IsStatusError 同语义</summary>
    public string StatusFgHex => IsStatusError ? "#C62828" : "#0A5C28";

    partial void OnIsStatusErrorChanged(bool value) => OnPropertyChanged(nameof(StatusFgHex));
    public bool HasTasks => Tasks.Count > 0;
    public bool IsEmpty => !IsLoading && !HasTasks;
    public string SummaryText => OnlyFailed
        ? $"有失败的删除任务（共 {Tasks.Count} 条）"
        : $"共 {Tasks.Count} 条删除任务";
    public string OnlyFailedText => OnlyFailed ? "看全部" : "仅看失败";

    partial void OnOnlyFailedChanged(bool value) => _ = RefreshAsync();

    // ═══ 生命周期 ═══

    /// <summary>
    /// 进页即刷新 + 起定时器。
    ///
    /// ⚠️ **不挡 `App.SmokeMode`** —— 本页只读（GET 性质的 POST），
    /// 不会像队列页那样真把东西发出去。冒烟巡检需要渲染它，挡了就测不到模板。
    /// </summary>
    public void OnNavigatedTo()
    {
        _ = RefreshAsync();
        _timer ??= new Timer(_ => Dispatcher.UIThread.Post(() => _ = RefreshAsync()),
            null, RefreshInterval, RefreshInterval);
    }

    /// <summary>登出/会员失效时停掉定时器（App 层调用）—— 凭证已失效，再打接口只会一路报错</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        Tasks.Clear();
        _metaLoaded = false;
        _brandByPlatform.Clear();
        StatusMessage = "";
        Error = "";
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(HasError));
    }

    // ═══ 加载 ═══

    public async Task RefreshAsync()
    {
        if (IsLoading) return;      // 防重入：定时器 + 手动刷新可能撞上
        IsLoading = true;
        OnPropertyChanged(nameof(IsLoading));
        try
        {
            await EnsureMetaAsync();

            var resp = await AsyncTaskApi.ListAsync(1, 50, kind: null, onlyUnsettled: false,
                onlyFailed: OnlyFailed);
            if (resp is null)
            {
                Error = "服务端没有返回数据";
                return;
            }

            Error = "";
            SyncTasks(resp.Items);
            await RefreshExpandedDetailsAsync();
            OnPropertyChanged(nameof(HasTasks));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(HasError));
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            /* 未登录：静默（--smoke 在没有服务端/未登录时也会走到这一页，不能炸） */
        }
        catch (Exception ex)
        {
            Error = "加载失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>平台品牌色（子项的色点）。失败静默走兜底色 —— 这只是装饰，不该挡住列表</summary>
    private async Task EnsureMetaAsync()
    {
        if (_metaLoaded) return;
        try
        {
            var platforms = await AsyncTaskApi.PlatformsAsync();
            foreach (var p in platforms ?? [])
                if (!string.IsNullOrWhiteSpace(p.BrandColorHex))
                    _brandByPlatform[p.PlatformKey] = p.BrandColorHex;
            _metaLoaded = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DeleteTask] 拉平台配置失败（用兜底色）");
        }
    }

    // ═══ 命令 ═══

    /// <summary>手动刷新（RefreshAsync 本身是 public，别处直接 await 它）</summary>
    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    [RelayCommand]
    private void ToggleOnlyFailed() => OnlyFailed = !OnlyFailed;

    /// <summary>
    /// 展开/收起明细。**<see cref="DeleteTaskRow.IsExpanded"/> 是唯一真值来源**。
    ///
    /// 视图上的三角是 `IsChecked` **OneWay** 绑到它 + 挂本命令：
    /// 控件点一下先自己翻 <c>IsChecked</c>（只当视觉反馈），命令再把 VM 翻到新值并与控件对齐。
    /// 为什么不做 TwoWay：那样控件侧的**瞬时状态会被写回 VM** —— 实测出现过
    /// 「展开着的一行自己合上」（一次 6 秒冒烟里偶发，约 1/12），而且 TwoWay + 命令各翻一次
    /// 会互相抵消（就是 2026-09-22 修掉的「三角点了没反应」）。
    /// </summary>
    [RelayCommand]
    private void ToggleExpand(DeleteTaskRow? row)
    {
        if (row is null) return;
        row.IsExpanded = !row.IsExpanded;
    }

    // ═══ 列表同步（就地更新，不重建行）═══

    /// <summary>
    /// 服务端那一页 → 现有行**就地更新**。
    ///
    /// 为什么不是 `Tasks.Clear()` + 重加（2026-09-22 之前是那么写的）：
    /// 行对象一换，用户刚展开的三角下一刷就合上、明细全丢、滚动位置弹回顶部 ——
    /// 5 秒一刷的页面上就成了「倒三角点不开、点开也看不清」。按 Id 复用实例之后，
    /// 展开态/明细/滚动位置都活着，每 5 秒换掉的只有文案与计数。
    /// </summary>
    private void SyncTasks(IReadOnlyList<ServerAsyncTask> items)
    {
        var byId = Tasks.ToDictionary(r => r.Id);
        var ordered = new List<DeleteTaskRow>(items.Count);

        foreach (var t in items)
        {
            if (byId.TryGetValue(t.Id, out var row))
            {
                row.Apply(t);
            }
            else
            {
                row = DeleteTaskRow.From(t);
                // 展开即拉明细：视图那边三角只做两件事 —— 翻 IsExpanded、把值写回来
                //（命令已在 2026-09-22 去掉，见 AsyncTaskView 的注释）
                row.PropertyChanged += OnRowPropertyChanged;
            }
            ordered.Add(row);
        }

        CollectionSync.Sync(Tasks, ordered);
    }

    /// <summary>一条批次的子项就地更新（同 <see cref="SyncTasks"/> 的理由）</summary>
    private void SyncItems(DeleteTaskRow row, IReadOnlyList<ServerAsyncTaskItem> items)
    {
        var byId = row.Items.ToDictionary(i => i.Id);
        var ordered = new List<DeleteTaskItemRow>(items.Count);

        foreach (var i in items)
        {
            if (byId.TryGetValue(i.Id, out var reuse))
            {
                reuse.Apply(i, _brandByPlatform);
                ordered.Add(reuse);
            }
            else
            {
                ordered.Add(DeleteTaskItemRow.From(i, _brandByPlatform));
            }
        }

        CollectionSync.Sync(row.Items, ordered);
        row.NotifyItemsChanged();
    }

    /// <summary>行展开 → 首次拉明细（视图只翻 IsExpanded，请求从这里发出）</summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DeleteTaskRow.IsExpanded)) return;
        if (sender is DeleteTaskRow { IsExpanded: true } row) _ = LoadDetailAsync(row, firstLoad: true);
    }

    /// <summary>
    /// 拉一条批次的明细。**首次展开才点 2px 进度条** —— 后台每 5 秒跟刷一次，
    /// 每次都点亮进度条的话页面上会一直有东西在闪。
    /// </summary>
    private async Task LoadDetailAsync(DeleteTaskRow row, bool firstLoad)
    {
        if (!_detailLoading.Add(row.Id)) return;      // 同一条已经在拉了
        if (firstLoad) row.IsLoadingItems = true;
        try
        {
            var detail = await AsyncTaskApi.DetailAsync(row.Id);
            if (detail is not null) SyncItems(row, detail.Items);
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            /* 未登录：静默（--smoke 在没有服务端/未登录时也会走到这一页，不能炸） */
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DeleteTask] 拉明细失败 taskId={Id}", row.Id);
            StatusMessage = "加载明细失败：" + ex.Message;
            IsStatusError = true;
            OnPropertyChanged(nameof(HasStatus));
        }
        finally
        {
            _detailLoading.Remove(row.Id);
            if (firstLoad) row.IsLoadingItems = false;
        }
    }

    /// <summary>
    /// 展开着的批次跟着刷明细 —— 用户展开就是为了看「哪几条还没删掉」，
    /// 只刷计数不刷明细的话，那个面板会一直停在他展开那一刻的样子。
    /// </summary>
    private async Task RefreshExpandedDetailsAsync()
    {
        foreach (var row in Tasks.Where(r => r.IsExpanded).ToList())
            await LoadDetailAsync(row, firstLoad: false);
    }

    // ═══ 重试 / 删记录 ═══

    /// <summary>重试一条失败子项</summary>
    [RelayCommand]
    private async Task RetryItemAsync(DeleteTaskItemRow? item)
    {
        if (item is null || item.IsBusy) return;
        item.IsBusy = true;
        try
        {
            await AsyncTaskApi.RetryAsync(itemId: item.Id);
            StatusMessage = "已放回队列，稍后会自动重试";
            IsStatusError = false;
            OnPropertyChanged(nameof(HasStatus));
        }
        catch (Exception ex)
        {
            StatusMessage = "重试失败：" + ex.Message;
            IsStatusError = true;
            OnPropertyChanged(nameof(HasStatus));
        }
        finally
        {
            item.IsBusy = false;
            await RefreshAsync();
        }
    }

    /// <summary>
    /// 摆出「确认已删除 → 清理本地记录」确认条（只翻本地状态，不发任何请求）。
    /// 见 <see cref="ConfirmGoneAsync"/> 的口径。
    /// </summary>
    [RelayCommand]
    private void ConfirmGoneBegin(DeleteTaskItemRow? item)
    {
        if (item is not null && item.CanConfirmGone) item.IsConfirmingGone = true;
    }

    /// <summary>确认条「取消」</summary>
    [RelayCommand]
    private void ConfirmGoneCancel(DeleteTaskItemRow? item)
    {
        if (item is not null) item.IsConfirmingGone = false;
    }

    /// <summary>
    /// 手动兜底：用户亲自核对后确认「平台侧其实已删除」→ 服务端把这条按
    /// 「平台上已不存在」收尾（全组清完时合集/记录一起清掉）。
    ///
    /// 为什么要有它（2026-10-14 用户实测）：快手合集删除平台侧每次成功却被误判失败，
    /// 合集是「全组清完才删本地记录」，一条误判就把整张卡永久卡住 —— 用户没有任何出路。
    /// 这个口子只在「平台删除失败」类条上出现，且文案写明风险（平台侧若仍在将失去管理入口）。
    /// </summary>
    [RelayCommand]
    private async Task ConfirmGoneAsync(DeleteTaskItemRow? item)
    {
        if (item is null || item.IsBusy) return;
        item.IsBusy = true;
        try
        {
            var result = await AsyncTaskApi.ConfirmGoneAsync(item.Id);
            if (result is null)
            {
                StatusMessage = "确认清理失败：服务端没有返回结果";
                IsStatusError = true;
            }
            else if (result.GroupHasFailure)
            {
                StatusMessage = "已按「平台上已不存在」清理这条；该合集还有没删掉的账号，记录先保留";
                IsStatusError = false;
            }
            else
            {
                StatusMessage = "已按「平台上已不存在」清理这条";
                IsStatusError = false;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "确认清理失败：" + ex.Message;
            IsStatusError = true;
        }
        finally
        {
            item.IsBusy = false;
            item.IsConfirmingGone = false;
            OnPropertyChanged(nameof(HasStatus));
            await RefreshAsync();
        }
    }

    /// <summary>重试一整批里的失败项</summary>
    [RelayCommand]
    private async Task RetryTaskAsync(DeleteTaskRow? row)
    {
        if (row is null || row.IsBusy) return;
        row.IsBusy = true;
        try
        {
            await AsyncTaskApi.RetryAsync(taskId: row.Id);
            StatusMessage = "已把失败项放回队列，稍后会自动重试";
            IsStatusError = false;
            OnPropertyChanged(nameof(HasStatus));
        }
        catch (Exception ex)
        {
            StatusMessage = "重试失败：" + ex.Message;
            IsStatusError = true;
            OnPropertyChanged(nameof(HasStatus));
        }
        finally
        {
            row.IsBusy = false;
            await RefreshAsync();
        }
    }

    /// <summary>
    /// 删掉一条已跑完的任务记录（进行中的会被服务端拒绝）。
    ///
    /// **有失败项的任务先摆确认条**（再点一次才真删）：删掉任务 = 丢掉失败项的
    /// 重试/确认清理入口，被它们卡住的本地记录（合集全组清不完）就再没有出口了。
    /// </summary>
    [RelayCommand]
    private async Task DeleteTaskAsync(DeleteTaskRow? row)
    {
        if (row is null || row.IsBusy) return;

        if (row.HasFailure && !row.IsConfirmingDelete)
        {
            row.IsConfirmingDelete = true;   // 就地摆条，这一下不发任何请求
            return;
        }

        row.IsConfirmingDelete = false;
        row.IsBusy = true;
        try
        {
            await AsyncTaskApi.DeleteAsync(row.Id);
            StatusMessage = $"已删除任务「{row.Title}」";
            IsStatusError = false;
            OnPropertyChanged(nameof(HasStatus));
        }
        catch (Exception ex)
        {
            StatusMessage = "删除失败：" + ex.Message;
            IsStatusError = true;
            OnPropertyChanged(nameof(HasStatus));
        }
        finally
        {
            row.IsBusy = false;
            await RefreshAsync();
        }
    }

    /// <summary>删记录确认条「取消」</summary>
    [RelayCommand]
    private void CancelDeleteTask(DeleteTaskRow? row)
    {
        if (row is not null) row.IsConfirmingDelete = false;
    }

    /// <summary>清空已完成（进行中的一条都不动）</summary>
    [RelayCommand]
    private async Task ClearFinishedAsync()
    {
        if (IsLoading) return;
        try
        {
            var removed = await AsyncTaskApi.ClearFinishedAsync();
            StatusMessage = removed is > 0 ? $"已清空 {removed} 条已完成任务" : "没有已完成的任务";
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            StatusMessage = "清空失败：" + ex.Message;
            IsStatusError = true;
        }
        finally
        {
            await RefreshAsync();
            OnPropertyChanged(nameof(HasStatus));
        }
    }
}
