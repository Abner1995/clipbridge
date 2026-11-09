using Material.Icons;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 主窗口壳 ViewModel（DI + 数据驱动导航）。
///
/// 职责：导航分组（NavSection/NavItem）→ 选中项驱动 CurrentPage；
/// 页面经 <see cref="_pageResolver"/> 从 DI 容器解析并缓存（切换后状态保留）。
/// 会话信息取自注入的 <see cref="SessionContext"/>。
///
/// 认证门控（2026-10-11 用户规则「凡是功能页面，都必须登录且激活才能使用」）：
///   NotLoggedIn → 登录窗（不存在壳）；Expired → 仅「激活会员」「账号设置」可用 + 默认落在激活页
///   （设备管理原按老版语义放行，本轮收进激活门；登出按钮在侧栏底部，不靠页面）；
///   Active → 全部真页可用。激活成功（<see cref="SessionContext.MembershipChanged"/>）→
///   重建导航可用性 + 补启动后台执行器 + 自动跳回首页。
/// </summary>
public partial class ShellViewModel : ViewModelBase
{
    private readonly Func<Type, ViewModelBase> _pageResolver;
    private readonly Dictionary<Type, ViewModelBase> _pageCache = new();
    private readonly UploadQueueViewModel _queue;
    private IReadOnlyList<NavSection> _sections = [];
    private NavItem _homeItem = null!;
    /// <summary>激活会员入口（单机版没有 —— 为 null；只有联网版的过期分支会用到它）</summary>
    private NavItem? _activationItem;
    private NavItem? _queueItem;
    private NavItem? _tasksItem;

    private readonly DeleteTaskConsumer _deleteTasks;
    private readonly ComposeQueueViewModel _composeQueue;
    private readonly TtsQueueViewModel _ttsQueue;
    private readonly DownloadQueueViewModel _downloadQueue;
    private NavItem? _composeQueueItem;
    private NavItem? _ttsQueueItem;
    private NavItem? _downloadItem;

    public ShellViewModel(SessionContext session,
        Func<Type, ViewModelBase> pageResolver, AccountDockViewModel dock,
        UploadQueueViewModel queue, DeleteTaskConsumer deleteTasks,
        ComposeQueueViewModel composeQueue, TtsQueueViewModel ttsQueue,
        DownloadQueueViewModel downloadQueue)
    {
        Session = session;
        Dock = dock;
        _queue = queue;
        _deleteTasks = deleteTasks;
        _composeQueue = composeQueue;
        _ttsQueue = ttsQueue;
        _downloadQueue = downloadQueue;
        _pageResolver = pageResolver;
        // 激活成功/会员变更 → 重建门控；壳窗口关闭后 VM 仍被单例会话持有（无害，事件仅重算）
        session.MembershipChanged += OnMembershipChanged;

        // 队列页是**消费者**（上传+发布在它那里跑）—— 开壳即启动，别等用户点进队列页，
        // 否则「提交完就关页面」的任务永远不动（老版起壳时同样常驻）
        _queue.OnStatsChanged = ApplyQueueBadge;

        // 删除任务的消费者同理：用户点了删除就走人，靠它把剩下的事干完
        _deleteTasks.OnStatsChanged = ApplyTaskBadge;

        // 合成队列同理（本机任务，与服务端无关）：合成页点了「开始合成」用户可能就走了 ——
        // 开壳即启动，把队列跑完（重开续跑：排队中的继续、合成中标已中断可重试）
        _composeQueue.OnStatsChanged = ApplyComposeBadge;

        // 配音队列同理（本机任务）：配音页批量提交后用户可能就走了 —— 开壳即启动；
        // 与合成队列的差别：可暂停/继续（暂停态进存档），消费者领取前查许可（过期自动暂停）
        _ttsQueue.OnStatsChanged = ApplyTtsQueueBadge;

        // 视频下载同理（本机任务）：解析/入队后用户可能就走了 —— 开壳即启动，跑完为止
        _downloadQueue.OnStatsChanged = ApplyDownloadBadge;

        // 后台执行器一律要「登录且激活」（用户规则：凡是功能页面，都必须登录且激活才能使用）——
        // 过期会员登录进来只看激活页，后台一件活都不跑（服务端任务停 Pending、本机任务留队）；
        // 激活成功后由 OnMembershipChanged 补启动
        if (!Session.IsExpired) StartConsumers();

        RebuildNav();
        // 默认页：过期→激活 / 有效→首页（单机版永远没有激活页，退到首页）
        Navigate(Session.IsExpired ? _activationItem ?? _homeItem : _homeItem);
    }

