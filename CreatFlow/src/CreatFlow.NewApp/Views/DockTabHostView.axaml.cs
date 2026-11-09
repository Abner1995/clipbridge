using System.ComponentModel;
using System.Net;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using CreatFlow.NewApp.Services;
using CreatFlow.NewApp.Services.AccountLogin;
using CreatFlow.NewApp.ViewModels;

namespace CreatFlow.NewApp.Views;

/// <summary>
/// 单账号标签宿主 —— 一个 NativeWebView(独立档案)+ 顶部工具条。
///
/// 生命周期规则(标签坞硬约束):
///   - 宿主由 TabDockView code-behind 按 Tabs 增删,一旦创建即常驻视觉树;
///   - 切标签只切根 Border.IsVisible(IsActive),**不摘树不重建**(摘树 = WebView 控制器销毁/重建);
///   - EnvironmentRequested 注册先于任何 Navigate(档案注入时机,PlatformLoginWindow 同款);
///   - extractor 生命周期随武装态:ArmAutoDetect true(新号登录/重登)→ 建 extractor + 检测自动提取;
///     false(登录完成/普通打开)→ 退订(浏览态不再误触发)。
/// </summary>
public partial class DockTabHostView : UserControl
{
    private const int AutoExtractDebounceMs = 1500; // 容忍 cookie/HttpOnly 落盘时序
    private const int RecaptureNavTimeoutMs = 15000; // 重载导航等待上限(超时兜底:页面卡住也要能收尾)
    private const int RecaptureSettleMs = 2500;      // 导航完成后首屏 API 流量再落一点(凭证捕获靠流量)
    private const int PreheatTokenWaitMs = 12000;    // 预热页等关键 token 到齐上限(老版死等 5s;抓到就走,快手 localStorage 需要页面 SPA 跑起来)
    private const int UserInfoRetryAttempts = 3;     // 回首页补抓账号信息(昵称/头像)的探测次数
    private const int UserInfoRetryDelayMs = 900;    // 账号卡 SPA 渲染需时间,两次探测间隔
    private const int CookieRestoreDays = 30;        // 补注的会话 cookie 留存天数(视频号登录态是会话级 cookie)

    private AccountDockTabViewModel? _tab;
    private AccountLoginExtractor? _extractor;
    private XhsPageSigner? _xhsSigner;           // 小红书 x-s 签名器（本标签档案页；懒建）
    private DouyinPageExecutor? _douyinExecutor; // 抖音网页代理发布（本标签档案页；懒建）
    private KuaishouCookieHost? _ksCookieHost;   // 快手活 Cookie（api_st 会过期；懒建）
    private KuaishouPageSigner? _ksSigner;       // 快手 sig3/sig4 签名器（本标签档案页；懒建）
    private bool _navigatedOnce;
    private bool _pageLoadedOnce;      // 本宿主 WebView 是否已加载过页面
    private bool _hostPending;         // 等首次加载完成后注册页面宿主（小红书签名 / 抖音发布）
    private bool _cookiesRestored;     // 本标签本轮是否已成功补注过会话 cookie
    private bool _cookieRestoreTried;  // 登录页复投是否已尝试过(只试一次,防重载循环)

    /// <summary>待收尾的重载导航(浏览态「完成登录」= 重新抓取时置位;首次导航/普通重登不置位)</summary>
    private TaskCompletionSource? _reloadNavigated;

    /// <summary>导航序号 —— 首次导航要等恢复会话（异步），期间来的引导跳转（「请完成验证」）
    /// 必须让它让位，否则恢复完会把页面又拽回登录页（抖音验证引导的竞态防线）</summary>
    private int _navSeq;

    public DockTabHostView()
    {
        InitializeComponent();

        // 多档案隔离:环境创建前指定本账号专属目录(探针验证 11.4.0 事件可用)
        WebView.EnvironmentRequested += (_, args) =>
        {
            if (args is WindowsWebView2EnvironmentRequestedEventArgs wv && _tab != null)
            {
                wv.UserDataFolder = _tab.ProfileDir;
                wv.ProfileName = "main";
            }
        };
        WebView.NavigationCompleted += (_, _) =>
        {
            _pageLoadedOnce = true;
            _reloadNavigated?.TrySetResult();
            // 页面宿主等「页面已加载」才注册：借用方（后台签名/发布代理）拿到就绪信号后会立刻切页，
            // 页面还没加载完就切走会把首次导航掐死（实测停在 about:blank）
            if (_hostPending)
            {
                _hostPending = false;
                RegisterPageHosts();
            }
            TryLateCookieRestore(); // 导航前没注成（控制器尚未就绪）时的复投
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ReferenceEquals(DataContext, _tab)) return;

        DetachTab();
        _tab = DataContext as AccountDockTabViewModel;
        if (_tab == null) return;

        _tab.PropertyChanged += OnTabPropertyChanged;
        _tab.NavigateRequested += OnTabNavigateRequested;
        ApplyArmState();

        // 懒加载:标签常驻但不建 WebView —— 首次激活(IsActive=true)才导航;
        // 复用宿主时(标签切换等)依武装态重载登录页
        if (_tab.IsActive && !_navigatedOnce)
            NavigateInitial();
        else if (_navigatedOnce && _tab.ArmAutoDetect)
            ReloadLoginPage();
    }

