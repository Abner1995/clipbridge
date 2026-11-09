using System.Text.Json;
using Avalonia.Controls;
using CreatFlow.Platforms.Download.Extractors;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 抖音「网页代理发布」宿主 —— 一个账号标签的档案页即一个宿主，
/// 由 <see cref="Views.DockTabHostView"/> 实现并注册（与小红书 x-s 签名宿主同款机制）。
///
/// 为什么抖音发布必须走页面而不是纯 HTTP：<c>bd_ticket_guard</c> 的原生票据由页面里
/// 的 SDK 维护，C# 侧只能靠本地 ECDSA 复算（<c>BdTicketSigner</c>），而设备认证等级
/// 不足时服务端会直接拒绝那份复算票据（Key-Sign-Result=2，见 PublishPipeline 403 注释）。
/// 页面内发起 XHR 则天然带上浏览器自己那份，这是唯一可靠路径。
/// </summary>
public interface IDouyinWebHost
{
    /// <summary>
    /// 在宿主页面里发 CreateWork 请求。返回原始 <c>{ok,status,body,headers}</c> JSON；
    /// null = 宿主不可用（页面未加载 / 脚本异常），调用方退回 HTTP 兜底。
    /// </summary>
    Task<string?> ExecuteCreateWorkAsync(string bodyJson, string csrfToken, CancellationToken ct);

    /// <summary>
    /// 在宿主里打开作品页并抓取数据（视频下载用）—— 抖音的 detail 接口受 Argus/uifid
    /// 保护，只有页面自己的上下文能拿到数据。返回页面数据 JSON
    /// <c>{router, videoSrc, title}</c>；null = 宿主不可用 / 页面没有数据。
    /// </summary>
    Task<string?> ExecuteExtractPageAsync(string url, CancellationToken ct);
}

/// <summary>
/// 抖音网页宿主注册表 + 调度 —— 与 <see cref="XhsSignerService"/> 逐条同构：
///
///   1. 各账号标签宿主在 WebView 就绪时按 sessionId 注册自己；
///   2. <see cref="PublishPipeline"/> 按「平台记录所属会话」取宿主 ——
///      **不是**老版那样全局只有一个 <c>HiddenInstance</c>（多账号下会用 A 账号的
///      浏览器发 B 账号的作品，见设计文档缺陷 L4 同源问题）；
///   3. 该会话没开过标签 → 返回 null，管线退回 HTTP 兜底（可能被风控拦，但不会发错账号）。
///
/// 静态类：与 ApiClient / XhsSignerService 同款（App 层无参单例）。
/// </summary>
public static class DouyinWebExecutor
{
    /// <summary>单次 CreateWork 预算 —— 页面内 XHR + 轮询，15 秒是发布请求的正常上限</summary>
    private static readonly TimeSpan ExecuteTimeout = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<Guid, IDouyinWebHost> _hosts = [];
    private static readonly object _lock = new();

    /// <summary>
    /// 引导用户完成身份验证的落点 —— 发布页。
    ///
    /// 为什么不是首页：验证由**发布动作**触发（平台帮助中心/社区口径：在创作者中心手动发布一条
    /// 视频完成短信验证后，接口才会放行）。落在发布页，用户离「发一条」只差一次点击。
    /// </summary>
    public const string PublishPageUrl = "https://creator.douyin.com/creator-micro/content/upload";

    /// <summary>请求用户完成身份验证的回调（App 开壳时挂：sessionId → 亮出该账号页面）。
    /// null = 不引导，管线只回报「需要验证」的失败原因。</summary>
    public static Func<Guid, CancellationToken, Task<bool>>? VerifyRequester { get; set; }

    /// <summary>本次运行已引导过验证的账号（每账号只抢一次页面，见 <see cref="RequestVerifyAsync"/>）</summary>
    private static readonly HashSet<Guid> _verifyPrompted = [];

    // ═══ 注册（标签宿主调用；UI 线程） ═══

    public static void Register(Guid sessionId, IDouyinWebHost host)
    {
        lock (_lock) _hosts[sessionId] = host;
        Log.Debug("[DouyinWeb] 会话 {Session} 注册网页代理宿主", sessionId);
    }

