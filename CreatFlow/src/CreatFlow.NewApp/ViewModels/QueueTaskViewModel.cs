using CreatFlow.NewApp.Models;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 队列单行的运行状态。
///
/// 前六个是**服务端状态**（<see cref="PublishPlatformStatus"/> 的显示名），
/// <see cref="Paused"/> 是**纯本地**状态 —— 用户点了暂停、或该账号登录已过期时挂起。
/// 服务端不知道「暂停」，所以同步时不能拿它的 Pending 把本地 Paused 打回去
/// （老版就这么干过，点了暂停三秒后又自己跑起来）。
/// </summary>
public enum QueueRowState
{
    Waiting = 0,
    Uploading = 1,
    Transcoding = 2,
    Publishing = 3,
    Published = 4,
    Failed = 5,
    Paused = 6,
}

/// <summary>
/// 队列里的「平台 × 账号」明细行（= 服务端一条 <c>x_publish_platform</c> 记录）。
///
/// 它是**执行单元**：消费循环按 (平台, 会话) 分组后逐行执行这一条。
/// 执行需要的字段（视频路径 + 平台行）在这里备齐，所以队列页不依赖发布页的草稿。
/// </summary>
public sealed partial class QueuePlatformRow : ObservableObject
{
    public QueuePlatformRow(ServerPlatformItem item, string brandHex, string accountLabel)
    {
        PlatformId = item.PlatformId;
        SessionId = item.PlatformSessionId;
        PlatformKey = item.PlatformKey;
        PlatformName = string.IsNullOrWhiteSpace(item.PlatformDisplayName)
            ? PlatformNames.GetDisplayName(item.PlatformKey)
            : item.PlatformDisplayName;
        // 账号名不在平台记录里（服务端 PublishPlatformWithTaskDto 只有 sessionId），
        // 由队列页从 /api/platform-sessions/list 的映射带进来
        _accountLabel = string.IsNullOrWhiteSpace(accountLabel) ? "未命名账号" : accountLabel;
        BrandHex = brandHex;
        Initial = PlatformName.Length > 0 ? PlatformName[..1] : "?";
        VideoFilePath = item.VideoFilePath;
        VideoFileName = item.VideoFileName;
        RetryCount = item.RetryCount;
        PublishedAt = item.PublishedAt;

        // 执行时要交给管线的字段（与 PublishPlatformItem 同形，直接构造 —— 队列页重试
        // 不该回查发布页的草稿：草稿提交后就被移除了）
        Item = new PublishPlatformItem(
            item.PlatformId, item.PlatformKey, PlatformName, item.PlatformSessionId,
            item.Title, item.Description, item.CoverFilePath, item.CoverUrl,
            item.CollectionId, item.ExtraDataJson, item.Status, item.StatusText,
            item.ProgressPct, item.ErrorMsg, item.RetryCount,
            item.PlatformVideoId, item.PlatformVideoUrl, item.PublishedAt, item.CreatedAt);

        ApplyServer(item.Status, item.ProgressPct, item.ErrorMsg);
    }

    public Guid PlatformId { get; }
    public Guid? SessionId { get; }
    public string PlatformKey { get; }
    public string PlatformName { get; }

    private string _accountLabel;

    /// <summary>账号名（备注 → 平台昵称 → 未命名）—— 用户改备注后由同步刷新，所以可写</summary>
    public string AccountLabel
    {
        get => _accountLabel;
        set => SetProperty(ref _accountLabel, value);
    }

    public string BrandHex { get; }
    public string Initial { get; }
    public string VideoFilePath { get; }
    public string VideoFileName { get; }
    public int RetryCount { get; }
    public DateTime? PublishedAt { get; private set; }

    /// <summary>交给管线执行的那份记录（重试时直接用，不再查服务端）</summary>
    public PublishPlatformItem Item { get; }

    /// <summary>会话 key（{平台}:{会话}）—— 分组、限速、账号异常横幅都按它归并</summary>
    public string SessionKey => PublishDraftStore.KeyOf(PlatformKey, SessionId ?? Guid.Empty);

    // ── 状态（服务端同步 + 本地覆盖）──

    [ObservableProperty] private QueueRowState _state = QueueRowState.Waiting;
    [ObservableProperty] private int _progressPct;
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private string? _errorMsg;

