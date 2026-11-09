namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 平台账号卡片模型（服务端 PlatformSessionItem + 平台配置合并；UI 观察对象）。
/// 状态徽章用色块非 emoji：Status 0=正常(绿) / 1=即将过期(橙) / 2=已过期(红) / 其他=灰。
/// </summary>
public partial class PlatformSession : ObservableObject
{
    // ═══ 服务端字段 ═══
    public Guid Id { get; init; }
    public string PlatformKey { get; init; } = "";

    [ObservableProperty]
    private string? _platformUid;

    [ObservableProperty]
    private string? _platformAvatar;

    // ═══ 平台配置（合并 PlatformConfigItem） ═══
    public string PlatformDisplayName { get; init; } = "";
    public string BrandColorHex { get; init; } = "#6C5CE7";

    // ═══ 可观察状态 ═══
    [ObservableProperty]
    private string? _accountLabel;

    [ObservableProperty]
    private bool _isPrimary;

    [ObservableProperty]
    private string? _platformName;

    [ObservableProperty]
    private int _status; // 0=正常 1=即将过期 2=已过期

    [ObservableProperty]
    private string _statusText = "未知";

    [ObservableProperty]
    private DateTime _loggedInAt;

    // 服务端估计值(默认登录 +3 天,非平台真实会话时效)—— 不展示(老版同款隐藏,见 CreatFlow.App)
    [ObservableProperty]
    private DateTime? _expiredAt;

    [ObservableProperty]
    private DateTime? _probedAt;

    [ObservableProperty]
    private string? _probedResult;

    // ═══ 改名编辑状态 ═══
    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _labelDraft = "";

    // ═══ 解绑确认状态（两段式防误触：确认条展开时隐藏常规操作行） ═══
    [ObservableProperty]
    private bool _isConfirmingUnbind;

    /// <summary>
    /// 解绑将清理的本端记录数（发布 + 合集）。单机版没有服务端副本 ——
    /// 删掉就永远失去这些作品的删除入口，确认条必须把代价说清楚（联网版仍用原文案）。
    /// </summary>
    [ObservableProperty]
    private int _unbindRecordCount;

    /// <summary>常规详情/操作行可见（改名或解绑确认中时隐藏）</summary>
    public bool ShowDetails => !IsRenaming && !IsConfirmingUnbind;

    /// <summary>解绑确认条可见</summary>
    public bool ShowUnbindConfirmStrip => IsConfirmingUnbind;

    /// <summary>确认条正文：单机版带上「将删除 N 条记录」的计数</summary>
    public string UnbindConfirmText => UnbindRecordCount > 0
        ? $"解绑将清除本机登录凭证与浏览器档案（不可恢复），并删除该账号在本端的 "
          + $"{UnbindRecordCount} 条发布/合集记录（含平台 ID）。"
          + "删除后本软件将无法再管理这些平台作品；平台上的账号、作品与合集本身均不受影响。"
        : "解绑将清除本机登录凭证与浏览器档案（不可恢复），并清理该账号在本端的全部发布记录与合集记录。平台上的账号、作品与合集均不受影响。";

    partial void OnUnbindRecordCountChanged(int value) => OnPropertyChanged(nameof(UnbindConfirmText));

    // ═══ 计算属性 ═══

    /// <summary>卡片主标题：备注 > 平台昵称 > 未命名</summary>
    public string DisplayLabel =>
        !string.IsNullOrWhiteSpace(AccountLabel) ? AccountLabel!
        : !string.IsNullOrWhiteSpace(PlatformName) ? PlatformName!
        : "未命名账号";

