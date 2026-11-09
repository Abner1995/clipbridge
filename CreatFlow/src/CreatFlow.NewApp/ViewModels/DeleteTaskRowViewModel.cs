using CreatFlow.NewApp.Models;
using CreatFlow.NewApp.Services;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 任务中心的一条**批次行**（一次「删除选中 N 项」= 一条任务）。
///
/// 与队列页的 <see cref="QueueTaskViewModel"/> **故意不共用类型**：
/// 那个的构造签名与状态文案全绑在上传语义上（`ProgressPct`、`HasFailure`、`!IsSettled` 的可见性
/// 都是「发布」的意思），塞进删除任务只会两边互相污染。共用的是**视觉**（`q-card` 那套样式）。
///
/// 会变的那几个字段是**就地更新**的（<see cref="Apply"/>），不是每次刷新换新对象 ——
/// 换对象的代价是展开态、明细、滚动位置全丢（见 <c>AsyncTaskViewModel.SyncTasks</c> 的注释）。
/// </summary>
public sealed partial class DeleteTaskRow : ObservableObject
{
    /// <summary>身份（刷新时靠它对上号）—— 不变，保持 init</summary>
    public required Guid Id { get; init; }
    public required DateTime CreatedAt { get; init; }

    // ── 服务端每次刷新都会变的字段 ──
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _kindText = "";
    [ObservableProperty] private int _status;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private int _failedCount;

    // ── 展示（纯函数在 AsyncTaskFormat，页面只绑）──

    public string DateText => AsyncTaskFormat.DateText(CreatedAt, Clock.Now);
    public string CountText => AsyncTaskFormat.CountText(DoneCount, TotalCount, FailedCount);
    public double ProgressValue => AsyncTaskFormat.ProgressValue(DoneCount, TotalCount);
    public string DotHex => AsyncTaskFormat.TaskDotHex(Status);
    public string PillBgHex => AsyncTaskFormat.TaskPillBgHex(Status);
    public string PillFgHex => AsyncTaskFormat.TaskPillFgHex(Status);

    /// <summary>有失败条目：行上要能一眼看出来（否则用户不知道有东西没删掉）</summary>
    public bool HasFailure => FailedCount > 0;

    /// <summary>已跑完（终态）—— 只有跑完的才允许删除这条任务记录</summary>
    public bool IsSettled => Status is 2 or 3 or 4 or 5;

    /// <summary>「重试失败项」只在有失败且已停稳时可用</summary>
    public bool CanRetry => HasFailure && IsSettled;

    /// <summary>删记录确认条文案（有失败项时先摆条 —— 见 AsyncTaskFormat.DeleteRecordConfirmText）</summary>
    public string DeleteConfirmText => AsyncTaskFormat.DeleteRecordConfirmText(FailedCount);

    /// <summary>展开态（用户交互状态，刷新不得覆盖）</summary>
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isBusy;

    /// <summary>「删除记录」确认条展开中（有失败项的任务必须先确认；用户交互状态，刷新不得覆盖）</summary>
    [ObservableProperty] private bool _isConfirmingDelete;

    /// <summary>明细懒加载：首次展开时的 2px 进度条（后台跟刷不点亮它，否则每 5 秒闪一下）</summary>
    [ObservableProperty] private bool _isLoadingItems;

    public ObservableCollection<DeleteTaskItemRow> Items { get; } = [];
    public bool HasItems => Items.Count > 0;

    /// <summary>子项集合被就地改过之后把 <see cref="HasItems"/> 播报出去</summary>
    public void NotifyItemsChanged() => OnPropertyChanged(nameof(HasItems));

