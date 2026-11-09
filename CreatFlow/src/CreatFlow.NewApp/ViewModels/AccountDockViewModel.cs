using CreatFlow.NewApp.Models;
using CreatFlow.NewApp.Services.AccountLogin;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 账号标签坞(App 级 DI 单例) —— 每账号一条常驻 WebView2 标签(独立档案+活会话),
/// 替代「登录窗用后即关」。规划文档 §5.2 形态 A:标签条常驻壳内容区顶部,
/// 内容区在导航页与账号 WebView 间互切(点侧栏=回页面,点标签=切账号)。
///
/// 归属:App 级单例(壳/平台页/坞视图三方共享);但**不持会员态数据** ——
/// 档案目录一律经 storeFactory 现解析(指向当前 SessionContext.MemberId),
/// 登出/换会员后壳重建时 CloseAllTabs(),单例坞空集合复用不串号。
///
/// 登录双通道(用户拍板:自动为主 + 手动兜底):
///   自动 —— 宿主 extractor 检测到登录态请求 → 1.5s 去抖 → ExtractAsync → sync → 自动命名;
///   手动 —— 标签工具条「完成登录」按钮(常驻可见,仅提取中置灰;失败可反复重试,
///   已同步后在浏览态点击 = 重新武装重载登录页再抓一次)同管线。
///
/// 打开即武装验证(已绑定账号):新号/重登以外,已绑定账号本运行首次打开同样武装 —— 档案会话
///   有效 → 自动抓取刷新(工具条「已同步,会话正常」);档案空/会话失效 → 落扫码页,扫完自动
///   抓取,漏了有「完成登录」兜底 —— 不再需要先点「重新登录」。_verifiedSessions 记忆去重:
///   每会员每运行每账号最多自动验证一次,Ready 后重开纯浏览不反复同步。
///
/// 跨组件纪律:仅持 SessionId(Guid)主键 + 原语,不持 PlatformSession 对象引用。
/// </summary>
public sealed partial class AccountDockViewModel : ObservableObject
{
    private readonly Func<PlatformAccountStore> _storeFactory;

    /// <summary>提取阶段超时:WebView2 脚本调用挂起时兜底 —— 否则 Phase 钉死在 Extracting,
    /// 「完成登录」永久置灰,用户无从重试</summary>
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromSeconds(20);

    public AccountDockViewModel(Func<PlatformAccountStore> storeFactory, AccountOrderStore orderStore)
    {
        _storeFactory = storeFactory;
        _orderStore = orderStore;
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
    }

    /// <summary>账号显示顺序（与发布页排序页共用同一份**会员级**存档，DI 单例注入，见 <see cref="AccountOrder"/>）</summary>
    private readonly AccountOrderStore _orderStore;

    public ObservableCollection<AccountDockTabViewModel> Tabs { get; } = [];

    /// <summary>签名宿主热启串行闸（进程内全局；见 EnsureSignHostReadyAsync 注释）</summary>
    private static readonly SemaphoreSlim SignHostGate = new(1, 1);

    /// <summary>本运行已同步验证过的会话(打开即武装验证的去重 —— 每会员每运行每账号最多自动验证一次;
    /// CloseAllTabs 清空,换会员/登出不跨号复用)</summary>
    private readonly HashSet<Guid> _verifiedSessions = [];

    /// <summary>本运行打开过窗口的会话（首导航那一刻记上；CloseAllTabs 清空）。
    /// 发布页/队列页的「建议先点开账号窗口」提示**以它为准** ——</summary>
    private readonly HashSet<Guid> _openedSessions = [];