    /// <summary>失败详情面板展开态（行级 —— 放页级会全行齐开）</summary>
    [ObservableProperty] private bool _isErrorExpanded;

    /// <summary>批量操作勾选（汇总条「全选」与这里双向联动；纯 UI 态，不落盘）</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// 失败原因全文 —— 本地执行时留下的完整版。服务端 error_msg 列只有 500 字，
    /// 30s 同步会用截短版覆盖 <see cref="ErrorMsg"/>，这里保底（离开失败态即作废）。
    /// </summary>
    private string? _fullError;

    /// <summary>本行正在跑（消费循环或手动重试）—— 用来丢弃迟到的进度回调</summary>
    public CancellationTokenSource? TaskCts { get; set; }

    /// <summary>
    /// 本行正在执行的那个任务（消费循环启动时赋值，跑完不清 —— 读完即弃的过期引用无害）。
    ///
    /// 「放弃正在跑的行」要靠它：取消只是发信号，**执行线程什么时候真退出**只有它知道 ——
    /// 退出时的落点（取消成功→暂停 / 已经发布成功→Published）决定记录是删还是隐，
    /// 直接删会留下「平台上有视频、本地没记录」的幽灵作品。
    /// </summary>
    public Task? RunningTask { get; set; }

    /// <summary>
    /// 正在撤回：本地已标记、服务端删除请求还在路上。消费循环**不许认领**这种行 ——
    /// 撤回是「先收集要删的行，再逐条 await 删」，那个窗口足够消费循环（2 秒一轮）
    /// 把它翻成上传中并真发出去：用户以为撤回了，平台那边却已经收到视频。
    /// </summary>
    public bool IsWithdrawing { get; set; }

    /// <summary>用户手动暂停（服务端同步不能把它打回等待）</summary>
    public bool IsManuallyPaused => State == QueueRowState.Paused;

    /// <summary>
    /// 这次暂停是**账号登录过期**造成的（不是用户点的）—— 账号一恢复就该自动重排，
    /// 不能非等用户再点一次「继续全部」：重登路径不止横幅那一条。
    /// </summary>
    public bool PausedBySession { get; private set; }
    /// <summary>正在执行（等待/暂停都不画进度条）</summary>
    public bool IsRunning => State is QueueRowState.Uploading or QueueRowState.Transcoding
        or QueueRowState.Publishing;
    public bool IsFailed => State == QueueRowState.Failed;
    public bool IsPublished => State == QueueRowState.Published;
    /// <summary>还没落到终态（可以被撤回/暂停）</summary>
    public bool IsPending => State is QueueRowState.Waiting or QueueRowState.Paused;

    /// <summary>行级「暂停」按钮：等待中与正在跑的行可暂停（失败/已发布/已暂停不给）</summary>
    public bool ShowPauseButton => State is QueueRowState.Waiting or QueueRowState.Uploading
        or QueueRowState.Transcoding or QueueRowState.Publishing;

    /// <summary>行级「继续」按钮：只有**用户手动暂停**的行给（账号掉线挂起的由登录恢复路径自动重排）</summary>
    public bool ShowResumeButton => IsManuallyPaused && !PausedBySession;

    /// <summary>进度条只在真正有进度可看时出现</summary>
    public bool HasBar => State is QueueRowState.Uploading or QueueRowState.Transcoding
        or QueueRowState.Publishing or QueueRowState.Published;

    public string StateText => State switch
    {
        QueueRowState.Uploading => "上传中",
        QueueRowState.Transcoding => "等待平台转码",
        QueueRowState.Publishing => "发布中",
        QueueRowState.Published => PublishedAt is { } t ? $"已发布 · {Ago(t)}" : "已发布",
        QueueRowState.Failed => "失败",
        QueueRowState.Paused => "已暂停",
        _ => "等待中",
    };

    /// <summary>状态色点（零 emoji：品牌紫=进行中 / 绿=完成 / 红=失败 / 橙=暂停 / 灰=等待）</summary>
    public string DotHex => State switch
    {
        QueueRowState.Published => "#00B42A",
        QueueRowState.Failed => "#F53F3F",
        QueueRowState.Paused => "#FA8C16",
        QueueRowState.Waiting => "#C9CDD4",
        _ => "#6C5CE7",
    };

