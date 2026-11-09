namespace CreatFlow.NewApp.ViewModels;

/// <summary>账号标签相位(状态机,由坞/宿主视图驱动)</summary>
public enum DockOpenPhase
{
    Creating,    // 宿主视图创建中(尚未导航)
    AwaitLogin,  // 已导航,等待登录(新号扫码 / 重登)
    Extracting,  // 检测到登录,正在提取+同步
    Ready,       // 已登录可浏览(标签 × 只关不删档案)
    Failed,      // 提取/同步失败(可重试)
}

/// <summary>
/// 账号坞单条标签 —— 展示态 + 宿主信息(一标签 = 一条独立 WebView2 档案)。
///
/// 仅持 Guid/原语与展示状态,不持 PlatformSession 对象引用
/// (平台页 Load 全量重建,跨组件联动一律以 SessionId 为主键)。
/// </summary>
public sealed partial class AccountDockTabViewModel : ObservableObject
{
    public AccountDockViewModel Dock { get; set; } = null!; // 由坞 Add 时注入(Close 委托)

    // ═══ 宿主信息(构造定死) ═══
    public Guid SessionId { get; }
    public string PlatformKey { get; }
    public string PlatformDisplayName { get; }
    public string BrandColorHex { get; }
    public string LoginUrl { get; }
    public string ProfileDir { get; }

    /// <summary>新号(尚未有登录成功记录):Ready 前关闭 = 取消 —— 服务端本无行(登录成功才建档),
    /// 仅清理本地档案 + 兜底删可能半途已建的行</summary>
    public bool IsPreCreated { get; set; }

    /// <summary>该标签的 WebView 已建且签名器已注册(小红书 x-s 签名用;宿主 Teardown 时回落)。
    /// 宿主建 WebView 是懒加载的(首激活才建)—— 签名前由坞 EnsureSignHostReadyAsync 轮询此标志。</summary>
    public bool SignHostReady { get; set; }

    /// <summary>重登武装(宿主监听 ArmAutoDetect:重载登录页 + 重建 extractor 自动检测)</summary>
    public void RequestRelogin()
    {
        ArmAutoDetect = true;
        Phase = DockOpenPhase.AwaitLogin;
        StatusText = "正在重新打开登录页…";
    }

    /// <summary>引导跳转（App 层「抖音要求身份验证」用）：宿主已建 → 立刻切页；
    /// 还没建 → 记在 <see cref="PendingNavigateUrl"/>，首次建宿主时直接落到该页（不走登录页）。</summary>
    public void RequestNavigate(string url)
    {
        PendingNavigateUrl = url;
        NavigateRequested?.Invoke(this, url);
    }

    /// <summary>待落的引导页（宿主建 WebView 时消费一次；null = 按 LoginUrl）</summary>
    public string? PendingNavigateUrl { get; set; }

    /// <summary>引导跳转请求（宿主视图监听；与 RequestRelogin 的「重载登录页」是两条路）</summary>
    public event Action<AccountDockTabViewModel, string>? NavigateRequested;

    /// <summary>自动检测开关(坞在建标签时拍板):新号登录/重登=true;已绑定账号本运行未验证过的
    /// 首开同样武装 —— 检测到登录态自动提取;验证成功转 Ready 后 = false(纯浏览态不再误触发)</summary>
    [ObservableProperty]
    private bool _armAutoDetect;

    // ═══ 展示态 ═══
    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private DockOpenPhase _phase = DockOpenPhase.Creating;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>标签条选中底(IsActive 变化时通知)</summary>
    public string ChipBackground => IsActive ? "#F4F1FE" : "#F2F3F5";
    public string ChipForeground => IsActive ? "#4B3BC3" : "#4E5969";

    /// <summary>可点态:提取中禁点防重入,其余(等待/失败/已同步/浏览)一律可点 —— 用户拍板
    /// 「完成登录一直显示,不管是否成功」:失败能立刻重试,成功后在浏览态点击=重新抓取一次凭证</summary>
    public bool CanClickManualComplete => Phase != DockOpenPhase.Extracting;

    /// <summary>按钮文案:提取中… 替代「完成登录」,让禁点有解释</summary>
    public string ManualCompleteText => Phase == DockOpenPhase.Extracting ? "提取中…" : "完成登录";

    public AccountDockTabViewModel(
        Guid sessionId, string platformKey, string platformDisplayName,
        string brandColorHex, string loginUrl, string profileDir,
        string? initialTitle, bool isPreCreated = false)
    {
        SessionId = sessionId;
        PlatformKey = platformKey;
        PlatformDisplayName = platformDisplayName;
        BrandColorHex = brandColorHex;
        LoginUrl = loginUrl;
        ProfileDir = profileDir;
        IsPreCreated = isPreCreated;
        _title = string.IsNullOrWhiteSpace(initialTitle)
            ? $"{platformDisplayName} · 未命名"
            : initialTitle!;
    }

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ChipBackground));
        OnPropertyChanged(nameof(ChipForeground));
    }

    partial void OnPhaseChanged(DockOpenPhase value)
    {
        OnPropertyChanged(nameof(CanClickManualComplete));
        OnPropertyChanged(nameof(ManualCompleteText));
        OnPropertyChanged(nameof(IsBusy));
    }

    /// <summary>标题/相位 → 视觉忙态(创建/提取中)</summary>
    public bool IsBusy => Phase is DockOpenPhase.Creating or DockOpenPhase.Extracting;
}