    // ═══ 导航 ═══

    /// <summary>首次导航(懒加载点:首次激活时;之后切激活/隐藏都不再导航,页面状态保留)。
    /// 已绑定账号首次打开同样可能带武装(本运行未验证) —— 此时走自动检测:档案会话有效
    /// → 自动抓取刷新;档案空/失效 → 落扫码页,扫完自动抓取,漏了有「完成登录」兜底。</summary>
    private void NavigateInitial()
    {
        if (_navigatedOnce || _tab == null) return;
        _navigatedOnce = true;

        // 「本运行打开过」提示依据：窗口真正开起来的这一刻记上（不等重抓凭证的结果 —— 见 IsSessionOpened 注释）
        _tab.Dock?.MarkSessionOpened(_tab.SessionId);

        // 档案目录交给 WebView2 创建(删除由解绑流程负责)
        try { Directory.CreateDirectory(_tab.ProfileDir); } catch { /* best effort */ }

        // 引导页（若有）= 首次就落到它，不先走登录页再跳一次（抖音身份验证引导）
        var target = _tab.PendingNavigateUrl;
        _tab.PendingNavigateUrl = null;

        _tab.Phase = _tab.ArmAutoDetect ? DockOpenPhase.AwaitLogin : DockOpenPhase.Ready;
        // 引导页的文案由调用方给（「请在本页完成验证」），别在这里覆盖
        if (target is null)
        {
            _tab.StatusText = _tab.ArmAutoDetect
                ? (_tab.IsPreCreated ? "正在打开登录页…" : "正在检测登录态…")
                : "";
        }
        ArmPageHosts();
        _ = NavigateWithSessionRestoreAsync(target);
    }

    // ═══ 会话恢复（档案页 cookie 被清/过期时，用服务端存的 cookie 把登录态补回来） ═══