    public static void Unregister(Guid sessionId)
    {
        lock (_lock) _hosts.Remove(sessionId);
        Log.Debug("[DouyinWeb] 会话 {Session} 注销网页代理宿主", sessionId);
    }

    /// <summary>是否有该会话的可用宿主（管线据此决定要不要先等页面就绪）。</summary>
    public static bool Has(Guid? sessionId)
    {
        lock (_lock) return sessionId is { } sid && _hosts.ContainsKey(sid);
    }

    // ═══ 入口 ═══

    /// <summary>
    /// 执行 CreateWork。宿主缺失/超时/异常一律返回 null，**不抛** ——
    /// 调用方按「网页路径不可用」退回 HTTP，发布流程不该被宿主问题打断。
    /// </summary>
    public static Task<string?> ExecuteCreateWorkAsync(
        Guid? sessionId, string bodyJson, string csrfToken, CancellationToken ct)
    {
        var host = Resolve(sessionId);
        if (host is null) return Task.FromResult<string?>(null);
        return InvokeUiAsync(host, bodyJson, csrfToken, ct);
    }

    /// <summary>
    /// 借账号页面打开作品页抓数据（视频下载）。宿主缺失/超时返回 null —— 由调用方
    /// 决定是回退 HTTP 还是报「请打开抖音账号窗口」。
    /// </summary>
    public static async Task<string?> ExecuteExtractPageAsync(
        Guid? sessionId, string url, CancellationToken ct)
    {
        var host = Resolve(sessionId);
        if (host is null) return null;

        Task<string?> job;
        try
        {
            job = Dispatcher.UIThread.InvokeAsync(() => host.ExecuteExtractPageAsync(url, ct));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DouyinWeb] UI 线程调度失败（下载抓取）");
            return null;
        }

        var done = await Task.WhenAny(job, Task.Delay(TimeSpan.FromSeconds(50), ct));
        if (done != job)
        {
            Log.Warning("[DouyinWeb] 下载页面抓取超时 50s");
            return null;
        }

        try { return await job; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DouyinWeb] 下载页面抓取异常");
            return null;
        }
    }

    /// <summary>
    /// 请求用户完成抖音身份验证（passport authlv 检查 / STS status_code=8）。
    ///
    /// 每次运行**每个账号只引导一次**：批量发布里同一账号的后续行再失败，只回报文案、
    /// 不再抢页面（否则 12 条视频会把用户从队列页拽走 12 次）。
    /// 返回 true = 已经把该账号的窗口亮出来（文案据此改成「已打开…点重试」）。
    /// </summary>
    public static async Task<bool> RequestVerifyAsync(Guid? sessionId, CancellationToken ct)
    {
        if (sessionId is not { } sid || VerifyRequester is not { } req) return false;

        lock (_lock)
        {
            if (!_verifyPrompted.Add(sid)) return false;
        }

        try
        {
            if (await req(sid, ct)) return true;
            // 没引导成（账号不在坞标签里等）—— 允许下一次失败再试
            lock (_lock) _verifyPrompted.Remove(sid);
            return false;
        }
        catch (OperationCanceledException)
        {
            lock (_lock) _verifyPrompted.Remove(sid);
            throw;
        }
        catch (Exception ex)
        {
            lock (_lock) _verifyPrompted.Remove(sid);
            Log.Warning(ex, "[DouyinWeb] 引导身份验证失败");
            return false;
        }
    }

    /// <summary>
    /// UI 线程调度（InvokeScript 只能在 UI 线程）。带超时兜底：
    /// 宿主卡住时不能让发布流程悬着 —— 超时即按「不可用」收场，由 HTTP 兜底接手。
    /// </summary>
    private static async Task<string?> InvokeUiAsync(
        IDouyinWebHost host, string bodyJson, string csrfToken, CancellationToken ct)
    {
        Task<string?> job;
        try
        {
            job = Dispatcher.UIThread.InvokeAsync(() =>
                host.ExecuteCreateWorkAsync(bodyJson, csrfToken, ct));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DouyinWeb] UI 线程调度失败");
            return null;
        }

        var done = await Task.WhenAny(job, Task.Delay(ExecuteTimeout, ct));
        if (done != job)
        {
            Log.Warning("[DouyinWeb] 网页代理发布超时 {Seconds}s —— 退回 HTTP 兜底",
                ExecuteTimeout.TotalSeconds);
            _ = job.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }
        try { return await job; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DouyinWeb] 网页代理发布异常");
            return null;
        }
    }

    /// <summary>
    /// 精确会话命中。**不做**「退而求其次用别的账号标签」——
    /// 老版用全局单例 HiddenInstance，多账号时会拿 A 账号的浏览器发 B 账号的作品。
    /// 宁可不发（退回 HTTP，失败可重试），也不能发错账号。
    /// </summary>
    private static IDouyinWebHost? Resolve(Guid? sessionId)
    {
        lock (_lock)
        {
            if (sessionId is { } sid && _hosts.TryGetValue(sid, out var exact)) return exact;
        }
        if (sessionId is { } missing)
            Log.Debug("[DouyinWeb] 会话 {Session} 无网页代理宿主 —— 走 HTTP 兜底", missing);
        return null;
    }
}

