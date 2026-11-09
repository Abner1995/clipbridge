using System.Text.Json;
using Avalonia.Controls;
using CreatFlow.Platforms.Core;

namespace CreatFlow.NewApp.Services.AccountLogin;

/// <summary>
/// 平台登录凭证提取器 —— 自 PlatformLoginWindow.axaml.cs 逐字移植(Phase 1 抽出为复用组件)。
///
/// 一个标签/窗口对应一个实例:ctor 订阅 WebView.WebResourceRequested 做
/// 「登录成功检测 + URL/请求头 token 逐请求捕获」,ExtractAsync() 跑完整提取管线
/// (cookie 含 HttpOnly + localStorage + 各平台 uid/name/avatar + env 指纹 + ExtraData)。
/// 与窗口/坞解耦:谁持有 WebView 谁驱动;Detach() 退订,防多标签捕获串扰。
///
/// 双通道约定:LoginDetected 事件 = 自动通道触发点(坞订阅后走自动提取);
/// 「完成登录」按钮 = 手动兜底通道(任意时刻直接 ExtractAsync)。
/// </summary>
public sealed class AccountLoginExtractor
{
    /// <summary>提取结果错误码:未提取到 Cookie(页面实际未登录/时序未就绪)</summary>
    public const string ErrorNoCookie = "no_cookie";

    /// <summary>提取结果错误码:人还停在登录页(尚未扫码/登录未完成)—— 拒绝提取,别覆盖已有凭证</summary>
    public const string ErrorNotLoggedIn = "not_logged_in";

    private readonly NativeWebView _webView;
    private readonly string _platformKey;
    private bool _loginDetected;

    // ── 凭证捕获(env 指纹快照 + 视频号 deviceId + 快手 api_ph + 抖音 csrf/msToken/a_bogus) ──
    private string? _capturedMsToken;
    private string? _capturedABogus;
    private string? _capturedCsrfToken;
    private string? _capturedDeviceId;
    private string? _capturedKuaishouApiPh;

    // ── DOM 账号信息快照(预热页导航**之前**抓;昵称/头像只长在账号卡上,跳转后 DOM 不同) ──
    private string? _snapUid;
    private string? _snapName;
    private string? _snapAvatar;

    // ── 页面加载后的登录态探针（B站/头条没有可靠的「登录后才有」URL，见 OnNavigationCompleted）──
    private bool _navProbeRunning;

    /// <summary>首次命中「已登录才会访问的 API」URL 时触发(UI 线程,同步回调内)</summary>
    public event Action? LoginDetected;

    /// <summary>拦截事件诊断打印剩余次数（排障用；查「token 抓不到/检测不触发」先看这些行）</summary>
    private int _interceptDiagLeft = 12;

    public bool IsLoginDetected => _loginDetected;

    public AccountLoginExtractor(NativeWebView webView, string platformKey)
    {
        _webView = webView;
        _platformKey = platformKey;
        _webView.WebResourceRequested += OnWebResourceRequested;
        // B站/头条：登录后没有一条「只在登录后出现」的请求 URL 可当检测信号（首页/登录页
        // 会发同一批接口），改成「页面加载完成后问一次页面接口」的探针。
        if (UsesNavigationLoginProbe(_platformKey))
            _webView.NavigationCompleted += OnNavigationCompleted;
    }

    /// <summary>退订网络拦截(宿主销毁/标签关闭时必须调用,防悬挂与多标签串扰)</summary>
    public void Detach()
    {
        _webView.WebResourceRequested -= OnWebResourceRequested;
        _webView.NavigationCompleted -= OnNavigationCompleted;
    }

    /// <summary>
    /// 该平台「预热页」要抓的关键 token 是否已到齐 —— 预热等待的提前结束条件（见 LoginPreheat）。
    /// 无 token 需求的平台恒 true（不等待）。注意：已绑定账号重抓时旧值仍在内存里，
    /// 条件会立刻为真 → 只跳过等待、不会跳过预热页导航本身。
    /// </summary>
    public bool HasPreheatTokens => _platformKey switch
    {
        PlatformNames.Douyin =>
            !string.IsNullOrWhiteSpace(_capturedMsToken) && !string.IsNullOrWhiteSpace(_capturedABogus),
        PlatformNames.Kuaishou => !string.IsNullOrWhiteSpace(_capturedKuaishouApiPh),
        _ => true,
    };

    /// <summary>
    /// 预热等待探针（宿主在预热页轮询调用）：除已抓到的流量字段外，还会现读一次
    /// 「页面侧才有的凭据」—— 快手 api_ph/api_st 是 localStorage 里的，只有读页面才知道
    /// 到没到（光看 <see cref="HasPreheatTokens"/> 会一直等到超时）。
    /// </summary>
    public async Task<bool> ProbePreheatTokensAsync()
    {
        if (_platformKey == PlatformNames.Kuaishou && string.IsNullOrWhiteSpace(_capturedKuaishouApiPh))
        {
            try { await CaptureKuaishouApiPhAsync(); }
            catch { /* 页面未就绪等 —— 下一轮再试 */ }
        }
        return HasPreheatTokens;
    }