    /// <summary>启动四个后台执行器（幂等，许可恢复 / 重进壳时可反复调）：
    /// 上传 / 删除是服务端任务（认领不改状态，停掉后重登自动接续）；
    /// 合成 / 配音是本机队列（任务快照在本地存档，重开续跑）</summary>
    private void StartConsumers()
    {
        _queue.Start();
        _deleteTasks.Start();
        _composeQueue.Resume();   // 登出/撤权时 App 置的许可停摆先清（没有停摆是空操作）
        _composeQueue.Start();
        _ttsQueue.Start();
        _downloadQueue.Resume();   // 登出/撤权时的许可停摆先清（用户主动暂停的存档不受影响）
        _downloadQueue.Start();
    }

    /// <summary>壳底状态栏中列：今日发布 x/y（队列页供数 —— 老版同款，统计一变就刷）</summary>
    [ObservableProperty] private string _todayPublishText = "";

    /// <summary>侧栏「上传队列」徽章 = 未完成行数（0 就不显示）</summary>
    private void ApplyQueueBadge()
    {
        // 今日统计与徽章同源（RefreshStats 一次推两样）——放在 early-return 之前，
        // 导航还没建好时也不漏刷（RebuildNav 末尾会再调一次本方法）
        TodayPublishText = _queue.TodayPublishText;
        // 队列的第一次统计可能早于导航建好（计时器在池线程上跑）—— 那时没有项可刷
        if (_queueItem is null) return;
        var n = _queue.UnfinishedCount;
        _queueItem.BadgeText = n > 0 ? n.ToString() : null;
    }

    /// <summary>
    /// 侧栏「异步任务」徽章 = 未完成任务数 + 失败条数（0 就不显示）。
    ///
    /// 失败也算进去 —— 只算「在跑」的话，全跑完但有条失败时徽章会消失，
    /// 用户以为没事了，其实平台上还有东西没删掉。
    /// </summary>
    private void ApplyTaskBadge()
    {
        if (_tasksItem is null) return;
        var n = _deleteTasks.PendingBadge;
        _tasksItem.BadgeText = n > 0 ? n.ToString() : null;
    }

    /// <summary>侧栏「合成队列」徽章 = 未落地条数（待合成+合成中+失败+中断；0 就不显示）</summary>
    private void ApplyComposeBadge()
    {
        if (_composeQueueItem is null) return;
        var n = _composeQueue.BadgeCount;
        _composeQueueItem.BadgeText = n > 0 ? n.ToString() : null;
    }

    /// <summary>侧栏「配音队列」徽章 = 未落地条数（待合成+合成中+失败+中断；0 就不显示）</summary>
    private void ApplyTtsQueueBadge()
    {
        if (_ttsQueueItem is null) return;
        var n = _ttsQueue.BadgeCount;
        _ttsQueueItem.BadgeText = n > 0 ? n.ToString() : null;
    }

    /// <summary>侧栏「视频下载」徽章 = 未落地条数（待下载+排队+下载中+失败/中断；0 就不显示）</summary>
    private void ApplyDownloadBadge()
    {
        if (_downloadItem is null) return;
        var n = _downloadQueue.BadgeCount;
        _downloadItem.BadgeText = n > 0 ? n.ToString() : null;
    }


    /// <summary>会话上下文（只读转发给 XAML）</summary>
    public SessionContext Session { get; }

    /// <summary>账号标签坞（App 级单例;壳 XAML 直绑 Dock.*）</summary>
    public AccountDockViewModel Dock { get; }

    public string Username => Session.Username;
    public string Initial => Session.Initial;
    public bool IsExpired => Session.IsExpired;
    public string ExpireText => Session.ExpireText;
    public string AppVersion => Session.AppVersion;

    /// <summary>单机版：没有登录，侧栏「退出登录」整块隐藏</summary>
    public bool ShowLogout => !AppConfig.IsStandalone;

    /// <summary>
    /// 单机版：无账号体系 —— 侧栏底部用户卡与状态栏用户名在 XAML 里整块隐藏
    /// （2026-10-28 用户要求首页不再出现「本地用户」）。
    /// </summary>
    public bool IsStandalone => AppConfig.IsStandalone;