/// <summary>
/// 抖音发布页代理 —— 在某个账号标签的 WebView2 档案页里执行 CreateWork XHR。
///
/// 自 CreatFlow.App/Views/WebViewLoginWindow.axaml.cs 的 <c>ExecuteCreateWorkAsync</c> 移植
/// （2026-09-17）。与老版的差别：
///   · 老版挂在「全局唯一隐藏窗」上（多账号串号风险），本实现挂**本会话自己的标签**；
///   · 老版假定页面永远停在 creator.douyin.com，本实现**显式校验来源**，
///     不在该域时临时导航过去、发完再退回用户原页面（同 XhsPageSigner 的借页手法）。
///
/// 线程约定：InvokeScript / Navigate 必须在 UI 线程调用（由 <see cref="DouyinWebExecutor"/> 保证）。
/// </summary>
public sealed class DouyinPageExecutor : IDouyinWebHost
{
    /// <summary>发布接口所在域 —— 页内 XHR 用相对路径，必须站在这个域上才发得对</summary>
    private const string CreatorHost = "creator.douyin.com";

    /// <summary>兜底落点：发布页（页面自带 bd_ticket_guard SDK）—— 与验证引导共用同一个地址</summary>
    private const string PublishPageUrl = DouyinWebExecutor.PublishPageUrl;

    private readonly NativeWebView _webView;

    /// <summary>同页串行 —— 并发注入的 __cw_* 全局变量会互相覆盖</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>借页前的用户页面地址（发完恢复）</summary>
    private Uri? _restoreUri;

    public DouyinPageExecutor(NativeWebView webView) => _webView = webView;