    public string StateColorHex => State switch
    {
        QueueRowState.Published => "#0A5C28",
        QueueRowState.Failed => "#C62828",
        QueueRowState.Paused => "#B45F06",
        QueueRowState.Waiting => "#86909C",
        _ => "#4B3BC3",
    };

    public string BarColorHex => State == QueueRowState.Published ? "#00B42A" : "#6C5CE7";

    /// <summary>失败详情全文（优先本地留的完整版；服务端同步的 500 字截短版垫底）</summary>
    public string ErrorDetailText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_fullError)) return _fullError;
            if (!string.IsNullOrWhiteSpace(ErrorMsg)) return ErrorMsg;
            return string.IsNullOrWhiteSpace(DetailText) ? "未知错误（无详细信息）" : DetailText;
        }
    }

    /// <summary>按关键词给的建议（移植老版失败详情弹窗的映射；「平台管理」已改名「平台账号」）</summary>
    public string ErrorSuggestion
    {
        get
        {
            var error = ErrorDetailText;
            if (error.Contains("登录") || error.Contains("凭证")
                || error.Contains("Cookie") || error.Contains("STS")
                || error.Contains("签名") || error.Contains("过期"))
            {
                return "登录凭证可能已失效：前往「平台账号」重新登录该平台，然后点「重试」。";
            }
            if (error.Contains("超时") || error.Contains("网络") || error.Contains("连接"))
                return "疑似网络问题：检查网络连接后，点「重试」再试一次。";
            return "点「重试」重新发布；若反复失败，请点「复制错误」反馈。";
        }
    }

    /// <summary>「复制错误」的文本 —— 连元信息一起（照老版失败详情弹窗）</summary>
    public string CopyText =>
        $"视频：{VideoFileName} · 平台：{PlatformName} · {AccountLabel} · 时间：{Item.CreatedAt:yyyy-MM-dd HH:mm}\n\n{ErrorDetailText}";

    /// <summary>服务端同步：只改「服务端说得清」的那部分状态</summary>
    public void ApplyServer(int status, int progressPct, string? errorMsg)
    {
        // 本地暂停/执行中的行不被服务端覆盖：
        //   · 暂停是本地概念，服务端一直是 Pending(0)，同步会把它打回等待 → 点了暂停又自己跑
        //   · 执行中的行服务端也只有 Pending（状态是执行结束时才回报的），同步会把进度清零
        if (State is QueueRowState.Paused || IsRunning) return;

        var mapped = status switch
        {
            1 => QueueRowState.Uploading,
            2 => QueueRowState.Transcoding,
            3 => QueueRowState.Publishing,
            4 => QueueRowState.Published,
            5 => QueueRowState.Failed,
            _ => QueueRowState.Waiting,
        };

        // 本地的**终态**也不被服务端「还没开始」打回去。这不是理论风险：
        // 回报结果那一步是容错的（TryReportAsync 吞掉失败），一旦网络在回报时抖一下，
        // 服务端就停在 Pending，而本地已经跑完 —— 回落成 Waiting 会被消费循环**再传一遍**，
        // 同一条视频发到同一个账号两次。只能前进不能后退。
        if (State is QueueRowState.Published or QueueRowState.Failed
            && mapped is QueueRowState.Waiting or QueueRowState.Uploading
                or QueueRowState.Transcoding or QueueRowState.Publishing)
            return;

        State = mapped;
        ProgressPct = progressPct is > 0 and <= 100 ? progressPct : State == QueueRowState.Published ? 100 : 0;
        ErrorMsg = errorMsg;
        if (mapped != QueueRowState.Failed)
        {
            _fullError = null;
            IsErrorExpanded = false;
        }
        // 详情行：失败给原因，其余给进度文案（等待时留空，不写「等待中」占位）
        DetailText = mapped switch
        {
            QueueRowState.Failed => errorMsg ?? "未知错误",
            _ => "",
        };
        RaiseDerived();
    }

    /// <summary>消费循环/重试在本地推进状态时用（服务端同步不会再覆盖它）</summary>
    /// <param name="pausedBySession">暂停原因是不是「账号登录过期」（见 <see cref="PausedBySession"/>）</param>
    public void ApplyLocal(QueueRowState state, int pct, string? detail, bool pausedBySession = false)
    {
        State = state;
        ProgressPct = pct;
        if (state == QueueRowState.Failed)
        {
            ErrorMsg = detail;
            // 留一份全文：服务端 error_msg 列只有 500 字，30s 同步会用截短版覆盖 ErrorMsg
            if (!string.IsNullOrWhiteSpace(detail)) _fullError = detail;
        }
        else
        {
            _fullError = null;
            IsErrorExpanded = false;   // 离开失败态（重试/继续）自动收起详情面板
        }
        DetailText = detail ?? (state == QueueRowState.Failed ? ErrorMsg ?? "" : "");
        if (state == QueueRowState.Published) PublishedAt = DateTime.Now;
        PausedBySession = state == QueueRowState.Paused && pausedBySession;
        RaiseDerived();
    }

    public void RaiseDerived()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(DotHex));
        OnPropertyChanged(nameof(StateColorHex));
        OnPropertyChanged(nameof(BarColorHex));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsPublished));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsManuallyPaused));
        OnPropertyChanged(nameof(HasBar));
        OnPropertyChanged(nameof(ShowPauseButton));
        OnPropertyChanged(nameof(ShowResumeButton));
        OnPropertyChanged(nameof(ErrorDetailText));
        OnPropertyChanged(nameof(ErrorSuggestion));
        OnPropertyChanged(nameof(CopyText));
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "刚刚";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} 分钟前";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} 小时前";
        return $"{(int)d.TotalDays} 天前";
    }
}

