namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 首页 —— 工作台总入口（决策 B，2026-09-07；2026-09-21 从「迁移期占位页」改成真入口页）。
/// 登录后的总入口：时段问候 + 三组入口卡（发布 / 发布之后 / 创作）+ 平台账号状态 + 会员状态。
///
/// **入口卡是纯入口，不显示实时数字**（有意为之）：队列 / 任务的计数在侧栏活徽章与各自页面上；
/// 要搬到首页得先把 <c>UploadQueueViewModel.OnStatsChanged</c> 这类**单赋值** Action 回调
/// 改成多播事件（现在已经被壳占着），属于另一件事 —— 别当遗漏来补。
/// 卡片跳页走 <see cref="OnNavigateRequested"/>（壳在 Navigate 时注入，页 VM 不认识壳）；
/// 过期会员的卡片由视图按 <see cref="IsExpired"/> 置灰，与侧栏灰显语义一致。
///
/// 平台账号概览：每次进入页面（INavigationAware）与坞内登录同步完成
/// （AccountDockViewModel.SessionSynced）时拉服务端会话列表按平台统计绑定账号数 ——
/// 服务端为唯一真相来源（本地 sessions.json 镜像只在登录同步时登记，老账号未在本端开过
/// 则缺失，不能当计数依据）。拉取失败静默保留上次计数（断网离线放行等场景不闪错）。
/// 监听 SessionContext.MembershipChanged：激活/续费后会员卡信息即时刷新。
/// </summary>
public partial class HomePageViewModel : ViewModelBase, INavigationAware
{
    /// <summary>6 平台模板（品牌定义静态；每次刷新以其为基底重建计数副本）</summary>
    private static readonly PlatformTile[] PlatformTemplate =
    [
        new("douyin", "抖音", "抖", "#161823"),
        new("kuaishou", "快手", "快", "#FF4906"),
        new("xiaohongshu", "小红书", "红", "#FF2442"),
        new("wechatvideo", "视频号", "视", "#07C160"),
        new("bilibili", "B站", "B", "#FB7299"),
        new("toutiao", "头条", "头", "#F04142"),
    ];

    private bool _refreshing;
    private bool _loaded; // 至少成功拉取过一次（false 时区块头不展示计数文案）
    private Dictionary<string, int> _boundCounts = new();

    public HomePageViewModel(SessionContext session, AccountDockViewModel dock)
    {
        Session = session;
        session.MembershipChanged += _ => RaiseMembershipProps();
        // 坞内登录同步完成 → 概览即时刷新（标签条常驻：同步可能发生在首页停留期间）
        dock.SessionSynced += s => { _ = RefreshCountsAsync(); };
    }

    public SessionContext Session { get; }

    /// <summary>导航请求（壳在 Navigate 时挂上 —— 页 VM 不认识壳，按仓库既有回调范式接线）</summary>
    public Action<string>? OnNavigateRequested { get; set; }

    /// <summary>
    /// 入口卡片的统一出口。<paramref name="key"/> 用侧栏导航键（publish / queue / tasks / video）。
    /// 过期会员在这里也拦一道：视图已把卡片置灰，但 <c>ShellViewModel.Navigate</c> 本身不看
    /// IsEnabled —— 只靠视图的话，一条程序化调用（冒烟巡检就是）就能把过期用户送进发布页。
    /// </summary>
    [RelayCommand]
    private void Go(string key)
    {
        if (string.IsNullOrEmpty(key) || Session.IsExpired) return;
        OnNavigateRequested?.Invoke(key);
    }

    public string Username => Session.Username;
    public bool IsExpired => Session.IsExpired;

    /// <summary>单机版没有会员体系：会员卡整块隐藏</summary>
    public bool ShowMembership => !AppConfig.IsStandalone;
    public string ExpireText => Session.ExpireText;

    /// <summary>会员剩余（大字展示）：剩 N 天 / 已过期 / 未知</summary>
    public string DaysLeftText => Session.DaysLeft switch
    {
        null => "会员状态未知",
        < 0 => "已过期",
        _ => $"剩 {Session.DaysLeft} 天"
    };