    public async Task<string?> ExecuteCreateWorkAsync(string bodyJson, string csrfToken, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!await EnsureOnCreatorHostAsync(ct)) return null;
            return await ExecuteOnceAsync(bodyJson, csrfToken, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DouyinWeb] 页内 CreateWork 异常");
            return null;
        }
        finally
        {
            RestorePage();
            _gate.Release();
        }
    }

    /// <summary>
    /// 借页打开作品页并抓数据（视频下载）。
    ///
    /// 三步，按可靠性排：
    ///  1. **页面自签的 `aweme/detail`** —— 唯一能拿到 `play_addr` 的路。`play_addr` 是
    ///     无水印的主 rendition，`download_addr` 才是带「抖音号 + logo + 搜索昵称」水印的
    ///     下载版（2026-10-23 实测同一视频：play 648,092,889 字节 / download 694,814,228 字节，
    ///     逐帧比对确认后者右下角多一块水印）。
    ///     ⚠️ **不要自己塞 a_bogus**：页面的 secsdk-strategy 拦截
    ///     `XMLHttpRequest.prototype.send` 会自动追加 a_bogus + timestamp +
    ///     x-secsdk-web-signature；我们自己算的 a_bogus 会被判 `403 Sign Invalid`（同日实测）。
    ///  2. 页面已有的 `_ROUTER_DATA`；
    ///  3. `<video>` currentSrc / 导航期间抓到的媒体请求。
    ///
    /// 已知坑：档案可能被 A/B 导到 `www-hj.douyin.com`，在那个 host 上同样的 XHR 返回
    /// **200 空 body** —— detail 为空不是异常，要能退回 2/3（不能当失败处理）。
    /// </summary>
    public async Task<string?> ExecuteExtractPageAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = _webView.Source;
            if (current is not null) _restoreUri = current;

            // 页面自己的媒体请求是**最可靠的直链来源**（DOM 里的 <video> 可能是 blob:）。
            // 导航期间抓第一个抖音 CDN 的请求 URL，作为 videoSrc 的兜底。
            string? mediaUrl = null;
            void OnRequest(object? sender, EventArgs e)
            {
                if (e is not WebResourceRequestedEventArgs args) return;
                var u = args.Request?.Uri?.ToString();
                if (string.IsNullOrWhiteSpace(u)) return;
                if (u.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return;

                var looksLikeMedia =
                    u.Contains("douyinvod.com", StringComparison.OrdinalIgnoreCase)
                    || u.Contains("/video/tos/", StringComparison.OrdinalIgnoreCase)
                    || (u.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
                        && !u.Contains("cover", StringComparison.OrdinalIgnoreCase));
                if (looksLikeMedia) mediaUrl ??= u;
            }

            _webView.WebResourceRequested += OnRequest;
            try
            {
                await NavigateAndWaitAsync(url, TimeSpan.FromSeconds(20), ct);
                await WaitPageSettledAsync(TimeSpan.FromSeconds(6), ct);

                // ① 页面自签的 aweme/detail —— 无水印源的唯一来源
                var videoId = ExtractVideoId(url)
                              ?? ExtractVideoId(_webView.Source?.ToString());
                var detail = videoId is null ? null : await FetchDetailAsync(videoId, ct);

                // ②③ 页面已有数据（detail 失败/返回空时的兜底）
                var page = await TryInvokeScriptAsync(ExtractPageScript);

                var payload = ComposePayload(page, detail, mediaUrl, _webView.Source?.ToString());
                Log.Debug("[DouyinWeb] 下载页面抓取完成，payload {Len} 字节，detail={Detail}，media={Media}",
                    payload?.Length ?? 0, detail is null ? "n/a" : (detail.Contains("\"info\":\"") ? "有" : "空"),
                    mediaUrl);
                return payload;
            }
            finally
            {
                _webView.WebResourceRequested -= OnRequest;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DouyinWeb] 下载页面抓取失败");
            return null;
        }
        finally
        {
            RestorePage();
            _gate.Release();
        }
    }

    /// <summary>
    /// 页内发 `aweme/detail` XHR（**不带 a_bogus**，交给页面 secsdk-strategy 补）。
    /// 返回 <c>{status,len,info,raw}</c>：info = aweme_detail 对象的 JSON 字符串
    /// （空 = 被拒/空 body，raw 里是响应的前一段，便于分辨「200 空 body」还是异常）。
    /// </summary>
    private async Task<string?> FetchDetailAsync(string videoId, CancellationToken ct)
    {
        var query = DouyinDownloadExtractor.BuildDetailQuery(videoId);
        await _webView.InvokeScript("window.__dy_q = " + JsonSerializer.Serialize(query) + ";");
        await _webView.InvokeScript("window.__dy_detail = null; window.__dy_detail_done = false;");
        await _webView.InvokeScript(DetailXhrScript);

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500, ct);
            try
            {
                if (Unescape(await _webView.InvokeScript("window.__dy_detail_done")) == "true")
                    return Unescape(await _webView.InvokeScript("window.__dy_detail"));
            }
            catch { /* 页面在跳转 —— 下一轮 */ }
        }

        Log.Debug("[DouyinWeb] 页内 aweme/detail 10 秒无响应");
        return null;
    }

    /// <summary>
    /// 等页面进入 `complete`。不拿 `<video>.currentSrc` 判就绪 —— 还没出片时它是
    /// `lf-douyin-pc-web.douyinstatic.com/.../uuu_265.mp4` 这个**静态占位**（2026-10-23 实测），
    /// 提前发 detail 会撞上页面还在 `interactive` 的阶段。
    /// </summary>
    private async Task WaitPageSettledAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (Unescape(await _webView.InvokeScript("String(document.readyState)")) == "complete")
                    return;
            }
            catch { /* 还在导航 */ }
            await Task.Delay(400, ct);
        }
    }

    private async Task<string?> TryInvokeScriptAsync(string script)
    {
        try { return Unescape(await _webView.InvokeScript(script)); }
        catch (Exception ex)
        {
            Log.Debug(ex, "[DouyinWeb] 页内脚本执行失败（页面未就绪）");
            return null;
        }
    }

    /// <summary>
    /// 页内抓取脚本：优先 `_ROUTER_DATA`（信息全），退而求其次取 `&lt;video&gt;` 的 currentSrc。
    /// 不发任何网络请求 —— 只用页面已经拿到的数据。
    /// </summary>
    private const string ExtractPageScript = """
        (function() {
            var out = { router: null, videoSrc: null, title: '' };
            try { if (window._ROUTER_DATA) out.router = JSON.stringify(window._ROUTER_DATA); } catch (e) {}
            try { if (!out.router && window.__INITIAL_STATE__) out.router = JSON.stringify(window.__INITIAL_STATE__); } catch (e) {}
            try {
                var v = document.querySelector('video');
                if (v) out.videoSrc = v.currentSrc || v.src || '';
            } catch (e) {}
            try { out.title = document.title || ''; } catch (e) {}
            return JSON.stringify(out);
        })()
        """;

    /// <summary>
    /// 页内发 `aweme/detail`。**故意不带 a_bogus** —— 页面 secsdk-strategy 会拦截
    /// `XMLHttpRequest.prototype.send` 自动补 a_bogus + timestamp + x-secsdk-web-signature
    /// （2026-10-23 实测：自算 a_bogus → 403 Sign Invalid；不签名交给页面 → 200 完整数据）。
    /// </summary>
    private const string DetailXhrScript = """
        (function() {
            window.__dy_detail = null;
            window.__dy_detail_done = false;
            try {
                var xhr = new XMLHttpRequest();
                xhr.open('GET', '/aweme/v1/web/aweme/detail/?' + (window.__dy_q || ''), true);
                xhr.onload = function() {
                    var out = { status: xhr.status, len: 0, info: null, raw: '' };
                    try {
                        var t = xhr.responseText || '';
                        out.len = t.length;
                        var d = JSON.parse(t);
                        if (d && d.aweme_detail) out.info = JSON.stringify(d.aweme_detail);
                        else out.raw = t.substring(0, 300);
                    } catch (e) { out.raw = 'parse: ' + e; }
                    window.__dy_detail = JSON.stringify(out);
                    window.__dy_detail_done = true;
                };
                xhr.onerror = function() {
                    window.__dy_detail = JSON.stringify({ status: 0, len: 0, info: null, raw: 'XHR error' });
                    window.__dy_detail_done = true;
                };
                xhr.send();
            } catch (e) {
                window.__dy_detail = JSON.stringify({ status: 0, len: 0, info: null, raw: String(e) });
                window.__dy_detail_done = true;
            }
        })()
        """;

    /// <summary>
    /// 汇总成给 extractor 的 payload：
    /// <c>{ router, videoSrc, title, pageUrl, detail, detailStatus, detailLen, detailRaw }</c>。
    ///
    /// `videoSrc` 的取舍：DOM 的 currentSrc 常是 `blob:` 或静态占位
    /// （`lf-douyin-pc-web.douyinstatic.com/.../uuu_265.mp4`），这时用导航期间抓到的
    /// 媒体请求覆盖 —— 「http 开头」不足以说明它是真流（2026-10-23 踩过）。
    /// </summary>
    private static string? ComposePayload(
        string? pageJson, string? detailJson, string? mediaUrl, string? pageUrl)
    {
        string? router = null, videoSrc = null, title = null;
        if (!string.IsNullOrWhiteSpace(pageJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(pageJson);
                var root = doc.RootElement;
                router = GetStr(root, "router");
                videoSrc = GetStr(root, "videoSrc");
                title = GetStr(root, "title");
            }
            catch (JsonException ex)
            {
                Log.Debug(ex, "[DouyinWeb] 页面脚本返回的不是 JSON，按缺省处理");
            }
        }

        string? detailInfo = null, detailRaw = null;
        int? detailStatus = null;
        long detailLen = 0;
        if (!string.IsNullOrWhiteSpace(detailJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(detailJson);
                var root = doc.RootElement;
                detailInfo = GetStr(root, "info");
                detailRaw = GetStr(root, "raw");
                detailStatus = root.TryGetProperty("status", out var s) && s.TryGetInt32(out var sv) ? sv : 0;
                detailLen = root.TryGetProperty("len", out var l) && l.TryGetInt64(out var lv) ? lv : 0;
            }
            catch (JsonException ex)
            {
                Log.Debug(ex, "[DouyinWeb] detail 结果不是 JSON");
            }
        }

        var srcIsPlaceholder =
            string.IsNullOrWhiteSpace(videoSrc)
            || videoSrc.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            || videoSrc.Contains("douyinstatic.com", StringComparison.OrdinalIgnoreCase);
        if (srcIsPlaceholder && !string.IsNullOrWhiteSpace(mediaUrl)) videoSrc = mediaUrl;

        if (string.IsNullOrWhiteSpace(router)
            && string.IsNullOrWhiteSpace(videoSrc)
            && string.IsNullOrWhiteSpace(detailInfo))
            return null;

        return JsonSerializer.Serialize(new
        {
            router,
            videoSrc,
            title,
            pageUrl,
            detail = detailInfo,
            detailStatus,
            detailLen,
            detailRaw,
        });
    }

    private static string? GetStr(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>`https://www.douyin.com/video/{id}` → id</summary>
    private static string? ExtractVideoId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = VideoIdPattern.Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static readonly System.Text.RegularExpressions.Regex VideoIdPattern = new(
        @"/(?:video|share/video)/(\d{6,})",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>借了页 → 导航回用户原页面（失败也不抛：发布的副作用不该污染调用方）</summary>
    private void RestorePage()
    {
        if (_restoreUri == null) return;
        try { _webView.Navigate(_restoreUri); }
        catch (Exception ex) { Log.Warning(ex, "[DouyinWeb] 恢复原页面失败"); }
        _restoreUri = null;
    }

    /// <summary>
    /// 确保页面站在 creator.douyin.com 上。不在就临时导航到发布页（同 XhsPageSigner）。
    /// 直接用别的域发相对路径 XHR 会打到那个域的 /web/api/... —— 静默 404，
    /// 表现出来是「网页路径也没成功」，排查时极难定位。
    /// </summary>
    private async Task<bool> EnsureOnCreatorHostAsync(CancellationToken ct)
    {
        var current = _webView.Source;
        if (current is not null
            && current.Host.EndsWith(CreatorHost, StringComparison.OrdinalIgnoreCase))
            return true;

        // 页面没加载过（InvokeScript 会抛）—— 先等一次导航完成
        if (!await WaitPageLoadedAsync(TimeSpan.FromSeconds(20), ct))
        {
            Log.Warning("[DouyinWeb] 等待页面加载超时（{Url}）", current);
            return false;
        }
        current = _webView.Source;
        if (current is not null
            && current.Host.EndsWith(CreatorHost, StringComparison.OrdinalIgnoreCase))
            return true;

        if (current is not null) _restoreUri = current;
        Log.Information("[DouyinWeb] 当前页（{Back}）不在 {Host}，临时导航发布页", current, CreatorHost);
        await NavigateAndWaitAsync(PublishPageUrl, TimeSpan.FromSeconds(20), ct);

        var now = _webView.Source;
        if (now is not null && now.Host.EndsWith(CreatorHost, StringComparison.OrdinalIgnoreCase))
            return true;

        Log.Warning("[DouyinWeb] 导航后仍未落在 {Host}（{Url}）", CreatorHost, now);
        return false;
    }

    /// <summary>
    /// 页内 XHR。返回 <c>{ok,status,body,headers}</c>；15 秒轮询不到结果即返回 null。
    /// 头部要带出来 —— 风控拒绝的原因只长在响应头里（<c>Bd-Ticket-Guard-Key-Sign-Result</c> /
    /// <c>x-tt-verify-passport-decision</c>）。
    /// </summary>
    private async Task<string?> ExecuteOnceAsync(string bodyJson, string csrfToken, CancellationToken ct)
    {
        await _webView.InvokeScript("window.__cw_body = " + JsonSerializer.Serialize(bodyJson) + ";");
        await _webView.InvokeScript("window.__cw_csrf = " + JsonSerializer.Serialize(csrfToken) + ";");
        await _webView.InvokeScript("window.__cw_done = false; window.__cw_result = null;");

        await _webView.InvokeScript("""
            (function() {
                var xhr = new XMLHttpRequest();
                xhr.open('POST', '/web/api/media/aweme/create_v2/?' +
                    'cookie_enabled=true&screen_width=1920&screen_height=1080' +
                    '&browser_language=zh-CN&browser_platform=Win32' +
                    '&browser_name=Mozilla&browser_version=5.0%20(Windows%20NT%2010.0;%20Win64;%20x64)' +
                    '&browser_online=true&timezone_name=Asia/Shanghai' +
                    '&aid=1128&support_h265=1&read_aid=2906',
                    true);
                xhr.setRequestHeader('Content-Type', 'application/json');
                xhr.setRequestHeader('X-Secsdk-Csrf-Token', window.__cw_csrf || '');
                xhr.onload = function() {
                    var respHeaders = '';
                    try { respHeaders = xhr.getAllResponseHeaders() || ''; } catch(e) {}
                    window.__cw_result = JSON.stringify({
                        ok: xhr.status >= 200 && xhr.status < 300,
                        status: xhr.status,
                        headers: respHeaders,
                        body: xhr.responseText
                    });
                    window.__cw_done = true;
                };
                xhr.onerror = function() {
                    window.__cw_result = JSON.stringify({ ok: false, status: 0, body: 'XHR error' });
                    window.__cw_done = true;
                };
                xhr.send(window.__cw_body || '{}');
            })()
            """);

        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500, ct);
            if (Unescape(await _webView.InvokeScript("window.__cw_done")) == "true")
                return Unescape(await _webView.InvokeScript("window.__cw_result"));
        }

        Log.Warning("[DouyinWeb] 页内 CreateWork 15 秒无响应");
        return null;
    }

    // ═══ 导航与工具 ═══

    private async Task<bool> WaitPageLoadedAsync(TimeSpan timeout, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var ready = Unescape(await _webView.InvokeScript("String(document.readyState)"));
                if (!string.IsNullOrEmpty(ready)) return true;
            }
            catch { /* 页面未加载 —— 等下一次导航完成 */ }

            var tcs = new TaskCompletionSource();
            void Handler(object? s, EventArgs e) => tcs.TrySetResult();
            _webView.NavigationCompleted += Handler;
            try { await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)); }
            catch (OperationCanceledException) { return false; }
            finally { _webView.NavigationCompleted -= Handler; }
        }
        return false;
    }

    private async Task NavigateAndWaitAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource();
        void Handler(object? s, EventArgs e) => tcs.TrySetResult();

        _webView.NavigationCompleted += Handler;
        try
        {
            _webView.Navigate(new Uri(url));
            await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
        }
        catch (Exception ex) { Log.Warning(ex, "[DouyinWeb] 导航失败 {Url}", url); }
        finally { _webView.NavigationCompleted -= Handler; }
    }

    /// <summary>InvokeScript 返回 JSON 字符串字面量（含转义引号）—— 解一层。</summary>
    private static string? Unescape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        try
        {
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                return JsonSerializer.Deserialize<string>(value);
        }
        catch { }
        return value;
    }
}