    /// <summary>
    /// 用服务端最新状态**就地更新**。只碰服务端字段 ——
    /// <see cref="IsExpanded"/> / <see cref="IsBusy"/> / <see cref="IsLoadingItems"/> / <see cref="Items"/>
    /// 都是本地状态，一动就等于把用户的展开与明细抹掉。
    /// </summary>
    public void Apply(ServerAsyncTask t)
    {
        Title = t.Title;
        KindText = t.KindText;
        Status = t.Status;
        StatusText = t.StatusText;
        TotalCount = t.TotalCount;
        DoneCount = t.DoneCount;
        FailedCount = t.FailedCount;

        // 计算属性不会自己重算（`[ObservableProperty]` 只播报它自己那一个）
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(DotHex));
        OnPropertyChanged(nameof(PillBgHex));
        OnPropertyChanged(nameof(PillFgHex));
        OnPropertyChanged(nameof(HasFailure));
        OnPropertyChanged(nameof(IsSettled));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(DeleteConfirmText));
    }

    public static DeleteTaskRow From(ServerAsyncTask t)
    {
        var row = new DeleteTaskRow { Id = t.Id, CreatedAt = t.CreatedAt };
        row.Apply(t);
        return row;
    }
}

/// <summary>
/// 任务中心的一条**子项行**（一个 视频/合集 × 平台 × 账号）。
/// 账号名与平台侧 ID 都是**入队时的快照** —— 本地记录早删了，这里照样显示得出。
///
/// 同 <see cref="DeleteTaskRow"/>：服务端字段就地更新，交互态（<see cref="IsBusy"/>）保留。
/// </summary>
public sealed partial class DeleteTaskItemRow : ObservableObject
{
    /// <summary>身份（刷新时靠它对上号）</summary>
    public required Guid Id { get; init; }

    [ObservableProperty] private string _platformKey = "";
    [ObservableProperty] private string _platformName = "";
    [ObservableProperty] private string? _accountLabel;
    [ObservableProperty] private int _status;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _errorMsg;
    [ObservableProperty] private int _retryCount;
    [ObservableProperty] private string _brandHex = "#6C5CE7";

    public string SubText => AsyncTaskFormat.ItemSubText(PlatformName, AccountLabel, RetryCount);
    public string DotHex => AsyncTaskFormat.ItemDotHex(Status);

    /// <summary>终态失败才给重试按钮 —— 成功项重试没有意义（平台上已经删了）</summary>
    public bool CanRetry => AsyncTaskFormat.IsFailed(Status);

    /// <summary>「确认已删除」按钮可见（只对「平台删除失败」开放，口径见 AsyncTaskFormat.CanConfirmGone）</summary>
    public bool CanConfirmGone => AsyncTaskFormat.CanConfirmGone(Status);

    /// <summary>确认条展开中（用户交互状态，刷新不得覆盖）</summary>
    [ObservableProperty] private bool _isConfirmingGone;

    /// <summary>等待中（还没轮到）—— 行上写清「排队中」，别让用户以为卡住了</summary>
    public bool IsPending => Status == (int)TaskItemStatus.Pending;

    /// <summary>失败原因（没有就不显示这一行）</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMsg);

    /// <summary>正在重试（用户交互状态，刷新不得覆盖）</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>就地更新（理由同 <see cref="DeleteTaskRow.Apply"/>）</summary>
    public void Apply(ServerAsyncTaskItem i, IReadOnlyDictionary<string, string> brandByPlatform)
    {
        PlatformKey = i.PlatformKey;
        PlatformName = i.PlatformDisplayName;
        AccountLabel = i.SessionLabel;
        Status = i.Status;
        StatusText = i.StatusText;
        ErrorMsg = i.ErrorMsg;
        RetryCount = i.RetryCount;
        BrandHex = brandByPlatform.GetValueOrDefault(i.PlatformKey, "#6C5CE7");

        OnPropertyChanged(nameof(SubText));
        OnPropertyChanged(nameof(DotHex));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanConfirmGone));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(HasError));
    }

    public static DeleteTaskItemRow From(
        ServerAsyncTaskItem i, IReadOnlyDictionary<string, string> brandByPlatform)
    {
        var row = new DeleteTaskItemRow { Id = i.Id };
        row.Apply(i, brandByPlatform);
        return row;
    }
}