    /// <summary>
    /// 该会话**本次运行打开过窗口**（WebView 首次导航即记，不等重抓凭证的结果）。
    ///
    /// 为什么提示不用「已同步验证」（_verifiedSessions）当依据：重抓凭证要页面流量、
    /// 还要跨过「还在登录页」的安全闸（视频号入口先落 /login.html，几秒后才跳
    /// /platform/home）—— 拿验证完成当依据时，用户明明打开了窗口、状态却停在「未完成」，
    /// 提示挂在脸上不走（2026-10-09 用户实测「已经登录了还提示」）。提示是**建议**不是门，
    /// 打开过就该消失。
    /// </summary>
    public bool IsSessionOpened(Guid sessionId) => _openedSessions.Contains(sessionId);

    /// <summary>宿主在标签首次导航时调用（窗口真正开起来的那一刻）</summary>
    public void MarkSessionOpened(Guid sessionId) => _openedSessions.Add(sessionId);

    /// <summary>
    /// 哪些平台会参与「建议先点开账号窗口」提示：**四个发布平台 + B站 / 头条**。
    /// 视频号 2026-10-09 纳入 —— 它的凭证同样会过期，开窗口重抓是唯一的保鲜路径
    /// （此前「视频号不需要」只对**发布链路本身**成立：deviceId 登录期即抓、发布不依赖页面流量，
    /// 见 LoginPreheat 注释）。B站/头条虽然还没接发布，但账号卡与凭证刷新走同一条页面链路，
    /// 会话过期时同样只能开窗口重扫。
    ///
    /// 注意:提示**不代用户打开窗口** —— 各平台都有扫码/风控,窗口必须用户自己在场开。
    /// </summary>
    public static bool NeedsAccountWindow(string platformKey) =>
        platformKey is PlatformNames.Douyin or PlatformNames.Kuaishou
            or PlatformNames.Xiaohongshu or PlatformNames.WechatVideo
            or PlatformNames.Bilibili or PlatformNames.Toutiao;

    [ObservableProperty]
    private AccountDockTabViewModel? _activeTab;

    partial void OnActiveTabChanged(AccountDockTabViewModel? value) => OnPropertyChanged(nameof(ShowTabHost));

    /// <summary>标签条可见性(有标签才显示,常驻壳内容区顶部)</summary>
    public bool HasTabs => Tabs.Count > 0;

    /// <summary>某会话登录收尾同步成功(Dock.CompleteLoginAsync 成功尾部触发)。参数:会话 ID。
    /// 平台页订阅后静默重拉账号卡,让坞内同步即时反映到列表。</summary>
    public event Action<Guid>? SessionSynced;

    /// <summary>内容区是否处于账号 WebView 模式(标签宿主叠层盖住导航页)</summary>
    public bool ShowTabHost => ActiveTab != null;

    public AccountDockTabViewModel? Find(Guid sessionId) =>
        Tabs.FirstOrDefault(t => t.SessionId == sessionId);

    // ═══ 「+ 添加账号」平台选择(壳内标签条尾部;平台配置懒加载缓存) ═══

    [ObservableProperty]
    private bool _isPlatformMenuOpen;

    public ObservableCollection<PlatformConfigItem> PlatformChoices { get; } = [];

    public bool PlatformChoicesLoaded { get; private set; }