    /// <summary>
    /// 导航前先尝试把服务端存的会话 cookie 注回本账号档案 —— 老版「唤醒已有会话」同款目的
    /// （老版走 `document.cookie` + 重载，注不了 HttpOnly；这里走 CookieManager 原生注入，
    /// 能注 HttpOnly，并可带过期时间）。
    ///
    /// 视频号尤其需要：它的登录态是**会话级 cookie**，进程退出/被强杀就丢，表现为「每次打开都
    /// 要重新扫码」；而账号卡（昵称/头像）只长在登录后的助手页上 —— 会话恢复不了就永远抓不到。
    ///
    /// 覆盖策略：**只补档案里没有的 cookie 名**（按名字去重）——档案里已有的（更新的一次登录）
    /// 一律不动，避免拿旧 cookie 把用户刚登录出来的新会话顶掉。
    /// </summary>
    private async Task RestoreSessionCookiesAsync()
    {
        var tab = _tab;
        if (tab == null || tab.IsPreCreated) return; // 空壳新号没有可恢复的会话
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(tab.SessionId);
            if (string.IsNullOrWhiteSpace(creds?.Cookie)) return;
            if (_tab != tab) return;

            var injected = await InjectMissingCookiesAsync(creds!.Cookie!, tab.LoginUrl, tab.PlatformKey);
            if (injected > 0)
            {
                _cookiesRestored = true;
                Log.Debug("[Cookie] {Platform} 会话 cookie 已补注 {Count} 项（档案缺的那些）",
                    tab.PlatformKey, injected);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Cookie] 会话恢复失败（照常打开，用户可扫码）");
        }
    }

    /// <summary>把 cookie 串里「档案里还没有」的项补进 CookieManager；返回补注条数</summary>
    private async Task<int> InjectMissingCookiesAsync(string cookieString, string loginUrl, string platformKey)
    {
        var manager = WebView.TryGetCookieManager();
        if (manager == null) return 0; // 控制器未就绪 —— 交给 NavigationCompleted 复投
        if (!Uri.TryCreate(loginUrl, UriKind.Absolute, out var uri)) return 0;

        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleted = 0;
        try
        {
            foreach (var c in await manager.GetCookiesAsync())
            {
                // ⚠️ 顺手清掉历史遗留的**脏名 cookie**：早期版本的转义污染（`\"sessionid`、`\\\"sessionid`…）
                // 会随每轮同步+注入各加深一层、在档案里累积 —— 浏览器把它们和正常 cookie 一起发给
                // 视频号登录页，一串互相打架的 sessionid 会让它判不出登录态（实测档案里攒了 10 条，
                // 页面卡在扫码页）。名字按 RFC 6265 令牌字符校验：非法名的既跳过、也从档案删掉（2026-10-09）。
                if (!AccountLoginExtractor.IsValidCookieName(c.Name))
                {
                    try { manager.DeleteCookie(c.Name, c.Domain, c.Path); deleted++; }
                    catch { /* 删不掉只影响清理进度，不影响注入 */ }
                    continue;
                }
                have.Add(c.Name);
            }
        }
        catch (Exception ex) { Log.Debug(ex, "[Cookie] 读档案 cookie 失败（按全缺处理）"); }

        if (deleted > 0)
            Log.Warning("[Cookie] {Platform} 档案里清理了 {Count} 条脏名 cookie（转义污染的历史遗留）",
                _tab?.PlatformKey, deleted);

        var count = 0;
        foreach (var part in cookieString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (name.Length == 0 || value.Length == 0) continue;
            if (IsCookieAttribute(name) || have.Contains(name)) continue;
            // 服务端存量字符串里可能也带着脏名（历史同步进去的）—— 别再把它注回档案、把刚清的重新养出来
            if (!AccountLoginExtractor.IsValidCookieName(name)) continue;
            try
            {
                manager.AddOrUpdateCookie(new Cookie(name, value, "/",
                    AccountLoginExtractor.CookieRestoreDomain(platformKey, uri.Host))
                {
                    // 档案里没有 = 上次退出时被丢掉的会话级 cookie —— 给个过期时间让它留下来
                    Expires = DateTime.Now.AddDays(CookieRestoreDays),
                });
                count++;
            }
            catch { /* 单个 cookie 非法（名字/值含特殊字符）不影响其它 */ }
        }
        return count;
    }

    /// <summary>cookie 串里的属性段（老版本导出可能是 Set-Cookie 形态，别当 cookie 注进去）</summary>
    private static bool IsCookieAttribute(string name) =>
        name.Equals("expires", StringComparison.OrdinalIgnoreCase)
        || name.Equals("path", StringComparison.OrdinalIgnoreCase)
        || name.Equals("domain", StringComparison.OrdinalIgnoreCase)
        || name.Equals("max-age", StringComparison.OrdinalIgnoreCase)
        || name.Equals("secure", StringComparison.OrdinalIgnoreCase)
        || name.Equals("httponly", StringComparison.OrdinalIgnoreCase)
        || name.Equals("samesite", StringComparison.OrdinalIgnoreCase);

    /// <summary>导航前注入未成功（控制器尚未就绪）时的复投 —— 只在页面落在登录页时补，
    /// 免得每个已绑定标签都白白重载一遍</summary>
    private void TryLateCookieRestore()
    {
        if (_cookiesRestored || _cookieRestoreTried) return;
        var tab = _tab;
        if (tab == null || tab.IsPreCreated) return;
        var url = WebView.Source?.ToString() ?? "";
        if (!url.Contains("/login", StringComparison.OrdinalIgnoreCase)) return;
        _cookieRestoreTried = true;
        _ = LateCookieRestoreAsync();
    }

    private async Task LateCookieRestoreAsync()
    {
        var tab = _tab;
        if (tab == null) return;
        Log.Debug("[Cookie] {Platform} 档案页落在登录页，补注会话 cookie 后重载一次", tab.PlatformKey);
        await RestoreSessionCookiesAsync();
        if (_cookiesRestored && _tab == tab) WebView.Navigate(new Uri(tab.LoginUrl));
    }

    /// <summary>导航前先补会话（失败不阻断 —— 照常打开登录页扫码）。
    /// <paramref name="targetUrl"/> 非空 = 直接落到该页（引导跳转），否则走账号的平台入口页。</summary>
    private async Task NavigateWithSessionRestoreAsync(string? targetUrl = null)
    {
        var tab = _tab;
        if (tab == null) return;

        var seq = ++_navSeq;
        await RestoreSessionCookiesAsync();
        if (_tab != tab) return;
        if (seq != _navSeq)
        {
            Log.Debug("[Dock] 首次导航期间收到引导跳转 —— 放弃本次导航（{Session}）", tab.SessionId);
            return;
        }
        WebView.Navigate(new Uri(targetUrl ?? tab.LoginUrl));
    }

    /// <summary>
    /// 引导跳转（App 层「抖音要求身份验证」）：把本账号标签切到指定页面。
    /// 还没建过 WebView → 走首次导航（用例：账号从没打开过，发布失败在后台被发现）；
    /// 已建 → 直接切页（借页方 <see cref="DouyinPageExecutor"/> 发完会自己退回原页）。
    /// </summary>
    private void OnTabNavigateRequested(AccountDockTabViewModel tab, string url)
    {
        if (!ReferenceEquals(tab, _tab)) return;

        if (!_navigatedOnce)
        {
            NavigateInitial();   // 消费 PendingNavigateUrl → 直接落到引导页
            return;
        }

        _navSeq++;               // 让「首次导航 + 恢复会话」那次让位（见 NavigateWithSessionRestoreAsync）
        tab.PendingNavigateUrl = null;
        try
        {
            WebView.Navigate(new Uri(url));
            Log.Debug("[Dock] 引导跳转：{Platform} 会话 {Session} → {Url}",
                tab.PlatformKey, tab.SessionId, url);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Dock] 引导跳转失败：{Url}", url);
        }
    }

    // ═══ 页面宿主注册（小红书 x-s 签名 / 抖音网页代理发布 —— 都需要「本账号档案页里的 JS」） ═══

    /// <summary>
    /// 备战页面宿主：页面已加载 → 立即注册；否则等首次 NavigationCompleted。
    /// 「就绪」= 页面已加载 —— 借用方拿到信号会立刻把页面切走（见 App 的 HostWarmer），
    /// 早于加载完成切走会把首次导航掐死（实测停在 about:blank）。
    /// </summary>
    private void ArmPageHosts()
    {
        if (_tab == null || _tab.SignHostReady || !HasPageHost(_tab.PlatformKey)) return;
        if (_pageLoadedOnce) { RegisterPageHosts(); return; }
        _hostPending = true;
    }

    /// <summary>
    /// 该平台有没有「页面宿主」这回事 —— 小红书（签名）、抖音（网页代理发布）、
    /// 快手（活 Cookie：作品回查要新鲜的 <c>api_st</c>，服务端快照会过期）。
    /// </summary>
    private static bool HasPageHost(string platformKey) =>
        platformKey is PlatformNames.Xiaohongshu or PlatformNames.Douyin or PlatformNames.Kuaishou;

    /// <summary>
    /// 按平台把本档案页注册成对应能力的宿主：
    ///   · 小红书 → <see cref="XhsSignerService"/>（x-s 的 x3 = window.mnsv2，纯 C# 无法复算）；
    ///   · 抖音   → <see cref="DouyinWebExecutor"/>（bd_ticket_guard 原生票据只有页面自己有）；
    ///   · 快手   → <see cref="KuaishouWebCookies"/>（作品回查认 Cookie 里的 api_st，
    ///     服务端快照会过期 → 109/500002；档案页的 CookieManager 才是最新的）
    ///     + <see cref="KuaishouSignerService"/>（sig3/sig4 模块只在页面 webpack 里，
    ///     替代已移除的 node.exe + chunk.js 回退）。
    /// 都按 sessionId 注册，SDK/管线据此定位「同账号的档案页」，避免多账号串号。
    /// </summary>
    private void RegisterPageHosts()
    {
        if (_tab == null || _tab.SignHostReady) return;

        switch (_tab.PlatformKey)
        {
            case PlatformNames.Xiaohongshu:
                _xhsSigner ??= new XhsPageSigner(WebView);
                XhsSignerService.Register(_tab.SessionId, _xhsSigner);
                break;
            case PlatformNames.Douyin:
                _douyinExecutor ??= new DouyinPageExecutor(WebView);
                DouyinWebExecutor.Register(_tab.SessionId, _douyinExecutor);
                break;
            case PlatformNames.Kuaishou:
                _ksCookieHost ??= new KuaishouCookieHost(WebView);
                KuaishouWebCookies.Register(_tab.SessionId, _ksCookieHost);
                _ksSigner ??= new KuaishouPageSigner(WebView);
                KuaishouSignerService.Register(_tab.SessionId, _ksSigner);
                break;
            default:
                return; // 其它平台没有页面宿主；SignHostReady 保持 false，不谎报就绪
        }
        _tab.SignHostReady = true;
    }

    private void UnregisterPageHosts()
    {
        if (_tab == null) return;
        XhsSignerService.Unregister(_tab.SessionId);
        DouyinWebExecutor.Unregister(_tab.SessionId);
        KuaishouWebCookies.Unregister(_tab.SessionId);
        KuaishouSignerService.Unregister(_tab.SessionId);
        _tab.SignHostReady = false;
    }

    /// <summary>重登:在已加载档案上重载登录页(会话有效则秒回已登录态,失效则落扫码页)</summary>
    private void ReloadLoginPage()
    {
        if (_tab == null) return;
        _tab.Phase = DockOpenPhase.AwaitLogin;
        _tab.StatusText = "正在重新打开登录页…";
        WebView.Navigate(new Uri(_tab.LoginUrl));
    }

    // ═══ 武装态 ⇄ extractor(新号登录/重登建;登录完成/普通浏览退订) ═══

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_tab == null) return;

        if (e.PropertyName == nameof(AccountDockTabViewModel.IsActive))
        {
            // 切走标签：WebView 只是隐藏（保活），但焦点若留在它身上，窗口激活恢复焦点时
            // 会调用失效控制器的 MoveFocus → E_INVALIDARG 杀进程（2026-11-04 实测）→ 先清焦点
            if (!_tab.IsActive) WebViewFocusGuard.ClearFocusInside(this);

            // 首次激活 → 建 WebView 并导航（懒加载挂点）
            if (_tab.IsActive && !_navigatedOnce)
                NavigateInitial();
            return;
        }

        if (e.PropertyName == nameof(AccountDockTabViewModel.ArmAutoDetect))
        {
            ApplyArmState();
            if (_tab.ArmAutoDetect && _navigatedOnce)
                ReloadLoginPage(); // 重登:重载登录页重新武装
        }
    }

    private void ApplyArmState()
    {
        if (_tab == null) return;
        if (_tab.ArmAutoDetect)
            EnsureExtractor();
        else
            ReleaseExtractor();
    }

    private void EnsureExtractor()
    {
        if (_extractor != null || _tab == null) return;
        _extractor = new AccountLoginExtractor(WebView, _tab.PlatformKey);
        _extractor.LoginDetected += OnLoginDetected;
    }

    private void ReleaseExtractor()
    {
        if (_extractor != null)
        {
            _extractor.LoginDetected -= OnLoginDetected;
            _extractor.Detach();
            _extractor = null;
        }
    }

    // ═══ 登录收尾:自动通道(去抖) + 手动兜底按钮,共走 Dock.CompleteLoginAsync ═══

    private void OnLoginDetected()
    {
        if (_tab == null || !_tab.ArmAutoDetect) return;
        _ = AutoExtractAsync(); // 检测到登录态 → 去抖后自动提取+同步
    }

    private async Task AutoExtractAsync()
    {
        try
        {
            await Task.Delay(AutoExtractDebounceMs);
            var tab = _tab;
            var extractor = _extractor;
            if (tab == null || extractor == null || !tab.ArmAutoDetect
                || tab.Phase is not (DockOpenPhase.AwaitLogin or DockOpenPhase.Failed)) return;

            await PrepareBeforeExtractAsync(extractor);
            if (_tab != tab || !tab.ArmAutoDetect
                || tab.Phase is not (DockOpenPhase.AwaitLogin or DockOpenPhase.Failed)) return;

            await tab.Dock.CompleteLoginAsync(tab, extractor);
            await RetryAfterLeavingLoginPageAsync(tab, extractor);   // 视频号：被登录页拒 → 等跳走自动重试
        }
        catch (Exception ex) { Log.Warning(ex, "Auto extract failed"); }
    }

    /// <summary>
    /// 「被登录页拒绝提取」后的自动重试（仅入口页即登录页的平台 = 视频号）。
    ///
    /// 为什么需要：视频号入口 channels.weixin.qq.com 会先把页面落在 /login.html 做会话检查
    /// （此期间登录页自带的 /micro/content 系请求会把登录检测提前打出去），几秒后页面才自己
    /// 跳 /platform/home —— 第一跳的提取必被安全闸拒（ErrorNotLoggedIn，防覆盖已有凭证），
    /// 而**会话明明还在**。没有这段重试时，用户看到的就是「窗口里已登录、标签状态却停在
    /// 『当前还在登录页』」，发布页/队列页据此一直提示未打开（2026-10-09 用户实测，
    /// 日志 15:35:38 被拒后没有任何第二次尝试）。
    ///
    /// 语义：被拒 ≠ 没登录，是「还没跳走」—— 等页面离开 /login 自动重试；页面真停在登录页
    /// 等扫码时，预算到点即止、不打扰手动通道（扫码后页面一跳走，本循环或重新武装的
    /// 登录检测都会接手）。
    /// </summary>
    private async Task RetryAfterLeavingLoginPageAsync(AccountDockTabViewModel tab, AccountLoginExtractor extractor)
    {
        if (!LoginPreheat.EntryUrlIsLoginPage(tab.PlatformKey)) return;
        if (tab.Phase != DockOpenPhase.AwaitLogin) return;   // 成功(Ready)/失败(Failed)都不归这里管

        extractor.ResetDetection();   // 重新武装：扫码后页面跳动产生的新流量也能再触发自动通道

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // 先等页面离开登录页（单次最多约 15 秒）
            for (var waited = 0; !LeftLoginPage() && waited < 15; waited += 2)
            {
                await Task.Delay(2000);
                if (_tab != tab || !tab.ArmAutoDetect || tab.Phase != DockOpenPhase.AwaitLogin) return;
            }
            if (!LeftLoginPage()) return;   // 真停在登录页（等扫码）——交给手动通道/扫码后的检测

            Log.Debug("[AutoExtract] {Platform} 已离开登录页（{Page}）—— 自动重试提取（第 {N} 次）",
                tab.PlatformKey, WebView.Source, attempt);

            await PrepareBeforeExtractAsync(extractor);
            if (_tab != tab || !tab.ArmAutoDetect || tab.Phase != DockOpenPhase.AwaitLogin) return;
            await tab.Dock.CompleteLoginAsync(tab, extractor);
            if (tab.Phase != DockOpenPhase.AwaitLogin) return;   // Ready=成了 / Failed=交给「完成登录」重试
            await Task.Delay(1500);
        }
    }

    /// <summary>
    /// 当前页已有真实地址且不在 /login —— 才允许重试提取。
    /// 用 Source 而不是页面内 location：它随真实导航更新，且文档不可用（拿不到 location）时
    /// 也不会误判成「已离开」；SPA 内部 replaceState 造成的滞后靠外层轮询兜掉。
    /// </summary>
    private bool LeftLoginPage() =>
        WebView.Source?.ToString() is { Length: > 0 } url
        && !url.Contains("/login", StringComparison.OrdinalIgnoreCase);

    // ═══ 提取前准备（四平台同款：① 账号信息 → ② 预热页 token） ═══

    /// <summary>
    /// 提取前的统一收尾，两步次序对齐老版 DoneButton_Click：
    ///   ① 账号信息（DOM 昵称/头像）必须在**任何跳转之前**抓 —— 老版原注「用户信息提取（必须在跳转前，
    ///      因为跳转后 DOM 不同）」。四平台同款：当前页没有账号卡（停在发布页/登录页/签名页）就回
    ///      创作者首页补抓；补不到按缺省继续（uid 另有来源，不受影响）。
    ///   ② 需要 token 的平台再跳预热页（见 <see cref="LoginPreheat"/>）：抖音 msToken/a_bogus/csrf
    ///      靠发布页自身流量落袋。
    /// 次序反了就会「token 全有、昵称头像全空」（2026-09-12 实测：抖音/视频号 platformName
    /// 退化成平台键 "douyin"/"wechatvideo"，UI 卡标题就是那串键、头像位首字母）。
    /// </summary>
    /// <param name="allowNavigation">是否允许跳页 —— 未确认登录态（比如用户还没扫完码就点「完成登录」）
    /// 必须传 false：只做原地探测，绝不把登录页跳走</param>
    private async Task PrepareBeforeExtractAsync(AccountLoginExtractor extractor, bool allowNavigation = true)
    {
        var tab = _tab;
        if (tab == null) return;

        try
        {
            // ① 账号信息（昵称/头像/平台号）
            await CaptureUserInfoAsync(extractor, tab, allowNavigation);

            // ② 预热页等关键 token（只有 LoginPreheat 列了页面的平台需要）
            if (!allowNavigation) return;
            if (!LoginPreheat.TryGetPage(tab.PlatformKey, out var pageUrl)) return;
            tab.StatusText = "正在打开发布页以捕获登录凭证…";
            Log.Debug("[Preheat] {Platform} → {Url}", tab.PlatformKey, pageUrl);
            await NavigateAndSettleAsync(pageUrl);

            var deadline = DateTime.UtcNow.AddMilliseconds(PreheatTokenWaitMs);
            while (DateTime.UtcNow < deadline && !await extractor.ProbePreheatTokensAsync())
                await Task.Delay(400);
            Log.Debug("[Preheat] {Platform} 结果：token {Result}",
                tab.PlatformKey, extractor.HasPreheatTokens ? "已就绪" : "未抓全（超时，按已有提取）");
        }
        catch (Exception ex) { Log.Warning(ex, "[Preheat] {Platform} 提取前准备失败（继续提取）", tab.PlatformKey); }
        finally { _reloadNavigated = null; }
    }

    /// <summary>
    /// 抓 DOM 账号信息（昵称/头像/平台号）留存：先探当前页，没有账号卡就回创作者首页补抓几次。
    /// 账号卡只长在创作者首页上 —— 停在发布页/合集创作页/登录页/签名页时探测必空，
    /// 这正是「昵称头像丢失、platformName 退化成平台键」的唯一根因（老版靠人停在首页避开它）。
    /// </summary>
    private async Task CaptureUserInfoAsync(
        AccountLoginExtractor extractor, AccountDockTabViewModel tab, bool allowNavigation)
    {
        tab.StatusText = "正在读取账号信息…";
        await extractor.CaptureUserInfoSnapshotAsync();
        if (extractor.HasUserInfoSnapshot || !allowNavigation) return;
        if (LoginPreheat.EntryUrlIsLoginPage(tab.PlatformKey))
        {
            // 视频号：入口 URL 就是登录页，跳过去 = 逼用户重新扫码 —— 宁可少个昵称也不跳
            Log.Debug("[Snapshot] {Platform} 当前页无账号卡，且入口页即登录页 —— 不跳页（按缺省继续）",
                tab.PlatformKey);
            return;
        }

        Log.Debug("[Snapshot] {Platform} 当前页无账号卡，回创作者首页补抓", tab.PlatformKey);
        if (!await NavigateAndSettleAsync(tab.LoginUrl)) return;
        // 账号卡是 SPA 渲染的，可能晚于 NavigationCompleted —— 补抓几次
        for (var i = 0; i < UserInfoRetryAttempts && !extractor.HasUserInfoSnapshot; i++)
        {
            if (i > 0) await Task.Delay(UserInfoRetryDelayMs);
            await extractor.CaptureUserInfoSnapshotAsync();
        }
        Log.Debug("[Snapshot] {Platform} 回首页补抓：{Result}",
            tab.PlatformKey, extractor.HasUserInfoSnapshot ? "账号信息已就绪" : "仍无账号卡（按缺省继续）");
    }

    /// <summary>
    /// 导航 + 等 NavigationCompleted + 首屏流量沉降（凭证捕获靠页面自身流量）。
    /// 返回是否等到了导航完成（超时不算失败 —— 页面卡住也要能收尾）。
    /// </summary>
    private async Task<bool> NavigateAndSettleAsync(string url)
    {
        var tcs = new TaskCompletionSource();
        _reloadNavigated = tcs;
        try
        {
            WebView.Navigate(new Uri(url));
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(RecaptureNavTimeoutMs)) == tcs.Task;
            await Task.Delay(RecaptureSettleMs);
            return completed;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Preheat] 导航失败：{Url}", url);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_reloadNavigated, tcs)) _reloadNavigated = null;
        }
    }

    /// <summary>
    /// 返回平台页(退出 WebView 叠层):清激活 → 宿主隐藏 → 账号列表露出。
    /// 只切可见性不销毁 —— WebView 保活,再点标签秒回当前浏览位置(登录态不丢)。
    /// </summary>
    private void BackToPageButton_Click(object? sender, RoutedEventArgs e)
    {
        _tab?.Dock.LeaveToPage();
    }

    /// <summary>
    /// 刷新当前页面(平台侧「系统繁忙,请刷新页面重试」等瞬时错误直接点此,不必关标签重开)。
    /// 优先 location.reload()(SPA/业务错误页均可整页重载;武装态下自动检测随新页面流量重新走),
    /// 文档不可用(加载失败页等)时退回按当前地址重新导航。
    /// </summary>
    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await WebView.InvokeScript("location.reload(); 'ok'");
            return;
        }
        catch
        {
            // 文档上下文不可用(加载失败页/跨进程重启等)→ 下方重新导航兜底
        }
        if (WebView.Source is { } uri)
            WebView.Navigate(uri);
    }

    /// <summary>手动兜底(「完成登录」按钮;常驻工具条,失败后立刻可再点)</summary>
    private async void CompleteLoginButton_Click(object? sender, RoutedEventArgs e)
    {
        var tab = _tab;
        if (tab == null) return;
        if (tab.Phase is DockOpenPhase.Extracting) return; // 防重入(按钮已置灰,此处兜底)

        // 已同步/浏览态再点 = 再抓一次凭证:重新武装 + 重载登录页,让页面流量把 msToken/a_bogus/csrf
        // 这类「靠请求捕获」的字段重新落袋 —— 服务端 SyncCredentials 是整体覆盖 extra_data,
        // 原地提取若恰逢这些字段为空就会把已有值抹掉(抖音发布用原生签名器不依赖它们,但其它平台会);
        // 重载后档案里登录态仍在,不必重新扫码。
        if (tab.Phase is DockOpenPhase.Ready)
        {
            _ = RecaptureAsync(tab);
            return;
        }

        EnsureExtractor(); // 武装态但拦截器未就位时补挂(否则点击静默无效)
        var extractor = _extractor;
        if (extractor == null) return;
        try
        {
            // 检测到登录态才允许跳页（不然会把还没扫完码的登录页跳走）；没检测到也走一遍
            // 「原地」账号信息探测（不跳转）—— 账号卡就在当前页时能直接抓到昵称/头像。
            await PrepareBeforeExtractAsync(extractor, allowNavigation: extractor.IsLoginDetected);

            tab.StatusText = "正在提取凭证…";
            await tab.Dock.CompleteLoginAsync(tab, extractor);
            await RetryAfterLeavingLoginPageAsync(tab, extractor);   // 视频号：被登录页拒 → 等跳走自动重试
        }
        catch (Exception ex) { Log.Warning(ex, "Manual complete login failed"); }
    }

    /// <summary>
    /// 浏览态「完成登录」= 重新抓取凭证:重新武装 + 重载登录页 → 等加载完 → 预热页 → 提取+同步。
    /// 走重载而非原地提取:请求捕获类字段(msToken/a_bogus/csrf 等)要页面流量才落袋,
    /// 而服务端 extra_data 是整体覆盖 —— 空着提交会抹掉已有值。重载后档案登录态仍在,不必重新扫码。
    /// 预热页(发布/创作页)是补 token 的关键:登录页/首页的流量给不出 msToken/a_bogus/api_ph。
    /// 自动通道若先一步动手(Phase 变 Extracting/Ready)则让位,不重复提取。
    /// </summary>
    private async Task RecaptureAsync(AccountDockTabViewModel tab)
    {
        try
        {
            // 视频号：入口页即登录页，重载登录页 = 逼用户重新扫码（实测「一点完成登录就回到登录页」）。
            // 档案里的会话还在，就地重抓即可 —— 它的 extra_data 只有 deviceId（localStorage 兜底可读），
            // 不依赖请求捕获类 token，无需重载页面造流量。
            if (LoginPreheat.EntryUrlIsLoginPage(tab.PlatformKey))
            {
                EnsureExtractor(); // 浏览态拦截器已退订，重抓前按需重建
                var inPlace = _extractor;
                if (inPlace == null) return;
                tab.Phase = DockOpenPhase.AwaitLogin; // 同 RequestRelogin 的相位语义，但不触发宿主重载
                tab.StatusText = "正在重新抓取凭证…";
                try
                {
                    await PrepareBeforeExtractAsync(inPlace, allowNavigation: false);
                    if (_tab != tab) return;
                    await tab.Dock.CompleteLoginAsync(tab, inPlace);
                }
                finally { ReleaseExtractor(); } // 浏览态不常驻拦截器（与 ArmAutoDetect 回落同语义）
                return;
            }

            _reloadNavigated = new TaskCompletionSource();
            tab.RequestRelogin(); // 宿主随 ArmAutoDetect 变化重载登录页(拦截器已先建好)
            await Task.WhenAny(_reloadNavigated.Task, Task.Delay(RecaptureNavTimeoutMs));
            _reloadNavigated = null;
            await Task.Delay(RecaptureSettleMs);

            var extractor = _extractor;
            if (_tab != tab || extractor == null || !tab.ArmAutoDetect) return;
            if (tab.Phase is not (DockOpenPhase.AwaitLogin or DockOpenPhase.Failed)) return; // 自动通道已接管

            await PrepareBeforeExtractAsync(extractor);
            if (_tab != tab || !tab.ArmAutoDetect) return;
            if (tab.Phase is not (DockOpenPhase.AwaitLogin or DockOpenPhase.Failed)) return;

            await tab.Dock.CompleteLoginAsync(tab, extractor);
        }
        catch (Exception ex) { Log.Warning(ex, "Re-capture credentials failed"); }
        finally { _reloadNavigated = null; }
    }

    // ═══ 释放 ═══

    /// <summary>退订事件 + best-effort 关闭 WebView2 controller(标签 × /壳关闭时调用)</summary>
    public void Teardown()
    {
        DetachTab();
        // 先归还焦点，再关控制器：隐藏/失效的 WebView2 被恢复焦点会抛 E_INVALIDARG 直接杀进程
        WebViewFocusGuard.ClearFocusInside(this);
        WebView2Teardown.TryCloseController(WebView);
    }

    private void DetachTab()
    {
        ReleaseExtractor();
        UnregisterPageHosts(); // WebView 随本宿主销毁 —— 注册表里不能留悬挂的页面宿主
        _navigatedOnce = false;
        _pageLoadedOnce = false;
        _hostPending = false;
        _cookiesRestored = false;
        _cookieRestoreTried = false;
        if (_tab != null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
            _tab.NavigateRequested -= OnTabNavigateRequested;
            _tab = null;
        }
    }
}