    /// <summary>副行：账号 ID + 默认标记</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(PlatformUid)) parts.Add($"账号 ID：{PlatformUid}");
            if (IsPrimary) parts.Add("默认账号");
            return string.Join("  ·  ", parts);
        }
    }

    public string LoginTimeText => LoggedInAt == default ? "" : $"登录于 {LoggedInAt:yyyy-MM-dd HH:mm}";
    public string ProbeText => ProbedAt == null ? "" : $"最近检测 {ProbedAt:MM-dd HH:mm}";

    /// <summary>卡片元信息行（登录/检测 · 分隔；有效期不显示 —— ExpiredAt 是估计值,见上）</summary>
    public string MetaText => string.Join("  ·  ",
        new[] { LoginTimeText, ProbeText }.Where(s => s.Length > 0));

    /// <summary>头像占位字（平台昵称首字 > 平台名首字 > 号）</summary>
    public string AvatarText
    {
        get
        {
            var src = !string.IsNullOrWhiteSpace(PlatformName) ? PlatformName! : PlatformDisplayName;
            return src.Length > 0 ? src[..1] : "号";
        }
    }

    /// <summary>状态徽章视觉（色块非 emoji）</summary>
    public string StatusBadgeBg => Status switch { 0 => "#E8FFEA", 1 => "#FFF7E6", 2 => "#FFECE8", _ => "#F2F3F5" };
    public string StatusBadgeFg => Status switch { 0 => "#00A870", 1 => "#FA8C16", 2 => "#F53F3F", _ => "#86909C" };
    public string StatusDotHex => Status switch { 0 => "#00B42A", 1 => "#FA8C16", 2 => "#F53F3F", _ => "#C9CDD4" };

    public string StatusLabel => Status switch
    {
        0 => "正常",
        1 => "即将过期",
        2 => "已过期",
        _ => "未知"
    };

    // ═══ 依赖通知 ═══
    partial void OnAccountLabelChanged(string? value) => OnPropertyChanged(nameof(DisplayLabel));
    partial void OnPlatformNameChanged(string? value)
    {
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(AvatarText));
    }
    partial void OnIsPrimaryChanged(bool value) => OnPropertyChanged(nameof(Subtitle));
    partial void OnStatusChanged(int value) =>
        OnPropertyChanged(nameof(StatusBadgeBg));
    partial void OnLoggedInAtChanged(DateTime value) => OnPropertyChanged(nameof(MetaText));
    partial void OnProbedAtChanged(DateTime? value) => OnPropertyChanged(nameof(MetaText));

    partial void OnIsRenamingChanged(bool value) => OnPropertyChanged(nameof(ShowDetails));
    partial void OnIsConfirmingUnbindChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDetails));
        OnPropertyChanged(nameof(ShowUnbindConfirmStrip));
    }

    // ═══ 组装 ═══
    public static PlatformSession FromApi(PlatformSessionItem item, PlatformConfigItem config) => new()
    {
        Id = item.Id,
        PlatformKey = item.PlatformKey,
        PlatformUid = item.PlatformUid,
        PlatformAvatar = item.PlatformAvatar,
        AccountLabel = item.AccountLabel,
        IsPrimary = item.IsPrimary,
        PlatformName = item.PlatformName,
        Status = item.Status,
        StatusText = item.StatusText,
        LoggedInAt = item.LoggedInAt,
        ExpiredAt = item.ExpiredAt,
        ProbedAt = item.ProbedAt,
        ProbedResult = item.ProbedResult,
        PlatformDisplayName = config.DisplayName,
        BrandColorHex = config.BrandColorHex,
    };

    /// <summary>登录/重登同步后更新本地字段（B 阶段接入）</summary>
    public void ApplySyncResult(PlatformSessionSyncResult result)
    {
        AccountLabel = result.AccountLabel;
        IsPrimary = result.IsPrimary;
        PlatformName = result.PlatformName;
        Status = result.Status;
        StatusText = result.StatusText;
        LoggedInAt = result.LoggedInAt;
        ExpiredAt = result.ExpiredAt;
        ProbedAt = null;
        ProbedResult = null;
    }
}

/// <summary>平台分组（左栏一个平台 = 一组账号）</summary>
public partial class PlatformGroup : ObservableObject
{
    public string PlatformKey { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string BrandColorHex { get; init; } = "#6C5CE7";
    public string LoginUrl { get; init; } = "";

    /// <summary>组内账号（必须 ObservableCollection —— ItemsControl 直接绑本集合,
    /// List 无变更通知 → 解绑 Remove 后卡片残留,同 Groups 踩坑;增删后仍需 RefreshStats 刷计数/汇总色）</summary>
    public ObservableCollection<PlatformSession> Sessions { get; } = [];

    /// <summary>左栏徽标字（平台名首字）</summary>
    public string AvatarText => DisplayName.Length > 0 ? DisplayName[..1] : "台";

    public bool IsEmpty => Sessions.Count == 0;
    public string CountText => Sessions.Count == 0 ? "未绑定" : $"{Sessions.Count} 个账号";
    public int ActiveCount => Sessions.Count(s => s.Status is 0 or 1);
    public int ExpiredCount => Sessions.Count(s => s.Status == 2);
    public string SummaryText => Sessions.Count == 0
        ? "未绑定账号"
        : $"正常 {ActiveCount} · 过期 {ExpiredCount} · 共 {Sessions.Count}";
    public string SummaryDotHex => Sessions.Count == 0
        ? "#C9CDD4"
        : ActiveCount > 0 ? (ExpiredCount > 0 ? "#FA8C16" : "#00B42A") : "#F53F3F";

    public void RefreshStats()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(SummaryDotHex));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(ExpiredCount));
    }
}