    /// <summary>首开「+」时拉一次平台配置(失败可重试);成功后由调用方弹菜单</summary>
    public async Task<bool> EnsurePlatformChoicesAsync()
    {
        if (PlatformChoicesLoaded) return PlatformChoices.Count > 0;
        try
        {
            var configs = await ApiClient.GetAsync<List<PlatformConfigItem>>("/api/config/platforms");
            if (configs != null)
            {
                PlatformChoices.Clear();
                foreach (var c in configs) PlatformChoices.Add(c);
                PlatformChoicesLoaded = true;
                return PlatformChoices.Count > 0;
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Load platform configs for dock failed"); }
        return false;
    }

    /// <summary>激活标签(点标签条;全部去活后置当前为活)</summary>
    public void Activate(AccountDockTabViewModel tab)
    {
        foreach (var t in Tabs) t.IsActive = t == tab;
        ActiveTab = tab;
    }

    /// <summary>回导航页(点侧栏时由 ShellViewModel.Navigate 调用):清激活 → 宿主叠层隐藏 → 页面露出</summary>
    public void LeaveToPage()
    {
        foreach (var t in Tabs) t.IsActive = false;
        ActiveTab = null;
    }

    // ═══ 签名宿主热启(小红书 x-s 签名:坞内标签页 = 签名模块载体) ═══

    /// <summary>
    /// 确保某会话的标签「WebView 已建 + 签名器已注册」——WebView 是懒加载的(首激活才建),
    /// 而 x-s 签名要求页面里跑着 mnsv2,故签名前需把宿主热起来:
    ///   已有宿主 → 直接返回;没有 → 激活该标签(宿主随之建 WebView 并导航),轮询等宿主注册,
    ///   随后把坞退回调用前视图(用户原本在看哪个标签/哪页,签完还回哪)。
    /// 只做「热宿主」,不保证已登录 —— 会话失效时页面会落登录页,签名自然失败(上层回报)。
    /// </summary>
    public async Task<bool> EnsureSignHostReadyAsync(
        Guid sessionId, TimeSpan timeout, CancellationToken ct)
    {
        var tab = Find(sessionId);
        if (tab == null)
        {
            Log.Warning("[SignHost] 会话 {Session} 不在坞标签里 —— 无法热宿主", sessionId);
            return false;
        }
        if (tab.SignHostReady) return true;

        // 坞同一时刻只有一个活动标签：两个账号并行热宿主会互相把对方刚建的 WebView 顶成非激活
        //（实测后激活那个停在 about:blank）—— 排队逐个来，每个账号各自等自己的页面加载完。
        await SignHostGate.WaitAsync(ct);
        try
        {
            if (tab.SignHostReady) return true;
            Log.Debug("[SignHost] 激活标签热宿主：{Session}（{Platform}）", sessionId, tab.PlatformKey);

            var previous = ActiveTab;
            var restoreNeeded = !ReferenceEquals(previous, tab);
            Activate(tab); // 首次激活 → 宿主建 WebView 并导航(懒加载挂点)

            var deadline = DateTime.UtcNow + timeout;
            var ready = false;
            while (DateTime.UtcNow < deadline)
            {
                if (ct.IsCancellationRequested) break;
                if (tab.SignHostReady) { ready = true; break; }
                await Task.Delay(250, ct);
            }

            if (restoreNeeded)
            {
                if (previous != null && Tabs.Contains(previous)) Activate(previous);
                else LeaveToPage();
            }
            Log.Debug("[SignHost] 热宿主结果：{Result}（会话 {Session}）", ready ? "就绪" : "超时", sessionId);
            return ready;
        }
        finally { SignHostGate.Release(); }
    }

    // ═══ 开标签(已绑定账号「打开」) ═══

    /// <summary>
    /// 打开/切到某已绑定账号的标签。已有标签 → 激活;forceLogin=true 时武装并重载登录页
    /// (重新登录语义);没有 → 建标签懒加载(首显才建 WebView,登录态随档案恢复)。
    /// </summary>
    public void OpenOrActivate(
        Guid sessionId, string platformKey, string platformDisplayName,
        string brandColorHex, string loginUrl, string? initialTitle,
        bool isPreCreated = false, bool forceLogin = false)
    {
        var existing = Find(sessionId);
        if (existing != null)
        {
            if (forceLogin)
                existing.RequestRelogin(); // 宿主监听:重载登录页 + 重新武装自动检测
            Activate(existing);
            return;
        }

        var profileDir = _storeFactory().ProfileDir(sessionId); // 当前会员目录(换会员后自动指向新目录)
        var tab = new AccountDockTabViewModel(
            sessionId, platformKey, platformDisplayName, brandColorHex,
            loginUrl, profileDir, initialTitle, isPreCreated)
        {
            Dock = this,
            // 武装:新号/重登必须;已绑定账号「打开」若本运行未验证过也武装(打开即武装验证 ——
            // 会话有效 → 自动抓取刷新 → Ready;无效 → 扫码页 +「完成登录」兜底,无需先点「重新登录」)
            ArmAutoDetect = isPreCreated || forceLogin || !_verifiedSessions.Contains(sessionId),
        };
        Tabs.Add(tab);
        Activate(tab);
    }

    // ═══ 添加新账号(新号:本地生成 ID → 坞内登录 → 同步时才建档) ═══

    /// <summary>
    /// 添加新账号:本地先生成会话 ID(「档案目录名 = 会话 ID」,WebView2 环境创建早于
    /// 登录完成,目录名必须先定;登录成功同步时服务端用同 ID 建档)→ 开武装标签等待扫码。
    /// 不做服务端预注册 —— 会话行只在完成登录后才落库,取消/中途放弃不留脏数据。
    /// </summary>
    public async Task<string?> AddNewAccountAsync(
        string platformKey, string platformDisplayName, string brandColorHex, string loginUrl)
    {
        // 「开任何标签前补删」:先清掉解绑/回滚残留的 pending-delete 目录再建新档案。
        // 必须 await(不能后台放飞)—— 否则孤儿清扫可能与本会话的新档案目录竞态,
        // 半登录新档案尚未入本地镜像,会被当成孤儿误删(历史白屏根因同类)。
        // 无残留时只是扫一次 marker 列表,毫秒级。
        await Task.Run(() => AccountProfileCleanup.RunPendingCleanup(_storeFactory().MemberId));

        OpenOrActivate(Guid.NewGuid(), platformKey, platformDisplayName, brandColorHex,
            loginUrl, initialTitle: null, isPreCreated: true);
        return null;
    }

    // ═══ 登录收尾(自动/手动双通道共走;由宿主宿主视图触发) ═══

    /// <summary>
    /// 登录完成后提取 → sync 服务端 → 本地镜像 → 标签命名。
    /// 宿主在「登录检测(自动,1.5s 去抖)」或「完成登录(手动)」时调用;单飞由 Phase 防重。
    /// </summary>
    public async Task CompleteLoginAsync(AccountDockTabViewModel tab, AccountLoginExtractor extractor)
    {
        if (tab.Phase is DockOpenPhase.Extracting or DockOpenPhase.Ready) return; // 防重入

        tab.Phase = DockOpenPhase.Extracting;
        tab.StatusText = "检测到登录，正在提取凭证…";

        var extractTask = extractor.ExtractAsync();
        if (await Task.WhenAny(extractTask, Task.Delay(ExtractTimeout)) != extractTask)
        {
            // 挂起任务稍后仍会落地:吞掉可能的异常(防 UnobservedTaskException 误报崩溃),结果丢弃
            _ = extractTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            tab.Phase = DockOpenPhase.Failed;
            tab.StatusText = "提取超时，请点「完成登录」重试";
            return;
        }

        var result = await extractTask;
        if (result is not { Success: true })
        {
            // 未登录/尚未扫码：停在「等登录」相位（可重试），不是失败
            var notReady = result?.Error is AccountLoginExtractor.ErrorNoCookie
                or AccountLoginExtractor.ErrorNotLoggedIn;
            tab.Phase = notReady ? DockOpenPhase.AwaitLogin : DockOpenPhase.Failed;
            tab.StatusText = result?.Error switch
            {
                AccountLoginExtractor.ErrorNoCookie => "检测到登录但凭证未就绪，请点击「完成登录」重试",
                AccountLoginExtractor.ErrorNotLoggedIn => "当前还在登录页：请先在页面扫码登录，再点「完成登录」",
                _ => $"提取异常：{result?.Error ?? "未知原因"}，请重试",
            };
            return;
        }

        tab.StatusText = "正在同步到服务端…";
        try
        {
            var sync = await PlatformSessionApi.SyncCredentialsAsync(tab.SessionId, tab.PlatformKey, result);
            if (sync == null)
            {
                tab.Phase = DockOpenPhase.Failed;
                tab.StatusText = "同步失败：服务端未返回数据";
                return;
            }

            // 本地注册表镜像(会话 → 档案目录一一对应;换会员目录隔离由 storeFactory 保证)
            _storeFactory().Register(sync.Id, tab.PlatformKey,
                result.PlatformUid, result.PlatformName, accountLabel: sync.AccountLabel);

            tab.Title = ResolveTitle(sync.AccountLabel, sync.PlatformName, tab.PlatformDisplayName);
            tab.IsPreCreated = false; // 已真实登录,不再是空壳(此后关闭=纯释放)
            _verifiedSessions.Add(tab.SessionId); // 打开即武装验证:本运行不再重复自动验证
            tab.Phase = DockOpenPhase.Ready;
            tab.StatusText = sync.Status == 0
                ? "已同步，会话正常"
                : $"已同步（{sync.StatusText}）";
            tab.ArmAutoDetect = false; // 登录态恢复为浏览模式
            SessionSynced?.Invoke(tab.SessionId); // 平台页监听 → 账号卡静默刷新到最新
        }
        catch (Exception ex)
        {
            tab.Phase = DockOpenPhase.Failed;
            tab.StatusText = $"同步失败：{ex.Message}";
        }
    }

    private static string ResolveTitle(string? accountLabel, string? platformName, string platformDisplayName) =>
        !string.IsNullOrWhiteSpace(accountLabel) ? accountLabel!
        : !string.IsNullOrWhiteSpace(platformName) ? platformName!
        : $"{platformDisplayName} · 未命名";

    // ═══ 已绑定账号自动吸附（Chrome 心智：一个账号 = 一条标签） ═══

    /// <summary>
    /// 启动/登录后把服务端已绑定账号全部吸附为标签（不激活、不建 WebView —— 首激活才懒加载导航）。
    /// 幂等：已在坞中的跳过；账号 × 关闭的标签尊重用户选择不重新吸附（可在平台页「打开」恢复）。
    /// </summary>
    public async Task EnsureBoundSessionsAsync()
    {
        try
        {
            var configs = await ApiClient.GetAsync<List<PlatformConfigItem>>("/api/config/platforms");
            if (configs is not { Count: > 0 }) return;

            PlatformSessionListResponse? resp = null;
            try
            {
                resp = await ApiClient.PostAsync<PlatformSessionListResponse>("/api/platform-sessions/list");
            }
            catch (ApiException ex) when (ex.StatusCode == 401) { /* 会话失效由壳统一处理 */ }

            if (resp?.Sessions == null || resp.Sessions.Count == 0) return;
            var configByKey = configs.ToDictionary(c => c.PlatformKey);

            // 顺序与发布页同源（默认序 + 用户自定义序）—— 不共用就会出现
            // 「在排序页排好了，账号坞标签条却还是乱的」。
            // 只在**吸附那一刻**应用：已经开着的标签不重排（用户正看着标签条突然重排是很糟的体验），
            // 用户手动打开的标签仍追加在末尾。
            var order = await _orderStore.LoadAsync();

            foreach (var s in AccountOrder.Apply(AccountOrder.Default(resp.Sessions, configs), order))
            {
                if (!configByKey.TryGetValue(s.PlatformKey, out var cfg)) continue;
                EnsureTabSilent(s.Id, s.PlatformKey, cfg.DisplayName, cfg.BrandColorHex,
                    cfg.LoginUrl, ResolveTitle(s.AccountLabel, s.PlatformName, cfg.DisplayName));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ensure bound dock tabs failed"); // 失败静默：平台页「打开」/下次启动重试兜底
        }
    }

    /// <summary>吸附单条：已存在跳过；新标签常驻标签条但不激活不导航（懒加载在首激活时）</summary>
    private void EnsureTabSilent(
        Guid sessionId, string platformKey, string platformDisplayName,
        string brandColorHex, string loginUrl, string? initialTitle)
    {
        if (Find(sessionId) != null) return;

        var profileDir = _storeFactory().ProfileDir(sessionId);
        var tab = new AccountDockTabViewModel(
            sessionId, platformKey, platformDisplayName, brandColorHex,
            loginUrl, profileDir, initialTitle, isPreCreated: false)
        {
            Dock = this,
            // 打开即武装验证:本运行未验证过的会话点开即武装自动检测 —— 档案会话有效则自动抓取
            // 刷新(会话信息及时同步);档案空/失效则落扫码页,扫完自动抓取 +「完成登录」按钮兜底。
            // 验证成功入 _verifiedSessions,此后重开纯浏览,不再反复同步。
            ArmAutoDetect = !_verifiedSessions.Contains(sessionId),
        };
        Tabs.Add(tab);
    }

    // ═══ 平台页联动 ═══

    /// <summary>平台页改名后同步打开的标签标题(备注优先;标签未开无需处理)</summary>
    public void ApplyAccountLabel(Guid sessionId, string? label)
    {
        var tab = Find(sessionId);
        if (tab == null || string.IsNullOrWhiteSpace(label)) return;
        tab.Title = label!;
    }

    // ═══ 关标签 / 清空 ═══

    /// <summary>
    /// 关闭标签:Ready 后 = 纯释放(档案保留,重开秒回登录态);
    /// Ready 前且新号未完成 = 取消登录 → 清半登录档案;服务端本无行(同步完成才建档),
    /// 但仍 best-effort 删一次,兜底「服务端已建档而响应丢失」的罕见半途态。
    /// </summary>
    public Task CloseTabAsync(AccountDockTabViewModel tab)
    {
        var idx = Tabs.IndexOf(tab);
        var wasActive = ActiveTab == tab;

        // 取消未完成登录:兜底删可能已建档的服务端行 + 清半登录档案(视图 Teardown 释放 WebView 后目录才可删)
        if (tab.IsPreCreated && tab.Phase != DockOpenPhase.Ready)
        {
            Tabs.Remove(tab);
            _ = RollbackShellAsync(tab);
        }
        else
        {
            Tabs.Remove(tab); // 视图 CollectionChanged → 宿主 Teardown(档案保留)
        }

        if (wasActive)
        {
            AccountDockTabViewModel? next = null;
            if (Tabs.Count > 0)
                next = Tabs[Math.Min(Math.Max(idx, 0), Tabs.Count - 1)];
            if (next != null) Activate(next);
            else { ActiveTab = null; foreach (var t in Tabs) t.IsActive = false; }
        }
        return Task.CompletedTask;
    }

    /// <summary>取消登录清理(服务端无行时该删除是 404 空操作;档案目录清理失败由 marker 兜底)</summary>
    private async Task RollbackShellAsync(AccountDockTabViewModel tab)
    {
        try { await PlatformSessionApi.DeleteSessionAsync(tab.SessionId); }
        catch (Exception ex) { Log.Warning(ex, "Rollback shell session {Session} failed", tab.SessionId); }
        try
        {
            // 等视图 Teardown 释放渲染进程后删目录;删不掉写 marker 下次启动补删
            _ = Task.Run(() =>
            {
                if (!AccountProfileCleanup.TryDeleteProfileDir(tab.ProfileDir))
                    AccountProfileCleanup.WritePendingMarker(tab.ProfileDir, _storeFactory().MemberId);
            });
        }
        catch (Exception ex) { Log.Warning(ex, "Rollback profile cleanup failed"); }
    }

    /// <summary>清空全部标签(壳关闭/登出/换会员时;只释放 WebView 不删档案)</summary>
    public void CloseAllTabs()
    {
        foreach (var t in Tabs) t.IsActive = false;
        Tabs.Clear();
        ActiveTab = null;
        _verifiedSessions.Clear(); // 验证记忆不跨会员/登出
        _openedSessions.Clear();   // 打开记忆同理
    }
}