/// <summary>
/// 队列里的一张「视频」卡（= 服务端一个 <c>x_publish_task</c>）。
///
/// 为什么按视频聚合（设计文档 D7）：老版一行 = 视频 × 平台 × 账号，
/// 20 条视频 × 4 平台 = 80 行噪音，用户真正关心的是「这 20 条出去了没」。
/// </summary>
public sealed partial class QueueTaskViewModel : ObservableObject
{
    public QueueTaskViewModel(Guid taskId, string videoFilePath, string videoFileName,
        string? title, DateTime createdAt)
    {
        TaskId = taskId;
        VideoFilePath = videoFilePath;
        VideoFileName = videoFileName;
        // 卡片标题取平台记录里带的那份（提交时写进去的标题），没有就退回文件名
        Title = title is { Length: > 0 } ? title : videoFileName;
        CreatedAt = createdAt;
    }

    public Guid TaskId { get; }
    public string VideoFilePath { get; }
    public string VideoFileName { get; }
    public string Title { get; }
    public DateTime CreatedAt { get; }

    public ObservableCollection<QueuePlatformRow> Rows { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    /// <summary>`48.2 MB · 4 个平台 · 14:32 提交` —— 时长不在平台记录里（要本地 ffprobe），就不假装有</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (SizeText is { Length: > 0 } s) parts.Add(s);
            parts.Add($"{Rows.Count} 个平台");
            parts.Add($"{CreatedAt:HH:mm} 提交");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>文件大小（本地 FileInfo，不读文件内容；文件不在了就不显示）</summary>
    public string? SizeText
    {
        get
        {
            try
            {
                var fi = new FileInfo(VideoFilePath);
                return fi.Exists ? FormatSize(fi.Length) : null;
            }
            catch { return null; }
        }
    }

    public int PublishedCount => Rows.Count(r => r.IsPublished);
    public int FailedCount => Rows.Count(r => r.IsFailed);
    public int RunningCount => Rows.Count(r => r.IsRunning);
    public int PausedCount => Rows.Count(r => r.IsManuallyPaused);
    public int WaitingCount => Rows.Count(r => r.State == QueueRowState.Waiting);

    /// <summary>全部落定（成功或失败都算）—— 排序与状态文案用</summary>
    public bool IsSettled => Rows.All(r => r.State is QueueRowState.Published or QueueRowState.Failed);

    /// <summary>卡内还有「未发布成功」的行（撤回收集的就是这些，含失败行）—— 撤回按钮的可见性</summary>
    public bool HasUnpublished => Rows.Any(r => !r.IsPublished);

    /// <summary>卡内有已发布的行 —— 「隐藏」按钮的可见性。
    /// 用「有」而不是原来的「全部成功（IsDone）」：成功+失败混合的卡以前两边按钮都不满足，
    /// 失败行永远收不掉（死卡）</summary>
    public bool HasPublished => Rows.Any(r => r.IsPublished);

    public int UnpublishedCount => Rows.Count(r => !r.IsPublished);

    // ── 撤回确认条（卡级一次可能删多行，破坏性操作要过一步确认）──
    //    项目惯例是**原地确认条**而不是弹窗（合集卡 ShowDeleteConfirmStrip 同款）

    [ObservableProperty] private bool _isConfirmingWithdraw;

    partial void OnIsConfirmingWithdrawChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowWithdrawAsk));
        OnPropertyChanged(nameof(ShowWithdrawConfirm));
    }

    /// <summary>「撤回」按钮：还有未完成的行、且不在确认态</summary>
    public bool ShowWithdrawAsk => HasUnpublished && !IsConfirmingWithdraw;

    public bool ShowWithdrawConfirm => IsConfirmingWithdraw;

    /// <summary>确认条文案 —— 有正在跑的要把「会先停止」讲出来</summary>
    public string WithdrawConfirmText
    {
        get
        {
            var running = Rows.Count(r => r.IsRunning && !r.IsPublished);
            return running > 0
                ? $"撤回 {UnpublishedCount} 条未完成记录（{running} 条正在上传会先停止）？"
                : $"撤回 {UnpublishedCount} 条未完成记录？";
        }
    }

    public string StatusText => IsSettled
        ? (FailedCount == 0 ? "已全部完成" : PublishedCount == 0 ? "全部失败" : "部分失败")
        : RunningCount > 0
            ? "进行中"
            : PausedCount > 0 ? "已暂停" : "等待中";

    public string StatusColorHex => StatusText switch
    {
        "已全部完成" => "#0A5C28",
        "全部失败" or "部分失败" => "#C62828",
        "已暂停" => "#B45F06",
        "进行中" => "#4B3BC3",
        _ => "#86909C",
    };

    public string StatusBgHex => StatusText switch
    {
        "已全部完成" => "#EBF7EE",
        "全部失败" or "部分失败" => "#FDECEA",
        "已暂停" => "#FFF3E8",
        "进行中" => "#F4F1FE",
        _ => "#F2F3F5",
    };

    /// <summary>`4 / 4 成功`；还没落定时为 null（不占那一行）</summary>
    public string? CountText => IsSettled && FailedCount == 0 ? $"{PublishedCount} / {Rows.Count} 成功"
        : FailedCount > 0 ? $"{PublishedCount} 成功 · {FailedCount} 失败"
        : null;

    public string CountDotHex => FailedCount > 0 ? "#F53F3F" : "#00B42A";

    public bool HasFailure => FailedCount > 0;
    public bool IsDone => IsSettled && FailedCount == 0;

    /// <summary>缩略图底色 —— 按任务 Id 稳定取一个浅色，同一张卡每次进页都一样</summary>
    public string ThumbTint
    {
        get
        {
            string[] tints = ["#E8E6FB", "#DCE7FB", "#FBE3D6", "#D6E8DC", "#F6E3F0"];
            // 用无符号取模：GetHashCode 可能是 int.MinValue，Math.Abs 会直接抛溢出
            return tints[(int)((uint)TaskId.GetHashCode() % (uint)tints.Length)];
        }
    }

    public void RaiseDerived()
    {
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColorHex));
        OnPropertyChanged(nameof(StatusBgHex));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CountDotHex));
        OnPropertyChanged(nameof(HasFailure));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsSettled));
        OnPropertyChanged(nameof(HasUnpublished));
        OnPropertyChanged(nameof(HasPublished));
        OnPropertyChanged(nameof(UnpublishedCount));
        OnPropertyChanged(nameof(ShowWithdrawAsk));
        OnPropertyChanged(nameof(WithdrawConfirmText));
        OnPropertyChanged(nameof(PublishedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(PausedCount));
        OnPropertyChanged(nameof(WaitingCount));
    }

    internal static string FormatDuration(int seconds) =>
        seconds >= 3600
            ? $"{seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2}"
            : $"{seconds / 60:D2}:{seconds % 60:D2}";

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };
}