    /// <summary>状态栏右侧文案：单机版标明版本身份，不显示「会员状态未知」</summary>
    public string StatusRightText => AppConfig.IsStandalone ? "单机版 · 本地数据" : Session.ExpireText;

    // ═══ 侧栏折叠（realworld 式：展开 = 图标+文字；收起 = 仅图标） ═══

    /// <summary>true = 展开（图标+文字）</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>收起态（= !IsExpanded，导航行内收起行显隐用 —— 正属性避免 XAML 取反写法）</summary>
    public bool IsCollapsed => !IsExpanded;

    /// <summary>侧栏宽度（展开 208 / 收起 64，XAML Border.Width 直绑）</summary>
    [ObservableProperty]
    private double _navWidth = 208;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(IsCollapsed));

    [RelayCommand]
    private void ToggleNav()
    {
        IsExpanded = !IsExpanded;
        NavWidth = IsExpanded ? 208 : 64;
    }

    // ═══ 导航分组（数据驱动，XAML ItemsControl 直绑） ═══

    public IReadOnlyList<NavSection> Sections
    {
        get => _sections;
        private set => SetProperty(ref _sections, value);
    }

    /// <summary>当前选中导航项（XAML 点击经 NavList_SelectionChanged → Navigate；各组 ListBox 单向绑定做高亮）</summary>
    [ObservableProperty]
    private NavItem? _selectedNav;

    /// <summary>导航到指定项（点击导航入口；MemberChanged 自动跳转/初始页同走此路径）</summary>
    public void Navigate(NavItem item)
    {
        if (item.PageType == null) return; // 占位/空项不导航
        // 账号坞已嵌「平台账号」页（页内标签条+内容），跨页可见性由壳叠层管理；
        // 离开平台页不清活动标签 —— 切回仍停留在刚才浏览的账号
        if (!_pageCache.TryGetValue(item.PageType, out var vm))
        {
            vm = _pageResolver(item.PageType);
            _pageCache[item.PageType] = vm;
        }
        // 首页卡片要跳页，但页 VM 不认识壳 —— 按仓库既有范式（OnStatsChanged / OnLogoutRequested）
        // 由壳把导航能力交给它。**接在这里而不是构造函数**：过期会员默认落激活页，那一次
        // 根本不会实例化首页 VM（见构造函数末行），只在构造函数里接会漏掉这批人。
        if (vm is HomePageViewModel home) home.OnNavigateRequested ??= NavigateToKey;
        // 发布页 / 队列页的「账号窗口未打开」提示条要跳「平台账号」页，同样注入
        if (vm is PublishViewModel pub) pub.OnNavigateRequested ??= NavigateToKey;
        if (vm is UploadQueueViewModel uq) uq.OnNavigateRequested ??= NavigateToKey;
        // 合成页状态区的「查看合成队列」入口同理
        if (vm is ComposeViewModel compose) compose.OnNavigateRequested ??= NavigateToKey;
        // 合成队列是**单例**且可能在壳重建后仍活着 → 无条件赋值（不用 ??=：
        // 旧壳的委托留着的话，自动发布结果的「去上传队列」会推到已关闭的旧壳上）
        if (vm is ComposeQueueViewModel composeQueue) composeQueue.OnNavigateRequested = NavigateToKey;
        // 配音页批量区的「查看配音队列」入口同理
        if (vm is TtsViewModel tts) tts.OnNavigateRequested ??= NavigateToKey;
        // 合集卡「删除未完成」警示条的「去处理」→ 异步任务页
        if (vm is CollectionViewModel coll) coll.OnNavigateRequested ??= NavigateToKey;
        SelectedNav = item;
        CurrentPage = vm;
        (vm as INavigationAware)?.OnNavigatedTo(); // 进入即刷新（设备/公告/钉钉等）
    }

    /// <summary>当前路由页面（ViewLocator 按 VM 类型解析 View）</summary>
    [ObservableProperty]
    private ViewModelBase? _currentPage;

    /// <summary>页面静默实例化完成（壳据此在保活叠层里建好隐藏宿主，不切可见性）</summary>
    public Action<ViewModelBase>? OnPagePreloaded { get; set; }

    /// <summary>
    /// 静默实例化某导航页：建 VM（进页面缓存）+ 让壳在保活叠层里建好隐藏控件树，
    /// **不切当前页、不显示** —— 供后台功能借用页内控件（如小红书签名要坞内 WebView 宿主）。
    /// 已实例化则直接返回 true。页面随后被真正导航到时复用同一 VM/控件树（不会二次加载）。
    /// </summary>
    public bool PreloadPageSilently(string key)
    {
        var item = Sections.SelectMany(s => s.Items)
            .FirstOrDefault(i => i.Key == key && i.PageType != null);
        if (item == null) return false;
        if (_pageCache.ContainsKey(item.PageType!)) return true;

        var vm = _pageResolver(item.PageType!);
        _pageCache[item.PageType!] = vm;
        (vm as INavigationAware)?.OnNavigatedTo();
        OnPagePreloaded?.Invoke(vm);
        return true;
    }

    /// <summary>
    /// 按 key 切页（首页入口卡用）—— key 与 <see cref="RebuildNav"/> 里 <c>Make(key, …)</c> 一致：
    /// publish / queue / tasks / video / …
    /// 与 <see cref="PreloadPageSilently"/> 是同一次「key → NavItem」查找，区别只在这一个真的切过去。
    /// 注意本方法**不看 IsEnabled**（<see cref="Navigate"/> 只看 PageType）：过期会员的入口卡
    /// 由页面自己按 Session.IsExpired 置灰，与侧栏灰显语义一致。
    /// </summary>
    public void NavigateToKey(string key)
    {
        var item = Sections.SelectMany(s => s.Items)
            .FirstOrDefault(i => i.Key == key && i.PageType != null);
        if (item != null) Navigate(item);
    }

    /// <summary>登出请求（App 层负责清会话 + 切回登录窗）</summary>
    public Action? OnLogoutRequested { get; set; }

    [RelayCommand]
    private void Logout() => OnLogoutRequested?.Invoke();

    // ═══ 会员变更 → 重建导航门控 ═══

    private void OnMembershipChanged(bool wasExpired)
    {
        RebuildNav();
        OnPropertyChanged(nameof(IsExpired));
        OnPropertyChanged(nameof(ExpireText));
        // 过期 → 激活成功：补启动后台执行器（过期登录时按门控没起）+ 自动回首页（老 App 同款行为）
        if (wasExpired && !Session.IsExpired)
        {
            StartConsumers();
            Navigate(_homeItem);
        }
    }

    /// <summary>按当前会员状态重建整组导航（IsEnabled = 已解锁真页；禁用项占位灰显）</summary>
    private void RebuildNav()
    {
        bool unlocked(bool allowedWhenExpired) =>
            !Session.IsExpired || allowedWhenExpired;

        NavItem Make(string key, string title, MaterialIconKind icon, Type? page = null,
            string? note = null, bool allowedWhenExpired = false) => new()
        {
            Key = key,
            Title = title,
            Icon = icon,
            Note = note,
            AllowedWhenExpired = allowedWhenExpired,
            // 真页按会员状态门控（page == null 的占位项恒灰显 —— 导航里已无占位项，
            // 这个分支留给将来的「还没做的页」，上一批占位项在合成页上线的同批清掉）
            IsEnabled = page != null && unlocked(allowedWhenExpired),
            PageType = page
        };

        var home = Make("home", "首页", MaterialIconKind.Home, typeof(HomePageViewModel));
        // 单机版没有登录/激活：激活入口整项不存在（不是灰显 —— 它没有可解释的含义）
        var activate = AppConfig.IsStandalone
            ? null
            : Make("activate", "激活会员", MaterialIconKind.Voucher,
                typeof(ActivationViewModel), allowedWhenExpired: true);

        _homeItem = home;
        _activationItem = activate;

        // 管理组：公告 / 钉钉 / 设备是"服务端运营 + 会员体系"专属，单机版移除
        var manageItems = new List<NavItem>
        {
            Make("platform", "平台账号", MaterialIconKind.AccountGroup, typeof(PlatformViewModel)),
            Make("collection", "合集", MaterialIconKind.PlaylistMusic, typeof(CollectionViewModel)),
            Make("video", "视频管理", MaterialIconKind.FolderVideo, typeof(VideoManageViewModel)),
        };
        if (!AppConfig.IsStandalone)
        {
            manageItems.Add(Make("announce", "公告", MaterialIconKind.Bullhorn, typeof(AnnouncementViewModel)));
            manageItems.Add(Make("dingtalk", "钉钉通知", MaterialIconKind.Bell, typeof(DingTalkConfigViewModel)));
            // 设备管理原按老版语义对过期用户放行（可踢设备），2026-10-11 用户规则
            //「凡是功能页面，都必须登录且激活才能使用」—— 收进激活门
            manageItems.Add(Make("device", "设备管理", MaterialIconKind.Devices, typeof(DeviceViewModel)));
        }

        var accountItems = new List<NavItem>();
        if (activate is not null)
            accountItems.Add(activate);
        // 单机版没有账号：「账号设置」改叫「本机设置」（页面标题同步，见 AccountViewModel.HeaderTitle）
        accountItems.Add(Make("account", AppConfig.IsStandalone ? "本机设置" : "账号设置",
            MaterialIconKind.AccountCircle,
            typeof(AccountViewModel), allowedWhenExpired: true)); // 老 App 语义：过期用户可看本页
        // 联系我们（2026-10-22）：独立页，内容来自服务端公开配置（HTML）；
        // 单机版保留页面，内容走本地 appsettings 兜底（不请求服务端）。
        accountItems.Add(Make("contact", "联系我们", MaterialIconKind.Headset,
            typeof(ContactViewModel), allowedWhenExpired: true));

        Sections =
        [
            new NavSection { Title = "概览", Items = [home] },
            new NavSection
            {
                Title = "发布",
                Items =
                [
                    Make("publish", "发布视频", MaterialIconKind.Movie, typeof(PublishViewModel)),
                    Make("queue", "上传队列", MaterialIconKind.CloudUpload,
                        typeof(UploadQueueViewModel)),
                    // 异步任务**独立一页**（与上传队列并列，不是它的一个视图）：
                    // 上传队列是「发出去」，这里是「撤回来」—— 两件事、两套队列、两个页面
                    Make("tasks", "异步任务", MaterialIconKind.ClipboardTextOutline,
                        typeof(AsyncTaskViewModel)),
                ]
            },
            new NavSection
            {
                Title = "创作",
                Items =
                [
                    // 配音页 2026-10-10 按用户决策恢复：负责「生成 mp3 文件」（合成页「本地音频」的来源），
                    // 合成页内联配音控件继续负责「边配边合成」—— 两页共用一份 voice 偏好
                    Make("tts", "配音", MaterialIconKind.Microphone, typeof(TtsViewModel)),
                    // 配音队列 = 配音页批量提交的并行执行器（本机任务，与服务端无关）
                    Make("ttsqueue", "配音队列", MaterialIconKind.MusicNote, typeof(TtsQueueViewModel)),
                    Make("compose", "合成视频", MaterialIconKind.VideoOutline, typeof(ComposeViewModel)),
                    // 合成队列 = 本机任务的串行执行器（单条合成/文件夹批量都入这里），与服务端无关
                    Make("composequeue", "合成队列", MaterialIconKind.TrayFull, typeof(ComposeQueueViewModel)),
                    // 视频下载 = 发布/合成的对偶能力（素材进来），本机队列 + 会员级存档
                    Make("download", "视频下载", MaterialIconKind.Download, typeof(DownloadQueueViewModel)),
                    Make("ai", "AI 设置", MaterialIconKind.AutoAwesome, typeof(AiSettingsViewModel)),
                ]
            },
            new NavSection { Title = "管理", Items = manageItems },
            new NavSection { Title = "账户", Items = accountItems },
        ];

        // 导航项是每次重建时新建的 —— 徽章要跟着新项补一次，否则会员状态一变就没了
        _queueItem = Sections.SelectMany(s => s.Items).First(i => i.Key == "queue");
        ApplyQueueBadge();
        _tasksItem = Sections.SelectMany(s => s.Items).First(i => i.Key == "tasks");
        ApplyTaskBadge();
        _composeQueueItem = Sections.SelectMany(s => s.Items).First(i => i.Key == "composequeue");
        ApplyComposeBadge();
        _ttsQueueItem = Sections.SelectMany(s => s.Items).First(i => i.Key == "ttsqueue");
        ApplyTtsQueueBadge();
        _downloadItem = Sections.SelectMany(s => s.Items).First(i => i.Key == "download");
        ApplyDownloadBadge();
    }
}