    /// <summary>
    /// cookie 名是否为合法的 RFC 6265 令牌（`!#$%&amp;'*+-.^_`|~` + 字母数字）。
    ///
    /// 为什么要有这道校验：历史版本给视频号档案注入过一批**转义污染的脏名 cookie**
    /// （`\"sessionid`、`\\\"sessionid`… 每轮同步+注入加深一层），它们会被提取管线
    /// 原样同步到服务端、再注回档案，越滚越多（实测攒到 10 条）—— 浏览器把这一串
    /// 互相打架的 cookie 一起发出去，视频号登录页判不出登录态、卡在扫码页（2026-10-09）。
    /// 提取端（不再往服务端带）与注入端（不再注回档案）共用它。
    /// </summary>
    public static bool IsValidCookieName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var ch in name)
        {
            var ok = ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.'
                or '^' or '_' or '`' or '|' or '~';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// 会话恢复时把服务端存的 cookie 注回档案用的域。
    ///
    /// 默认用登录页的 host（host-only cookie）：其余平台页面与 API 同域，够用。
    /// B站/头条的登录 cookie 挂在主域上（页面是 member.bilibili.com / mp.toutiao.com，
    /// 账号接口在 api.bilibili.com / sso.toutiao.com）—— 按 host 注入会让跨子域请求
    /// 带着空会话，恢复后仍显示未登录。
    /// </summary>
    public static string CookieRestoreDomain(string platformKey, string fallbackHost) => platformKey switch
    {
        PlatformNames.Bilibili => ".bilibili.com",
        PlatformNames.Toutiao => ".toutiao.com",
        _ => fallbackHost,
    };

    /// <summary>读快手 api_ph（缺失时退化 api_st）—— 提取管线与预热探针共用</summary>
    private async Task CaptureKuaishouApiPhAsync()
    {
        var apiPh = UnescapeJsonString(
            await _webView.InvokeScript("localStorage.getItem('kuaishou.web.cp.api_ph') || ''"));
        if (string.IsNullOrWhiteSpace(apiPh) || apiPh.Length <= 10)
        {
            apiPh = UnescapeJsonString(
                await _webView.InvokeScript("localStorage.getItem('kuaishou.web.cp.api_st') || ''"));
        }
        if (!string.IsNullOrWhiteSpace(apiPh) && apiPh.Length > 10)
            _capturedKuaishouApiPh = apiPh;
        // 注：2026-09-12 实测快手 localStorage 已无 kuaishou.web.cp.api_ph / api_st
        //（只剩 refresh_last_time_* 一类键，发布页跑满 12s 仍读不到）—— 合集链路靠
        // KuaishouApiClient 的兜底 api_ph 通过；发布若确需该 token，须页面内 hook fetch/XHR。
    }

    /// <summary>重新武装(重登/再次扫码后要重新触发 LoginDetected 时调用)</summary>
    public void ResetDetection() => _loginDetected = false;

    // ═══ 网络拦截:登录成功检测 + 凭证捕获 ═══

    private void OnWebResourceRequested(object? sender, EventArgs e)
    {
        var url = ResolveRequestUrl(e);

        // 诊断：拦截器是否真的在收流量（token 捕获与登录自动检测全靠它；不触发 = 全空）
        if (_interceptDiagLeft > 0)
        {
            _interceptDiagLeft--;
            Log.Debug("[Interceptor] {Platform} {Method} {Url}", _platformKey,
                ResolveRequestMethod(e), url.Length > 160 ? url[..160] : url);
        }

        // ── 抖音:URL msToken/a_bogus + 请求头 csrf(STS 系已裁剪) ──
        if (_platformKey == PlatformNames.Douyin && !string.IsNullOrWhiteSpace(url))
        {
            ExtractQueryToken(url, "msToken", v => _capturedMsToken = v);
            ExtractQueryToken(url, "a_bogus", v => _capturedABogus = v);
            if (_capturedCsrfToken == null)
            {
                var csrf = ReadHeader(e, "x-secsdk-csrf-token");
                if (!string.IsNullOrWhiteSpace(csrf)) _capturedCsrfToken = csrf;
            }
        }

        // ── 头条：合集写接口要页面 secsdk 现签的 x-secsdk-csrf-token（Cookie 里没有） ──
        if (_platformKey == PlatformNames.Toutiao && _capturedCsrfToken == null)
        {
            var csrf = ReadHeader(e, "x-secsdk-csrf-token");
            if (!string.IsNullOrWhiteSpace(csrf)) _capturedCsrfToken = csrf;
        }

        // ── 视频号 device_id(finger-print-device-id 请求头) ──
        if (_platformKey == PlatformNames.WechatVideo && _capturedDeviceId == null)
        {
            var deviceId = ReadHeader(e, "finger-print-device-id");
            if (!string.IsNullOrWhiteSpace(deviceId) && deviceId.Length > 10)
                _capturedDeviceId = deviceId;
        }

        if (_loginDetected) return;
        if (IsAuthApiUrl(url))
        {
            _loginDetected = true;
            LoginDetected?.Invoke();
        }
    }

    // ═══ 页面加载后登录态探针（B站 / 头条） ═══

    /// <summary>
    /// 这两个平台没有可靠的「登录后才出现」的请求 URL：B站首页未登录也会打
    /// <c>/x/web/white</c>，头条首页未登录也会打 <c>creator_center/user_info</c> ——
    /// 按 URL 判会把「还没扫码」误判成已登录。改为页面加载完成后在页面上下文问一次
    /// 平台接口（B站 <c>web-interface/nav</c> 的 isLogin / 头条 user_login_status 的 is_login），
    /// 返回已登录才触发自动提取。已登录账号首次打开（档案会话还在）也靠它自动同步。
    /// </summary>
    private static bool UsesNavigationLoginProbe(string platformKey) =>
        platformKey is PlatformNames.Bilibili or PlatformNames.Toutiao;

    private async void OnNavigationCompleted(object? sender, EventArgs e)
    {
        if (_loginDetected || _navProbeRunning) return;
        if (IsOnPlatformLoginPage()) return;   // 登录页上必然未登录，省一次 4s 兜底等待
        _navProbeRunning = true;
        try
        {
            var loggedIn = await ProbeApiLoginStateAsync();
            if (loggedIn == true && !_loginDetected)
            {
                _loginDetected = true;
                Log.Debug("[Interceptor] {Platform} 页面接口确认已登录（{Page}）",
                    _platformKey, CurrentPageUrl);
                LoginDetected?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Interceptor] {Platform} 登录态探针失败（忽略）", _platformKey);
        }
        finally { _navProbeRunning = false; }
    }

    /// <summary>
    /// 当前页是否**还停在平台登录页**（接口拉不到时的兜底判据）。
    /// B站：passport.bilibili.com；头条：/auth/page/login 或 sso 域。
    /// </summary>
    private bool IsOnPlatformLoginPage() => _platformKey switch
    {
        PlatformNames.Bilibili => CurrentPageUrl.Contains("passport.bilibili.com"),
        PlatformNames.Toutiao => CurrentPageUrl.Contains("/auth/page/login")
            || CurrentPageUrl.Contains("sso.toutiao.com"),
        _ => false,
    };

    /// <summary>
    /// 问页面接口「现在登录了吗」。返回 null = 平台不需要判定 / 页面不可用 / 请求失败
    /// （调用方按未知处理，不拦提取）。
    /// </summary>
    private async Task<bool?> ProbeApiLoginStateAsync() => _platformKey switch
    {
        PlatformNames.Bilibili => await ProbeBilibiliLoginAsync(),
        PlatformNames.Toutiao => await ProbeToutiaoLoginAsync(),
        _ => (bool?)null,
    };

    // ═══ 拦截事件参数读取 ═══
    // Avalonia.Controls.WebView 11.4 的形状是 e.Request{ Uri, Method, Headers }；
    // 老版（老 App 的 WebView 包）是 e.RequestUri / Request.Headers.GetHeader(name)。
    // 坞内之前套用老版反射（RequestUri）→ **恒为空串**：token 抓不到、登录自动检测从不触发
    // （2026-09-12 实测：338 次拦截事件全部 url=''）。故先按强类型读，反射仅作老形状兜底。

    /// <summary>拦截请求 URL（强类型优先；老形状反射兜底）</summary>
    private static string ResolveRequestUrl(EventArgs e)
    {
        if (e is WebResourceRequestedEventArgs a && a.Request?.Uri is { } uri)
            return uri.ToString();
        try { return e.GetType().GetProperty("RequestUri")?.GetValue(e)?.ToString() ?? ""; }
        catch { return ""; }
    }

    /// <summary>拦截请求方法（诊断用）</summary>
    private static string ResolveRequestMethod(EventArgs e) =>
        e is WebResourceRequestedEventArgs a ? a.Request?.Method?.Method ?? "" : "";

    /// <summary>读请求头（强类型优先；老形状 Request.Headers.GetHeader 反射兜底）</summary>
    private static string? ReadHeader(EventArgs e, string name)
    {
        if (e is WebResourceRequestedEventArgs a && a.Request?.Headers is { } headers)
        {
            foreach (var h in headers)
                if (string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
                    return h.Value;
            return null;
        }
        try
        {
            var req = e.GetType().GetProperty("Request")?.GetValue(e);
            var hdrs = req?.GetType().GetProperty("Headers")?.GetValue(req);
            var getHeader = hdrs?.GetType().GetMethod("GetHeader");
            return getHeader?.Invoke(hdrs, [name])?.ToString();
        }
        catch { return null; }
    }

    private static void ExtractQueryToken(string url, string key, Action<string> setter)
    {
        var idx = url.IndexOf($"{key}=", StringComparison.Ordinal);
        if (idx < 0) return;
        var start = idx + key.Length + 1;
        if (start >= url.Length) return;
        var end = url.IndexOf('&', start);
        var value = end > 0 ? url[start..end] : url[start..];
        if (value.Length > 20)
            setter(Uri.UnescapeDataString(value));
    }

    private bool IsAuthApiUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return _platformKey switch
        {
            PlatformNames.Douyin =>
                url.Contains("creator.douyin.com") && (
                    url.Contains("/aweme/v1/web/") || url.Contains("/user/info") || url.Contains("msToken=")),
            // 快手创作者平台的数据接口不少走 /rest/（如 /rest/cp/works/...），只认 /api/ 会漏判
            PlatformNames.Kuaishou =>
                url.Contains("cp.kuaishou.com") && (url.Contains("/api/") || url.Contains("/rest/")),
            // 小红书创作平台另有 /web_api/ 一类路径
            PlatformNames.Xiaohongshu =>
                url.Contains("creator.xiaohongshu.com") && (url.Contains("/api/") || url.Contains("/web_api/")),
            // 视频号助手：**不能**按 /cgi-bin/ 或 /micro/ 泛匹配 —— 登录页一开就自带
            // /cgi-bin/mmfinderassistant-bin/report-perf（埋点），会把「还没扫码」误判成已登录，
            // 于是提前提取出一份无昵称的空壳会话（2026-09-14 实测：platformName=wechatvideo）。
            // 只认登录后才有意义的两个页面族：/platform 与 /micro/content。
            PlatformNames.WechatVideo =>
                url.Contains("channels.weixin.qq.com") && (
                    url.Contains("/platform") || url.Contains("/micro/content")),
            // B站创作中心：**不能**认 /x/web/white 或 web-interface/nav 的 URL —— 未登录的首页
            // 也会打它们（实测 2026-11-04：首次加载就打 /x/web/white）。只认登录后才出现的
            // 数据/投稿接口；已登录账号首次打开另靠 NavigationCompleted 探针（见上）。
            PlatformNames.Bilibili =>
                url.Contains("member.bilibili.com") && (
                    url.Contains("/x/vupre/") || url.Contains("/x/web/index/")
                    || url.Contains("/x/web/elec/") || url.Contains("/x/web/data/")
                    || url.Contains("/x2/creative/") || url.Contains("/preupload")),
            // 头条：登录完成的 SSO 回跳是最干净的信号（首页未登录也会打 get_media_info /
            // creator_center/user_info，按那些判会误触发）
            PlatformNames.Toutiao =>
                url.Contains("mp.toutiao.com/passport/sso/login/callback"),
            _ => false
        };
    }

    // ═══ 完整提取管线(原「完成登录」点击处理器体,逐字移植) ═══

    /// <summary>
    /// 提取登录凭证并组装 LoginResult。UI 线程调用(InvokeScript 要求)。
    /// 返回约定:Success=true 提取成功;Success=false + Error==ErrorNoCookie 表示
    /// Cookie 未就绪(需回登录等待/提示用户点完成);其余 Error=异常消息。
    /// </summary>
    public async Task<LoginResult?> ExtractAsync()
    {
        string? cookie = null, localStorage = null, uid = null, name = null, avatar = null;

        try
        {
            // Cookie(CookieManager 含 HttpOnly)+ localStorage 全量
            cookie = await ExtractAllCookiesAsync();
            localStorage = await ExtractLocalStorageAsync();

            // B站/头条：页面接口明确说「未登录」= 人还停在扫码页，这一版 cookie 是登录页自身的。
            // 服务端 SyncCredentials 是**整体覆盖**，放行会把上一次真实登录的凭证抹掉 ——
            // 与视频号「在登录页上提取」同一条安全闸（2026-09-14 实测教训）。
            // 页面 URL 还在登录页就不必问接口（必然未登录）；接口拉不到（CORS/上下文不可用）
            // 时按未知处理、不拦 —— 但页面在登录页这一条必须拦住。
            var onLoginPage = IsOnPlatformLoginPage();
            var apiLogin = onLoginPage ? false : await ProbeApiLoginStateAsync();
            if (apiLogin == false)
            {
                Log.Warning("[Extract] {Platform} 判定未登录（登录页={OnLoginPage}，页={Page}）—— 拒绝提取",
                    _platformKey, onLoginPage, CurrentPageUrl);
                return new LoginResult { Success = false, Error = ErrorNotLoggedIn };
            }

            // 用户信息(必须在跳转前 — DOM 不同)。
            // 预热页导航前抓下的快照优先(那一版是在账号卡所在的首页抓的,见 CaptureUserInfoSnapshotAsync);
            // 快照缺的字段用当前页实时探测补 —— 无预热页的平台只有实时探测这一路。
            var info = await ExtractPlatformUserAsync();
            if (!string.IsNullOrWhiteSpace(info))
            {
                using var doc = JsonDocument.Parse(info);
                uid = doc.RootElement.TryGetProperty("uid", out var u) ? u.GetString() : null;
                name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                avatar = doc.RootElement.TryGetProperty("avatar", out var a) ? a.GetString() : null;
            }
            if (!string.IsNullOrWhiteSpace(_snapUid)) uid = _snapUid;
            if (!string.IsNullOrWhiteSpace(_snapName)) name = _snapName;
            if (!string.IsNullOrWhiteSpace(_snapAvatar)) avatar = _snapAvatar;

            // 视频号 device_id 兜底:localStorage
            if (_platformKey == PlatformNames.WechatVideo && string.IsNullOrWhiteSpace(_capturedDeviceId))
            {
                var lsDevId = UnescapeJsonString(
                    await _webView.InvokeScript("localStorage.getItem('_finger_print_device_id') || ''"));
                if (!string.IsNullOrWhiteSpace(lsDevId)) _capturedDeviceId = lsDevId;
            }

            // 快手 api_ph / api_st(会话 token,发布 API 必需)
            if (_platformKey == PlatformNames.Kuaishou)
                await CaptureKuaishouApiPhAsync();
        }
        catch (Exception ex)
        {
            return new LoginResult { Success = false, Error = ex.Message };
        }

        if (string.IsNullOrWhiteSpace(cookie))
            return new LoginResult { Success = false, Error = ErrorNoCookie };

        // 「在登录页上提取」= 还没登录成功。视频号的入口 URL 本身就是登录页 —— 这一版 cookie 是
        // 登录页自身的（不含会话），而服务端 SyncCredentials 是**整体覆盖**：放行会把上一次真实
        // 登录的凭证抹掉（2026-09-14 实测：视频号记录被登录页 cookie 覆盖成无昵称空壳）。
        // 故：登录页 + 没抓到任何账号信息 → 直接拒绝，让用户先扫码。
        if (LoginPreheat.EntryUrlIsLoginPage(_platformKey)
            && CurrentPageUrl.Contains("/login", StringComparison.OrdinalIgnoreCase)
            && !HasUserInfoSnapshot)
        {
            Log.Warning("[Extract] {Platform} 仍在登录页（{Page}）—— 拒绝提取，避免覆盖已有凭证",
                _platformKey, CurrentPageUrl);
            return new LoginResult { Success = false, Error = ErrorNotLoggedIn };
        }

        // 抓取结果落日志：ExtraData 是整体覆盖写服务端 —— 少抓一个 token 就是把它抹空,
        // 排查「发布缺料」先看这行（预热页是否生效一目了然）
        Log.Debug("[Extract] 平台={Platform} 页={Page} uid={Uid} name={Name} avatar={HasAvatar} " +
            "msToken={HasMsToken} aBogus={HasABogus} csrf={HasCsrf} apiPh={HasApiPh} deviceId={HasDeviceId}",
            _platformKey,
            CurrentPageUrl,
            string.IsNullOrWhiteSpace(uid) ? "-" : uid,
            string.IsNullOrWhiteSpace(name) ? "-" : name,
            !string.IsNullOrWhiteSpace(avatar),
            !string.IsNullOrWhiteSpace(_capturedMsToken), !string.IsNullOrWhiteSpace(_capturedABogus),
            !string.IsNullOrWhiteSpace(_capturedCsrfToken), !string.IsNullOrWhiteSpace(_capturedKuaishouApiPh),
            !string.IsNullOrWhiteSpace(_capturedDeviceId));

        // ═══ 组装 ExtraData(env 指纹 + 各平台会话凭证;STS/签名系裁剪) ═══
        var extraData = new Dictionary<string, object?>();
        var envSnapshot = await CaptureEnvironmentSnapshotAsync();
        if (envSnapshot is { IsValid: true })
            extraData["env"] = JsonSerializer.Serialize(envSnapshot);

        if (_platformKey == PlatformNames.WechatVideo)
        {
            if (!string.IsNullOrWhiteSpace(_capturedDeviceId)) extraData["deviceId"] = _capturedDeviceId;
            extraData["aid"] = Guid.NewGuid().ToString("N");
        }
        else if (_platformKey == PlatformNames.Kuaishou)
        {
            if (!string.IsNullOrWhiteSpace(_capturedKuaishouApiPh)) extraData["apiPh"] = _capturedKuaishouApiPh;
            var ksUserId = ExtractCookieValue(cookie, "userId") ?? ExtractCookieValue(cookie, "bUserId");
            if (!string.IsNullOrWhiteSpace(ksUserId)) extraData["userId"] = ksUserId;
        }
        else if (_platformKey == PlatformNames.Bilibili)
        {
            // 写接口的 csrf 参数 = Cookie 里的 bili_jct（2026-11-04 抓包）
            var biliJct = ExtractCookieValue(cookie, "bili_jct");
            if (!string.IsNullOrWhiteSpace(biliJct)) extraData["csrf"] = biliJct;
        }
        else if (_platformKey == PlatformNames.Toutiao)
        {
            // 头条写接口要页面 secsdk 现签的 x-secsdk-csrf-token，Cookie 里没有 ——
            // 由网络拦截从页面请求头抓（2026-11-05 合集写接口实测必需）。
            // 账号信息（media_id）走 PlatformUid，不重复存。
            if (!string.IsNullOrWhiteSpace(_capturedCsrfToken))
                extraData["csrfToken"] = _capturedCsrfToken;
        }
        else if (_platformKey == PlatformNames.Douyin)
        {
            if (!string.IsNullOrWhiteSpace(_capturedMsToken)) extraData["msToken"] = _capturedMsToken;
            if (!string.IsNullOrWhiteSpace(_capturedABogus)) extraData["aBogus"] = _capturedABogus;
            if (!string.IsNullOrWhiteSpace(_capturedCsrfToken)) extraData["csrfToken"] = _capturedCsrfToken;
        }

        return new LoginResult
        {
            Success = true,
            Cookie = cookie,
            LocalStorageJson = localStorage,
            PlatformUid = uid,
            PlatformName = string.IsNullOrWhiteSpace(name) ? _platformKey : name,
            PlatformAvatar = avatar,
            ExtraDataJson = extraData.Count > 0 ? JsonSerializer.Serialize(extraData) : null,
        };
    }

    // ═══ 各平台 uid/name/avatar 提取(老版 DOM 探测逐字移植) ═══

    private Task<string?> ExtractPlatformUserAsync() => _platformKey switch
    {
        PlatformNames.WechatVideo => ExtractWechatVideoUserAsync(),
        PlatformNames.Kuaishou => ExtractKuaishouUserAsync(),
        PlatformNames.Xiaohongshu => ExtractXhsUserAsync(),
        PlatformNames.Bilibili => ExtractBilibiliUserAsync(),
        PlatformNames.Toutiao => ExtractToutiaoUserAsync(),
        PlatformNames.Douyin => ExtractDouyinUserAsync(),
        _ => Task.FromResult<string?>(null),
    };

    /// <summary>
    /// 快照里是否已有可用账号信息 —— 判定看**昵称/头像**，不看 uid：
    /// uid 来自 localStorage(SLARDAR)，任何页面都读得到；昵称/头像只在账号卡 DOM 里，
    /// 才是「当前页能不能采到账号信息」的判据。
    /// </summary>
    public bool HasUserInfoSnapshot =>
        !string.IsNullOrWhiteSpace(_snapName) || !string.IsNullOrWhiteSpace(_snapAvatar);

    /// <summary>当前页面 URL（诊断用：账号信息探测/提取跑在哪个页面上，对不上时一眼可见）</summary>
    private string CurrentPageUrl =>
        _webView.Source?.ToString() is { Length: > 0 } url
            ? (url.Length > 90 ? url[..90] : url)
            : "-";

    /// <summary>
    /// 预热页导航**之前**抓一次 DOM 账号信息留存 —— 老版原始次序：
    /// 「用户信息提取（必须在跳转前，因为跳转后 DOM 不同）」→ 再跳发布页取 token。
    /// 预热页（合集创作/发布页）上没有账号卡，跳过去再探测就只剩空值
    /// （2026-09-12 实测：抖音账号同步后 platformName 退化成平台键 "douyin"，头像位、昵称全空）。
    ///
    /// 覆盖策略：只在抓到时覆盖，绝不把上一次的好值抹空；但 uid 变了视为换号，
    /// 整组重置 —— 防上一个账号的昵称/头像串到新账号。
    /// </summary>
    public async Task CaptureUserInfoSnapshotAsync()
    {
        try
        {
            var info = await ExtractPlatformUserAsync();
            if (string.IsNullOrWhiteSpace(info)) return;
            using var doc = JsonDocument.Parse(info);
            var uid = doc.RootElement.TryGetProperty("uid", out var u) ? u.GetString() : null;
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            var avatar = doc.RootElement.TryGetProperty("avatar", out var a) ? a.GetString() : null;

            if (!string.IsNullOrWhiteSpace(uid) && uid != _snapUid)
            {
                _snapUid = uid;
                _snapName = name;
                _snapAvatar = avatar;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(uid)) _snapUid = uid;
                if (!string.IsNullOrWhiteSpace(name)) _snapName = name;
                if (!string.IsNullOrWhiteSpace(avatar)) _snapAvatar = avatar;
            }

            Log.Debug("[Snapshot] 平台={Platform} 页={Page} uid={Uid} name={Name} avatar={Avatar}",
                _platformKey,
                CurrentPageUrl,
                string.IsNullOrWhiteSpace(_snapUid) ? "-" : _snapUid,
                string.IsNullOrWhiteSpace(_snapName) ? "-" : _snapName,
                string.IsNullOrWhiteSpace(_snapAvatar) ? "-" : "有");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Snapshot] {Platform} 账号信息快照失败（继续提取）", _platformKey);
        }
    }

    private async Task<string?> ExtractWechatVideoUserAsync() => UnescapeJsonString(await _webView.InvokeScript("""
        (function() {
            var uid = '', name = '', avatar = '';
            try {
                var avatarImg = document.querySelector('.finder-info-container img.avatar');
                if (avatarImg && avatarImg.src) avatar = avatarImg.src;
            } catch(e) {}
            if (!avatar) {
                var imgs = document.querySelectorAll('img');
                for (var i = 0; i < imgs.length; i++) {
                    var src = imgs[i].src || '';
                    if (src.indexOf('wx.qlogo.cn') > -1 || src.indexOf('qlogo.cn/finderhead') > -1) {
                        avatar = src; break;
                    }
                }
            }
            try {
                var nameEl = document.querySelector('.finder-nickname');
                if (nameEl) {
                    var t = (nameEl.textContent || nameEl.innerText || '').trim();
                    if (t.length > 0 && t.length < 50) name = t;
                }
            } catch(e) {}
            if (!name) {
                var selectors = [
                    '.finder-nickname', '[class*=nickname]', '[class*=username]',
                    '[class*=NickName]', '[class*=finder-name]',
                    '[class*=account-name]', '[class*=avatar] ~ [class*=name]',
                ];
                for (var i = 0; i < selectors.length; i++) {
                    try {
                        var el = document.querySelector(selectors[i]);
                        if (el) {
                            var t = el.textContent || el.innerText || '';
                            t = t.trim().replace(/\s+/g, ' ');
                            if (t.length > 0 && t.length < 50 &&
                                t !== '视频号' && t !== '微信' && t !== 'WeChat') {
                                name = t; break;
                            }
                        }
                    } catch(e) {}
                }
            }
            try {
                var uidEl = document.querySelector('.finder-uniq-id');
                if (uidEl) {
                    var t = (uidEl.textContent || uidEl.innerText || '').trim();
                    if (t.length > 3 && t.length < 50) uid = t;
                }
            } catch(e) {}
            if (!uid) {
                try {
                    var cookies = document.cookie.split(';');
                    for (var i = 0; i < cookies.length; i++) {
                        var c = cookies[i].trim();
                        if (c.startsWith('wxuin=')) { uid = c.substring('wxuin='.length); break; }
                    }
                } catch(e) {}
            }
            return JSON.stringify({ uid: uid, name: name, avatar: avatar });
        })()
        """));

    private async Task<string?> ExtractKuaishouUserAsync() => UnescapeJsonString(await _webView.InvokeScript("""
        (function() {
            var uid = '', name = '', avatar = '';
            try {
                var avatarImg = document.querySelector('.user-info-avatar img');
                if (avatarImg && avatarImg.src) avatar = avatarImg.src;
            } catch(e) {}
            if (!avatar) {
                var imgs = document.querySelectorAll('img');
                for (var i = 0; i < imgs.length; i++) {
                    var src = imgs[i].src || '';
                    if ((src.indexOf('kwimgs.com') > -1 || src.indexOf('yximgs.com') > -1) &&
                        imgs[i].naturalWidth > 20 && imgs[i].naturalWidth < 200) {
                        avatar = src; break;
                    }
                }
            }
            try {
                var nameEl = document.querySelector('.user-info-name');
                if (nameEl) {
                    var t = (nameEl.textContent || nameEl.innerText || '').trim();
                    if (t.length > 0 && t.length < 50) name = t;
                }
            } catch(e) {}
            if (!name) {
                var selectors = [
                    '[class*=user-info-name]',
                    '[class*=userName]', '[class*=username]', '[class*=nickname]',
                    '[class*=UserName]', '[class*=NickName]', '[class*=creator-name]',
                    '[class*=user-name]', '[class*=account-name]',
                    '[class*=avatar] + div span', '[class*=avatar] ~ div span',
                    '[class*=sidebar] [class*=name]', '[class*=profile] [class*=name]',
                    '[class*=header] [class*=name]',
                ];
                for (var i = 0; i < selectors.length; i++) {
                    try {
                        var el = document.querySelector(selectors[i]);
                        if (el) {
                            var t = el.textContent || el.innerText || '';
                            t = t.trim().replace(/\s+/g, ' ');
                            if (t.length > 0 && t.length < 50 &&
                                t !== '快手' && t !== '创作者' &&
                                t !== 'Kuaishou' && t !== 'kuaishou') {
                                name = t; break;
                            }
                        }
                    } catch(e) {}
                }
            }
            try {
                var cookies = document.cookie.split(';');
                for (var i = 0; i < cookies.length; i++) {
                    var c = cookies[i].trim();
                    if (c.startsWith('userId=')) { uid = c.substring('userId='.length); break; }
                }
                if (!uid) {
                    for (var i = 0; i < cookies.length; i++) {
                        var c = cookies[i].trim();
                        if (c.startsWith('bUserId=')) { uid = c.substring('bUserId='.length); break; }
                    }
                }
            } catch(e) {}
            return JSON.stringify({ uid: uid, name: name, avatar: avatar });
        })()
        """));

    private async Task<string?> ExtractXhsUserAsync() => UnescapeJsonString(await _webView.InvokeScript("""
        (function() {
            var uid = '', name = '', avatar = '';
            try {
                var raw = localStorage.getItem('USER_INFO_FOR_BIZ');
                if (raw) {
                    var d = JSON.parse(raw);
                    uid = d.userId || '';
                    name = d.userName || '';
                    avatar = d.userAvatar || '';
                }
            } catch(e) {}
            if (!uid) {
                try {
                    var cookies = document.cookie.split(';');
                    for (var i = 0; i < cookies.length; i++) {
                        var c = cookies[i].trim();
                        if (c.startsWith('x-user-id-creator.xiaohongshu.com=')) {
                            uid = c.substring('x-user-id-creator.xiaohongshu.com='.length);
                            break;
                        }
                    }
                } catch(e) {}
            }
            if (!name) {
                try {
                    var el = document.querySelector('.account-name');
                    if (el) name = (el.textContent || el.innerText || '').trim();
                } catch(e) {}
            }
            if (!name) {
                try {
                    var el = document.querySelector('.name-box');
                    if (el) name = (el.textContent || el.innerText || '').trim();
                } catch(e) {}
            }
            if (!avatar) {
                try {
                    var el = document.querySelector('.avatar img');
                    if (el) avatar = el.src || '';
                } catch(e) {}
            }
            if (!avatar) {
                var imgs = document.querySelectorAll('img');
                for (var i = 0; i < imgs.length; i++) {
                    var src = imgs[i].src || '';
                    if (src.indexOf('img.xiaohongshu.com/avatar') > -1) { avatar = src; break; }
                }
            }
            return JSON.stringify({ uid: uid, name: name, avatar: avatar });
        })()
        """));

    private async Task<string?> ExtractDouyinUserAsync() => UnescapeJsonString(await _webView.InvokeScript("""
        (function() {
            var uid = '', name = '', avatar = '';
            try {
                var raw = localStorage.getItem('SLARDARdouyin_creator_longvideo');
                if (raw) {
                    var d = JSON.parse(raw);
                    uid = d.userId || '';
                }
            } catch(e) {}
            try {
                var avatarContainer = document.querySelector('[class*="container-"] [class*="avatar-"]');
                if (avatarContainer) {
                    var avatarImg = avatarContainer.querySelector('img');
                    if (avatarImg) avatar = avatarImg.src || '';
                }
            } catch(e) {}
            if (!avatar) {
                var imgs = document.querySelectorAll('img');
                for (var i = 0; i < imgs.length; i++) {
                    var src = imgs[i].src || '';
                    if (src.indexOf('avatar') > -1 || src.indexOf('douyinpic.com') > -1) {
                        avatar = src; break;
                    }
                }
            }
            try {
                var content = document.querySelector('[class*="container-"] [class*="content-"]');
                if (content) {
                    var header = content.querySelector('[class*="header-"]');
                    if (header) {
                        var left = header.querySelector('[class*="left-"]');
                        if (left) {
                            var nameEl = left.querySelector('[class*="name-"]');
                            if (nameEl) {
                                var t = (nameEl.textContent || nameEl.innerText || '').trim();
                                if (t.length > 0 && t.length < 50) name = t;
                            }
                        }
                    }
                    if (!name) {
                        var nameEl = content.querySelector('[class*="header-"] [class*="name-"]');
                        if (nameEl) {
                            var t = (nameEl.textContent || nameEl.innerText || '').trim();
                            if (t.length > 0 && t.length < 50) name = t;
                        }
                    }
                }
            } catch(e) {}
            if (!name) {
                try {
                    var allNameEls = document.querySelectorAll('[class*="name-"]');
                    for (var i = 0; i < allNameEls.length; i++) {
                        var el = allNameEls[i];
                        var parent = el.parentElement;
                        var skip = false;
                        while (parent) {
                            var pc = parent.className || '';
                            if (typeof pc === 'string' &&
                                (pc.indexOf('side-bar') > -1 || pc.indexOf('sidebar') > -1 ||
                                 pc.indexOf('menu') > -1 || pc.indexOf('nav') > -1 ||
                                 pc.indexOf('button') > -1)) {
                                skip = true; break;
                            }
                            parent = parent.parentElement;
                        }
                        if (skip) continue;
                        var t = (el.textContent || el.innerText || '').trim().replace(/\s+/g, ' ');
                        if (t.length > 0 && t.length < 50 && t !== '抖音' && t !== '创作者') {
                            name = t; break;
                        }
                    }
                } catch(e) {}
            }
            if (!name) {
                var selectors = [
                    '[class*=username]', '[class*=nickname]',
                    '[class*=NickName]', '[class*=creator-name]', '[class*=user-name]',
                    '[class*=account-name]',
                    '[class*=avatar] + span',
                    '[class*=avatar] ~ [class*=name]',
                    '[class*=header] [class*=name]',
                    '.side-bar [class*=name]',
                    'a[href*="/user/"] span'
                ];
                for (var i = 0; i < selectors.length; i++) {
                    try {
                        var el = document.querySelector(selectors[i]);
                        if (el) {
                            var t = el.textContent || el.innerText || '';
                            t = t.trim().replace(/\s+/g, ' ');
                            if (t.length > 0 && t.length < 50 && t !== '抖音' && t !== '创作者') {
                                name = t; break;
                            }
                        }
                    } catch(e) {}
                }
            }
            if (!uid) {
                try {
                    var uniqueEl = document.querySelector('[class*="unique_id-"]');
                    if (uniqueEl) {
                        var txt = (uniqueEl.textContent || '').trim();
                        var m = txt.match(/抖音号[：:]\s*(\S+)/);
                        if (m) uid = m[1];
                    }
                } catch(e) {}
            }
            return JSON.stringify({ uid: uid, name: name, avatar: avatar });
        })()
        """));

    // ═══ B站 / 头条：页面接口取账号信息与登录态 ═══
    //
    // InvokeScript/ExecuteScriptAsync **不会等待 Promise**（返回的是表达式值），所以不能直接
    // `return await fetch(...)`。沿用 CaptureEnvironmentSnapshotAsync 的「写 window 变量 + C# 轮询」
    // 手法：脚本里发起 fetch，结果写 window.__cf_user，C# 轮询 window.__cf_user_done。
    //
    // 两平台的账号信息都只能从页面接口拿（导航栏不显示昵称）：
    //   B站  → api.bilibili.com/x/web-interface/nav（页面自己就这么取；跨域 cookie 由浏览器带）
    //   头条 → /mp/agw/media/get_media_info（账号对象 media：id/name/avatar_url）

    private const string BilibiliUserInfoScript = """
        window.__cf_user = null; window.__cf_user_done = false;
        fetch('https://api.bilibili.com/x/web-interface/nav', { credentials: 'include' })
          .then(function(r) { return r.json(); })
          .then(function(d) {
              var x = (d && d.data) || {};
              var face = x.face || '';
              if (face.indexOf('//') === 0) face = 'https:' + face;
              window.__cf_user = JSON.stringify({
                  loggedIn: !!x.isLogin,
                  uid: x.mid ? String(x.mid) : '',
                  name: x.uname || '',
                  avatar: face
              });
              window.__cf_user_done = true;
          })
          .catch(function() { window.__cf_user_done = true; });
        'ok'
        """;

    /// <summary>
    /// 头条页面自带的账号快照 —— **首选来源**（同步读，无网络）。
    ///
    /// 头条号把登录账号写进页面全局：`window.Garfish.pgc_info`（xigua 页是 `window.Garr.pgc_info`），
    /// 头部账号卡就是从这里渲染的（见 2026-11-05 抓包：首页 bundle
    /// `var t=window.Garfish.pgc_info, user=t.user, media=t.media`；
    /// 头像 `user.https_avatar_url || media.https_display_avatar_url`，
    /// 昵称 `media.display_name || user.screen_name`）。
    ///
    /// 2026-11-05 用户实测：只走 `/mp/agw/media/get_media_info` 取不到昵称/头像（同步出来是
    /// 平台键 "toutiao"）—— 页面全局才是页面自己认的那一份，接口只作兜底。
    /// DOM 兜底再用账号卡的 `.auth-avator-name` / `.auth-avator-img`。
    /// </summary>
    private const string ToutiaoPageInfoScript = """
        (function() {
            var g = (window.Garfish && window.Garfish.pgc_info)
                 || (window.Garr && window.Garr.pgc_info) || null;
            var u = (g && g.user) || {};
            var m = (g && g.media) || {};
            var name = m.display_name || u.display_name || u.screen_name || m.name || '';
            var avatar = u.https_avatar_url || u.https_display_avatar_url
                      || m.https_avatar_url || m.https_display_avatar_url
                      || m.avatar_url || '';
            if (avatar.indexOf('//') === 0) avatar = 'https:' + avatar;
            var uid = (m.id !== undefined && m.id !== null) ? String(m.id)
                    : (u.id_str !== undefined && u.id_str !== null) ? String(u.id_str)
                    : (u.id !== undefined && u.id !== null) ? String(u.id) : '';
            if (!name) {
                try {
                    var el = document.querySelector('.auth-avator-name');
                    if (el) name = (el.textContent || el.innerText || '').trim();
                } catch(e) {}
            }
            if (!avatar) {
                try {
                    var img = document.querySelector('.auth-avator-img');
                    if (img && img.src) avatar = img.src;
                } catch(e) {}
            }
            if (!uid) {
                try {
                    var a = document.querySelector('a[href*="/c/user/"]');
                    var mm = a ? (a.getAttribute('href') || '').match(/\/c\/user\/(\d+)/) : null;
                    if (mm) uid = mm[1];
                } catch(e) {}
            }
            return JSON.stringify({ loggedIn: !!g, uid: uid, name: name, avatar: avatar });
        })()
        """;

    private const string ToutiaoUserInfoScript = """
        window.__cf_user = null; window.__cf_user_done = false;
        Promise.all([
            fetch('/mp/agw/media/user_login_status_api', { credentials: 'include' })
                .then(function(r) { return r.json(); }).catch(function() { return null; }),
            fetch('/mp/agw/media/get_media_info', { credentials: 'include' })
                .then(function(r) { return r.json(); }).catch(function() { return null; })
        ]).then(function(res) {
            var st = res[0] || {};
            var mi = res[1] || {};
            var m = mi.media || (mi.data && mi.data.media) || null;
            var loggedIn = !!(st.data && st.data.is_login) || !!m;
            var avatar = (m && (m.avatar_url || m.https_avatar_url || m.avatar_uri)) || '';
            if (avatar.indexOf('//') === 0) avatar = 'https:' + avatar;
            window.__cf_user = JSON.stringify({
                loggedIn: loggedIn,
                uid: (m && m.id) ? String(m.id) : '',
                name: (m && (m.name || m.display_name || m.screen_name)) || '',
                avatar: avatar
            });
            window.__cf_user_done = true;
        }).catch(function() { window.__cf_user_done = true; });
        'ok'
        """;

    /// <summary>执行同步 JS 并取回 JSON 字符串（页面不可用 / 非 JSON → null）</summary>
    private async Task<string?> InvokeJsonScriptAsync(string script)
    {
        try
        {
            var json = UnescapeJsonString(await _webView.InvokeScript(script));
            return string.IsNullOrWhiteSpace(json) || json == "null" ? null : json;
        }
        catch { return null; }
    }

    /// <summary>
    /// 执行「fetch 结果写 window 变量」的脚本并轮询取回结果 JSON 字符串。
    /// 页面不可用 / 超时 / fetch 失败 → null（调用方按「未知」处理，不据此拦提取）。
    /// </summary>
    private async Task<string?> InvokePageFetchAsync(string script, int timeoutMs = 4000)
    {
        try
        {
            await _webView.InvokeScript(script);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(150);
                if (await _webView.InvokeScript("window.__cf_user_done") == "true") break;
            }
            var json = UnescapeJsonString(await _webView.InvokeScript("window.__cf_user") ?? "");
            return string.IsNullOrWhiteSpace(json) || json == "null" ? null : json;
        }
        catch { return null; }
    }

    private async Task<bool?> ProbeBilibiliLoginAsync() =>
        ReadLoggedIn(await InvokePageFetchAsync(BilibiliUserInfoScript));

    private async Task<bool?> ProbeToutiaoLoginAsync()
    {
        // 页面全局有 pgc_info = 页面自己认定的登录态（最准，也最快）
        var page = await InvokeJsonScriptAsync(ToutiaoPageInfoScript);
        if (ReadLoggedIn(page) == true) return true;

        // 页面全局还没注好/没有时再问接口
        return ReadLoggedIn(await InvokePageFetchAsync(ToutiaoUserInfoScript));
    }

    private static bool? ReadLoggedIn(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("loggedIn", out var v)
                ? v.ValueKind == JsonValueKind.True
                : null;
        }
        catch { return null; }
    }

    private async Task<string?> ExtractBilibiliUserAsync()
    {
        var api = await InvokePageFetchAsync(BilibiliUserInfoScript);

        // nav 被风控/CORS 挡住时的兜底：DedeUserID 非 HttpOnly、头像在顶栏 <img>。
        // 昵称拿不到就留空（显示层回落平台名），别让整次提取失败。
        string? dom = null;
        try
        {
            dom = UnescapeJsonString(await _webView.InvokeScript("""
                (function() {
                    var uid = '', name = '', avatar = '';
                    try {
                        var cs = document.cookie.split(';');
                        for (var i = 0; i < cs.length; i++) {
                            var c = cs[i].trim();
                            if (c.indexOf('DedeUserID=') === 0) uid = c.substring(11);
                        }
                    } catch(e) {}
                    try {
                        var imgs = document.querySelectorAll('img');
                        for (var i = 0; i < imgs.length; i++) {
                            var src = imgs[i].src || '';
                            if (src.indexOf('hdslb.com/bfs/face') > -1) { avatar = src; break; }
                        }
                    } catch(e) {}
                    return JSON.stringify({ uid: uid, name: name, avatar: avatar });
                })()
                """));
        }
        catch { /* 页面不可用：只用接口那一路 */ }

        return MergeUserInfo(api, dom);
    }

    private async Task<string?> ExtractToutiaoUserAsync()
    {
        // 页面全局（Garfish.pgc_info）优先 —— 头部账号卡就是它渲染的；
        // 接口只作兜底（2026-11-05 用户实测：只走接口拿不到昵称/头像）。
        var page = await InvokeJsonScriptAsync(ToutiaoPageInfoScript);
        var api = await InvokePageFetchAsync(ToutiaoUserInfoScript);
        return MergeUserInfo(page, api);
    }

    /// <summary>合并两路账号信息 JSON（接口优先，缺口用兜底补）；两边都空返回 null</summary>
    private static string? MergeUserInfo(string? primary, string? fallback)
    {
        static (string? Uid, string? Name, string? Avatar) Read(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return (null, null, null);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string? Get(string name) =>
                    root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() : null;
                return (Get("uid"), Get("name"), Get("avatar"));
            }
            catch { return (null, null, null); }
        }

        var (uid1, name1, avatar1) = Read(primary);
        var (uid2, name2, avatar2) = Read(fallback);
        var uid = !string.IsNullOrWhiteSpace(uid1) ? uid1 : uid2;
        var name = !string.IsNullOrWhiteSpace(name1) ? name1 : name2;
        var avatar = !string.IsNullOrWhiteSpace(avatar1) ? avatar1 : avatar2;
        if (string.IsNullOrWhiteSpace(uid) && string.IsNullOrWhiteSpace(name)
            && string.IsNullOrWhiteSpace(avatar)) return null;
        return JsonSerializer.Serialize(new { uid, name, avatar });
    }

    // ═══ Cookie/存储提取(逐字移植) ═══

    private async Task<string?> ExtractLocalStorageAsync() => UnescapeJsonString(await _webView.InvokeScript("""
        (function() {
            var r = {};
            for (var i = 0; i < localStorage.length; i++) {
                try {
                    var key = localStorage.key(i);
                    var val = localStorage.getItem(key);
                    if (val != null) r[key] = val;
                } catch(e) {}
            }
            return JSON.stringify(r);
        })()
        """));

    private async Task<string?> ExtractAllCookiesAsync()
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var jsCookie = await _webView.InvokeScript("document.cookie");
            if (!string.IsNullOrWhiteSpace(jsCookie))
            {
                foreach (var part in jsCookie.Split(';'))
                {
                    var eq = part.IndexOf('=');
                    if (eq > 0)
                    {
                        var n = part[..eq].Trim();
                        var v = part[(eq + 1)..].Trim();
                        // 脏名过滤：不让转义污染的 cookie 名同步到服务端、再被注回档案（见 IsValidCookieName）
                        if (!string.IsNullOrWhiteSpace(n) && !merged.ContainsKey(n)
                            && IsValidCookieName(n))
                            merged[n] = v;
                    }
                }
            }
        }
        catch { }

        try
        {
            var cookieManager = _webView.TryGetCookieManager();
            if (cookieManager != null)
            {
                var getCookiesMethod = cookieManager.GetType().GetMethod("GetCookiesAsync")
                    ?? cookieManager.GetType().GetMethod("GetAllCookiesAsync");
                if (getCookiesMethod != null)
                {
                    var parameters = getCookiesMethod.GetParameters();
                    object?[] args = parameters.Length > 0
                        ? [_webView.Source?.ToString() ?? ""]
                        : [];
                    var task = (Task?)getCookiesMethod.Invoke(cookieManager, args);
                    if (task != null)
                    {
                        await task;
                        var result = task.GetType().GetProperty("Result")?.GetValue(task);
                        if (result is System.Collections.IEnumerable cookies)
                        {
                            foreach (var c in cookies)
                            {
                                var nameProp = c.GetType().GetProperty("Name");
                                var valueProp = c.GetType().GetProperty("Value");
                                if (nameProp != null && valueProp != null)
                                {
                                    var n = nameProp.GetValue(c)?.ToString();
                                    var v = valueProp.GetValue(c)?.ToString();
                                    // 脏名过滤（同 document.cookie 一路）：CookieManager 里也会躺着历史污染项
                                    if (!string.IsNullOrWhiteSpace(n) && v != null
                                        && IsValidCookieName(n))
                                        merged[n] = v;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { /* CookieManager 不可用时仅 document.cookie */ }

        if (_platformKey == PlatformNames.Toutiao)
        {
            // 只记名字不记值 —— 排障用（例如确认某个 cookie 到底在不在会话里）
            Log.Debug("[Cookie] toutiao 合并后 cookie 名: {Names}", string.Join(",", merged.Keys));
        }

        return merged.Count > 0
            ? string.Join("; ", merged.Select(kv => $"{kv.Key}={kv.Value}"))
            : null;
    }

    // ═══ 环境指纹快照(服务端发布 HTTP 对齐 UA/sec-ch-ua) ═══

    private async Task<PlatformEnvironmentSnapshot?> CaptureEnvironmentSnapshotAsync()
    {
        try
        {
            await _webView.InvokeScript("window.__env_snap_done = false; window.__env_snap_result = null;");
            await _webView.InvokeScript("""
                (function() {
                    var out = {
                        userAgent: navigator.userAgent || '',
                        secChUa: '',
                        secChUaMobile: '?0',
                        secChUaPlatform: '"Windows"',
                        screenWidth: String(screen.width || 0),
                        screenHeight: String(screen.height || 0),
                        browserLanguage: navigator.language || '',
                        browserPlatform: navigator.platform || '',
                        timezoneName: ''
                    };
                    try {
                        out.timezoneName = Intl.DateTimeFormat().resolvedOptions().timeZone || '';
                    } catch(e) {}
                    var finish = function() {
                        window.__env_snap_result = JSON.stringify(out);
                        window.__env_snap_done = true;
                    };
                    try {
                        if (navigator.userAgentData && navigator.userAgentData.getHighEntropyValues) {
                            navigator.userAgentData.getHighEntropyValues(['brands', 'mobile', 'platform'])
                                .then(function(ua) {
                                    var brands = ua.brands || [];
                                    out.secChUa = brands.map(function(b) {
                                        return '"' + (b.brand || '') + '";v="' + (b.version || '') + '"';
                                    }).join(', ');
                                    out.secChUaMobile = ua.mobile ? '?1' : '?0';
                                    out.secChUaPlatform = '"' + (ua.platform || 'Windows') + '"';
                                    finish();
                                })
                                .catch(finish);
                        } else {
                            finish();
                        }
                    } catch(e) { finish(); }
                })()
                """);
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(200);
                var done = await _webView.InvokeScript("window.__env_snap_done");
                if (done == "true") break;
            }
            var json = UnescapeJsonString(await _webView.InvokeScript("window.__env_snap_result") ?? "");
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<PlatformEnvironmentSnapshot>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    // ═══ 工具 ═══

    private static string? UnescapeJsonString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        try
        {
            // InvokeScript 返回的是 JSON 字符串字面量(含转义引号)
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                return System.Text.Json.JsonSerializer.Deserialize<string>(value);
        }
        catch { }
        return value;
    }

    private static string? ExtractCookieValue(string? cookie, string name)
    {
        if (string.IsNullOrEmpty(cookie)) return null;
        var prefix = name + "=";
        foreach (var part in cookie.Split(';'))
        {
            var p = part.Trim();
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return p[prefix.Length..];
        }
        return null;
    }
}