    /// <summary>到期日（会员卡次要行）</summary>
    public string ExpireDateText => Session.ExpireDate == null
        ? "未获取到到期时间"
        : $"有效期至 {Session.ExpireDate:yyyy-MM-dd HH:mm}";

    /// <summary>时段问候语</summary>
    public string Greeting => DateTime.Now.Hour switch
    {
        < 6 => "夜深了",
        < 9 => "早上好",
        < 12 => "上午好",
        < 14 => "中午好",
        < 18 => "下午好",
        _ => "晚上好"
    };

    /// <summary>
    /// 首页问候整行：联网版「下午好，用户名」；单机版没有账号概念，
    /// 只显示时段问候（2026-10-28 用户要求移除「本地用户」）。
    /// </summary>
    public string GreetingText => AppConfig.IsStandalone ? Greeting : $"{Greeting}，{Username}";

    /// <summary>今日日期（如 2026年9月7日 周一）</summary>
    public string DateText
    {
        get
        {
            var week = new[] { "日", "一", "二", "三", "四", "五", "六" }[(int)DateTime.Now.DayOfWeek];
            return $"{DateTime.Now:yyyy年M月d日} 周{week}";
        }
    }

    /// <summary>平台账号概览（6 平台；进页/坞内同步后整体替换 —— 未拉取前计数为空不误导）</summary>
    public IReadOnlyList<PlatformTile> Platforms => _platforms;
    private IReadOnlyList<PlatformTile> _platforms = PlatformTemplate;

    /// <summary>区块头右侧说明（成功拉取一次后才有值：总绑定数 / 引导去绑定）</summary>
    public string OverviewNote => !_loaded ? ""
        : TotalBound == 0 ? "可前往「平台账号」页绑定"
        : $"共绑定 {TotalBound} 个账号";

    /// <summary>六平台绑定总数（服务端上次成功快照；未知平台键不参与）</summary>
    private int TotalBound =>
        PlatformTemplate.Sum(t => _boundCounts.GetValueOrDefault(t.PlatformKey));

    public void OnNavigatedTo() => _ = RefreshCountsAsync();

    /// <summary>
    /// 拉服务端会话列表 → 按平台统计绑定数，重建小块列表。
    /// 失败静默保留旧计数：401 会话失效由壳统一处理（403 membership 已被 ApiClient
    /// 全局拦截），网络失败 PostAsync 返回 null 不抛 —— 进页/下次同步会再试。
    /// </summary>
    private async Task RefreshCountsAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            PlatformSessionListResponse? resp;
            try
            {
                resp = await ApiClient.PostAsync<PlatformSessionListResponse>("/api/platform-sessions/list");
            }
            catch (ApiException ex) when (ex.StatusCode == 401)
            {
                return;
            }
            if (resp?.Sessions == null) return;

            _boundCounts = resp.Sessions
                .Where(s => !string.IsNullOrEmpty(s.PlatformKey))
                .GroupBy(s => s.PlatformKey)
                .ToDictionary(g => g.Key, g => g.Count());
            _platforms = PlatformTemplate
                .Select(t => t with { Count = _boundCounts.GetValueOrDefault(t.PlatformKey) })
                .ToArray();
            _loaded = true;
            OnPropertyChanged(nameof(Platforms));
            OnPropertyChanged(nameof(OverviewNote));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Home platform overview refresh failed"); // 失败静默：保留旧计数
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RaiseMembershipProps()
    {
        OnPropertyChanged(nameof(IsExpired));
        OnPropertyChanged(nameof(ExpireText));
        OnPropertyChanged(nameof(DaysLeftText));
        OnPropertyChanged(nameof(ExpireDateText));
    }
}

/// <summary>
/// 首页平台账号小块（品牌定义静态 + 服务端绑定计数副本；色值随平台品牌）。
/// 每次刷新重建实例整体替换 Platforms，故无需可观察。
/// </summary>
public sealed record PlatformTile(
    string PlatformKey, string Name, string Initial, string ColorHex, int? Count = null)
{
    /// <summary>计数副行：Count=null（未拉取到）时不展示文案，避免误示「未绑定」</summary>
    public string CountText => Count switch
    {
        null => "",
        0 => "未绑定",
        _ => $"{Count} 个账号"
    };
}
