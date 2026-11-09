using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Platform;
using CreatFlow.Platforms.Douyin;
using CreatFlow.Platforms.Download;
using CreatFlow.Platforms.Download.Extractors;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 抖音「无水印源」探针 —— `--probe-douyin &lt;url|id&gt;` 运行。
///
/// **诊断工具，不进业务路径**：答完问题就可以删（或留着做线上排障）。
///
/// 要回答的问题（2026-10-23）：抖音下载产物带**服务端烧录**水印 —— 右下角「抖音音符 logo +
/// 抖音号：xxx + 🔍 昵称」、右边缘竖排「该行为存在危险请勿模仿」。而三条纯 HTTP 解析路径
/// 此时全部失效（detail 403 Argus / iteminfo 返回 0 字节 body / 分享页 `_ROUTER_DATA` 里
/// 已经没有 `play_addr`），只剩浏览器 `<video>` 兜底在跑 —— 那是「下载版」rendition。
///
/// 所以探针要看的是：**在页面上下文里 fetch `aweme/detail` 能不能拿到不带水印的 `play_addr`**。
/// 依据：Argus 拦的是我们从 C# 侧复刻的请求（缺 uifid），页面自己发的请求天然带着它；
/// a_bogus 我们可以纯算（<see cref="ABogusGenerator"/>），缺的只有 uifid —— 页面正好有。
///
/// 产出 `%TEMP%\douyin_probe.json`（同时写一行摘要进日志）：
///   · play_addr / play_addr_h264 / play_addr_265 / download_addr 各自全部 url_list；
///   · bit_rate[] 每一档的 gear / 码率 / 分辨率 / 地址（顺带看能不能拿回高码率档）；
///   · 页面 `performance` 里已经跑过的媒体请求 —— 播放器实际吃的是哪条流；
///   · 失败时回原始响应前缀 + 顶层字段名，便于分辨「风控」还是「签名格式变了」。
///
/// ⚠️ 实测水印要拿**短视频**（30~60 秒）—— 长视频每种候选地址都整份下下来太贵；
///    探针只负责把候选地址列全，下载与抽帧交给外部脚本。
/// ⚠️ 桌面端必须先退出：WebView2 档案目录被占用时环境创建会失败。
/// </summary>
public static class DouyinDetailProbe
{
    private static readonly Regex IdPathPattern = new(
        @"/(?:video|share/video|note)/(\d{6,})", RegexOptions.Compiled);

    private static readonly Regex IdQueryPattern = new(
        @"[?&](?:modal_id|aweme_id|item_id)=(\d{6,})", RegexOptions.Compiled);

    private static readonly TimeSpan NavigateTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DetailTimeout = TimeSpan.FromSeconds(25);

    /// <summary>入口（由 App.OnFrameworkInitializationCompleted 在探针模式下调用；UI 线程）。</summary>
    public static async Task<int> RunAsync(string target, Guid? wantSession)
    {
        var videoId = ExtractVideoId(target);
        if (videoId is null)
        {
            Log.Error("[DouyinProbe] 无法从「{Target}」识别作品 ID", target);
            return 2;
        }

        var session = ResolveSession(wantSession);
        if (session is null)
        {
            Log.Error("[DouyinProbe] 没找到抖音账号档案 —— 先正常启动软件、登录抖音账号并点开一次账号窗口");
            return 3;
        }

        var (sessionId, profileDir, label) = session.Value;
        Log.Information("[DouyinProbe] 作品 {Id}｜账号 {Label}（{Session}）｜档案 {Dir}",
            videoId, label, sessionId, profileDir);

        if (!Directory.Exists(profileDir))
        {
            Log.Warning("[DouyinProbe] 档案目录不存在（该账号没打开过？）—— 大概率没有登录态：{Dir}", profileDir);
        }

        var window = new Window
        {
            Title = $"抖音无水印源探针 — {videoId}",
            Width = 1000,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var view = new NativeWebView();
        window.Content = view;

        // 档案注入必须早于任何导航（同 DockTabHostView / PlatformLoginWindow 的时机约定）
        view.EnvironmentRequested += (_, args) =>
        {
            if (args is WindowsWebView2EnvironmentRequestedEventArgs wv)
            {
                wv.UserDataFolder = profileDir;
                wv.ProfileName = "main";
            }
        };

        // 页面自己发的媒体请求 —— 播放器实际吃哪条流，是「有没有更干净源」的直接证据
        var mediaRequests = new List<string>();
        void OnRequest(object? sender, EventArgs e)
        {
            if (e is not WebResourceRequestedEventArgs a) return;
            var u = a.Request?.Uri?.ToString();
            if (string.IsNullOrWhiteSpace(u)) return;
            if (u.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return;
            var looksMedia = u.Contains("douyinvod.com", StringComparison.OrdinalIgnoreCase)
                             || u.Contains("/video/tos/", StringComparison.OrdinalIgnoreCase);
            if (!looksMedia) return;
            lock (mediaRequests)
            {
                if (mediaRequests.Count < 60 && !mediaRequests.Contains(u)) mediaRequests.Add(u);
            }
        }

        view.WebResourceRequested += OnRequest;
        try
        {
            window.Show();

            var pageUrl = $"https://www.douyin.com/video/{videoId}";
            await NavigateAsync(view, pageUrl);

            // 页面是客户端渲染 —— 等真就绪（媒体请求出现 / _ROUTER_DATA 到位），最多 ~25 秒
            var stateJson = await WaitForPageAsync(view,
                () => { lock (mediaRequests) return mediaRequests.Count > 0; },
                TimeSpan.FromSeconds(25));
            if (stateJson is null)
            {
                Log.Error("[DouyinProbe] 页面加载超时，拿不到 navigator.userAgent —— 无法算 a_bogus");
                return 4;
            }

            var ua = GetStr(stateJson, "ua");
            if (string.IsNullOrWhiteSpace(ua))
            {
                Log.Error("[DouyinProbe] 页面没返回 userAgent：{State}", Truncate(stateJson, 400));
                return 4;
            }

            // 登录态是「detail 返空」的头号解释 —— 先量出来，别把「没登录」误判成「签名不对」
            var cookieNames = await ReadCookieNamesAsync(view);
            var loggedIn = cookieNames.Any(n =>
                n is "sessionid" or "sessionid_ss" or "sid_tt" or "sid_guard");
            Log.Information("[DouyinProbe] 档案 cookie {Count} 个｜登录态 {Login}｜有 msToken {MsToken}｜有 ttwid {TtWid}",
                cookieNames.Count,
                loggedIn ? "已登录" : "未登录",
                cookieNames.Contains("msToken") ? "是" : "否",
                cookieNames.Contains("ttwid") ? "是" : "否");

            // 与 DouyinDownloadExtractor 同一条 query（两边必须一致，否则签名对不上）
            var query = DouyinDownloadExtractor.BuildDetailQuery(videoId);
            var abogus = ABogusGenerator.Generate(query, ua, body: null, aid: 6383);

            // 打法一：不带签名，交给页面的 secsdk-strategy 在 send 时追加
            Log.Information("[DouyinProbe] ① 页内 XHR（不签名，交页面 SDK 处理）");
            var xhrResult = await FetchDetailAsync(view, "xhr",
                "/aweme/v1/web/aweme/detail/?" + query, DetailTimeout);

            // 打法二：带我们自己的 a_bogus
            Log.Information("[DouyinProbe] ② 页内 fetch（自带 a_bogus，{Len} 字符）", abogus.Length);
            var fetchResult = await FetchDetailAsync(view, "fetch",
                "/aweme/v1/web/aweme/detail/?" + query + "&a_bogus=" + Uri.EscapeDataString(abogus),
                DetailTimeout);

            if (xhrResult is null && fetchResult is null)
            {
                Log.Error("[DouyinProbe] 两种打法都没有响应（超时）—— 页面可能被挑战页接管");
                return 5;
            }

            // ③ 生产路径整条走一遍：DouyinPageExecutor（页面自签 XHR）→ TryParseBrowserPayload
            //    —— 探针自己那两发只是证明机制，这一段才验「真正上线的那条代码」
            Log.Information("[DouyinProbe] ③ 生产路径（DouyinPageExecutor → TryParseBrowserPayload）");
            var prodPayload = await new DouyinPageExecutor(view)
                .ExecuteExtractPageAsync(pageUrl, CancellationToken.None);
            var prodInfo = prodPayload is null
                ? null
                : DouyinDownloadExtractor.TryParseBrowserPayload(
                    prodPayload, pageUrl, videoId, new ExtractContext());

            if (prodInfo is null)
            {
                Log.Warning("[DouyinProbe] 生产路径没解析出作品（payload {Len} 字节）",
                    prodPayload?.Length ?? 0);
            }
            else
            {
                Log.Information("[DouyinProbe] 生产路径 → 标题「{Title}」作者「{Author}」{Count} 条流",
                    prodInfo.Title, prodInfo.Author, prodInfo.Formats.Count);
                foreach (var f in prodInfo.Formats.Take(4))
                    Log.Information("[DouyinProbe]   fmt {Id}  {W}x{H}  {Url}",
                        f.FormatId, f.Width, f.Height, Truncate(f.Url, 120));

                var sel = FormatSelector.Select(prodInfo.Formats, FormatStrategy.BestMerged);
                Log.Information("[DouyinProbe] 选流结果 {W}x{H}  {Url}",
                    sel.Video.Width, sel.Video.Height, Truncate(sel.Video.Url, 150));
            }

            // ④ 完整 ExtractAsync（四条路径全开）—— 复现「下载页真正走的那条」。
            //    重点看它有没有走到浏览器：走到 = 会打 detail；没走到 = 前三条里某条"成功"了，
            //    而那条拿到的是不是水印流就是这次要查的。
            Log.Information("[DouyinProbe] ④ 完整 ExtractAsync（四条路径全开）");
            var extractUrl = target.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? target
                : $"https://www.douyin.com/video/{videoId}";
            try
            {
                var extractCtx = new ExtractContext
                {
                    UserAgent = ua,
                    // ⚠️ ExtractAsync 内部全程 ConfigureAwait(false)，回调一定在**线程池**上 ——
                    // 必须像 DouyinWebExecutor 那样弹回 UI 线程，否则 NativeWebView.Source 直接
                    // 抛 "Call from invalid thread"（探针第一版就栽在这儿）
                    BrowserExtract = (u, t) => Dispatcher.UIThread.InvokeAsync(
                        () => new DouyinPageExecutor(view).ExecuteExtractPageAsync(u, t)),
                };
                var full = await new DouyinDownloadExtractor()
                    .ExtractAsync(extractUrl, extractCtx, CancellationToken.None);
                Log.Information("[DouyinProbe] 提取成功 → 「{Title}」{Count} 条流",
                    full.Title, full.Formats.Count);
                foreach (var f in full.Formats.Take(4))
                    Log.Information("[DouyinProbe]   ext {Id}  {W}x{H}  {Url}",
                        f.FormatId, f.Width, f.Height, Truncate(f.Url, 130));
            }
            catch (DownloadException ex)
            {
                Log.Warning("[DouyinProbe] 提取失败 {Kind}：{Msg}", ex.Kind, ex.Message);
            }

            var report = new Dictionary<string, object?>
            {
                ["probedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["videoId"] = videoId,
                ["sessionId"] = sessionId.ToString("N"),
                ["accountLabel"] = label,
                ["pageUrl"] = pageUrl,
                ["userAgent"] = ua,
                ["loggedIn"] = loggedIn,
                ["cookieNames"] = cookieNames,
                ["pageState"] = stateJson,
                ["mediaRequests"] = mediaRequests.ToList(),
                ["xhrNoSign"] = Parse(xhrResult),
                ["fetchWithAbogus"] = Parse(fetchResult),
                ["productionPayload"] = prodPayload,
                ["productionFormats"] = prodInfo?.Formats.Select(f => new
                {
                    f.FormatId, f.Width, f.Height, f.Url, f.HasAudio,
                }).ToList(),
            };

            var path = Path.Combine(Path.GetTempPath(), "douyin_probe.json");
            await File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

            LogSummary("① 页面 SDK 签名", xhrResult);
            LogSummary("② 自带 a_bogus", fetchResult);
            LogMediaRequests(mediaRequests);
            Log.Information("[DouyinProbe] 报告已写入 {Path}", path);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DouyinProbe] 探针异常");
            return 9;
        }
        finally
        {
            view.WebResourceRequested -= OnRequest;
            try { window.Close(); } catch { /* 关不掉也不影响退出 */ }
        }
    }

    // ═══ 页内脚本 ═══

    /// <summary>读页面状态：UA（算签名用）、播放器实际在用的流、已发生的媒体请求。</summary>
    private const string ReadStateScript = """
        (function() {
            var v = document.querySelector('video');
            var perf = [];
            try {
                perf = (performance.getEntriesByType('resource') || [])
                    .map(function(e) { return e.name; })
                    .filter(function(n) {
                        return n.indexOf('douyinvod') >= 0
                            || n.indexOf('/video/tos/') >= 0
                            || n.indexOf('aweme/v1/play') >= 0;
                    });
            } catch (e) {}
            return JSON.stringify({
                ua: navigator.userAgent || '',
                href: location.href || '',
                title: document.title || '',
                ready: document.readyState || '',
                hasRouter: !!window._ROUTER_DATA,
                videoSrc: v ? (v.currentSrc || v.src || '') : '',
                perf: perf.slice(0, 60)
            });
        })()
        """;

    /// <summary>
    /// 页内请求 `aweme/detail`，两种打法各跑一次：
    ///
    ///  · `xhr`   —— **不带 a_bogus**，让页面自己的 secsdk-strategy 在 `XHR.prototype.send`
    ///    被拦截时追加（这正是它存在的意义）。回 `responseURL` 看它到底补没补、
    ///    补的是什么 —— 补上了就说明「签名交给页面」这条路通。
    ///  · `fetch` —— 带我们自己算的 a_bogus（<see cref="ABogusGenerator"/>）。2026-10-23 实测
    ///    返回 `403 Blocked by ArgusSecurityPlugin Sign Invalid`：uifid 有了，签名对不上。
    ///
    /// 结果压成只留视频/活动相关字段 —— 原始响应几百 KB，没必要整个搬出来。
    /// `activityList` = 响应顶层 <c>activity_list</c>（2026-11-09 2.6.10：活动是否挂上的唯一回读通道）。
    /// </summary>
    private const string DetailProbeScript = """
        (function() {
            window.__dp_done = false;
            window.__dp_result = null;
            var mode = window.__dp_mode;
            var url = window.__dp_url;

            function done(status, finalUrl, text) {
                text = text || '';
                var out = { mode: mode, status: status, url: finalUrl || '', len: text.length, ok: false, raw: '' };
                try {
                    var d = JSON.parse(text);
                    var ad = d.aweme_detail;
                    if (ad && ad.video) {
                        var v = ad.video;
                        function u(x) { return (x && x.url_list) ? x.url_list : []; }
                        out.ok = true;
                        out.awemeId = ad.aweme_id || '';
                        out.desc = ad.desc || '';
                        out.duration = v.duration || 0;
                        out.play = u(v.play_addr);
                        out.playH264 = u(v.play_addr_h264);
                        out.playH265 = u(v.play_addr_265);
                        out.download = u(v.download_addr);
                        out.gears = (v.bit_rate || []).map(function(b) {
                            return {
                                gear: b.gear_name || '',
                                quality: b.quality_type || 0,
                                bitRate: b.bit_rate || 0,
                                w: (b.play_addr && b.play_addr.width) || 0,
                                h: (b.play_addr && b.play_addr.height) || 0,
                                urls: u(b.play_addr)
                            };
                        });
                        // 2026-11-09（2.6.10）：活动回显 —— 详情响应**顶层** activity_list
                        // （抓包 requests/video_detail.json 确认存在；本字段是活动是否挂上的
                        // 唯一回读通道，解析时别丢）
                        out.activityList = d.activity_list || [];
                    } else {
                        out.raw = text.substring(0, 1200);
                        out.keys = (d && typeof d === 'object') ? Object.keys(d).slice(0, 20) : [];
                    }
                } catch (e) {
                    out.raw = text.substring(0, 1200);
                }
                window.__dp_result = JSON.stringify(out);
                window.__dp_done = true;
            }

            if (mode === 'xhr') {
                try {
                    var xhr = new XMLHttpRequest();
                    xhr.open('GET', url, true);
                    xhr.onload = function() { done(xhr.status, xhr.responseURL, xhr.responseText); };
                    xhr.onerror = function() { done(0, '', 'XHR error'); };
                    xhr.send();
                } catch (e) {
                    done(0, '', String(e));
                }
            } else {
                fetch(url, {
                    method: 'GET',
                    credentials: 'include',
                    headers: { 'accept': 'application/json, text/plain, */*' }
                }).then(function(r) {
                    return r.text().then(function(t) { done(r.status, r.url, t); });
                }).catch(function(e) {
                    done(0, '', String(e));
                });
            }
        })()
        """;

    // ═══ 导航与轮询 ═══

    private static async Task NavigateAsync(NativeWebView view, string url)
    {
        var tcs = new TaskCompletionSource();
        void Handler(object? s, EventArgs e) => tcs.TrySetResult();

        view.NavigationCompleted += Handler;
        try
        {
            // 刚 Show 出来时控制器可能还没就绪 —— 失败重试几次（同 DockTabHostView 的懒导航时机）
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    view.Navigate(new Uri(url));
                    break;
                }
                catch (Exception ex) when (attempt < 2)
                {
                    Log.Debug("[DouyinProbe] 导航尚未就绪（第 {N} 次）：{Msg}", attempt + 1, ex.Message);
                    await Task.Delay(1000);
                }
            }

            await Task.WhenAny(tcs.Task, Task.Delay(NavigateTimeout));
        }
        finally
        {
            view.NavigationCompleted -= Handler;
        }
    }

    /// <summary>
    /// 轮询页面状态，直到页面**真的**就绪（或超时）。
    ///
    /// ⚠️ 别把 `&lt;video&gt;.currentSrc` 当场就绪判据：还没加载出片子时它是
    /// `lf-douyin-pc-web.douyinstatic.com/.../uuu_265.mp4` 这个**静态占位**（2026-10-23 实测
    /// 把它当成了真流，结果 detail 请求在页面 `readyState=interactive` 时就发出去了）。
    /// 真就绪 = readyState complete + （有 _ROUTER_DATA，或已经抓到媒体请求）。
    /// </summary>
    private static async Task<string?> WaitForPageAsync(
        NativeWebView view, Func<bool> hasMediaRequest, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? last = null;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(800);
            try { last = Unescape(await view.InvokeScript(ReadStateScript)); }
            catch { continue; }   // 还在导航 —— 下一轮再试

            if (last is null) continue;
            var ready = GetStr(last, "ready");
            var hasRouter = last.Contains("\"hasRouter\":true", StringComparison.Ordinal);
            if (ready == "complete" && (hasRouter || hasMediaRequest()))
                return last;
        }
        return last;
    }

    private static async Task<string?> FetchDetailAsync(
        NativeWebView view, string mode, string url, TimeSpan timeout)
    {
        await view.InvokeScript("window.__dp_mode = " + JsonSerializer.Serialize(mode) + ";");
        await view.InvokeScript("window.__dp_url = " + JsonSerializer.Serialize(url) + ";");
        await view.InvokeScript(DetailProbeScript);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            try
            {
                if (Unescape(await view.InvokeScript("window.__dp_done")) == "true")
                    return Unescape(await view.InvokeScript("window.__dp_result"));
            }
            catch { /* 页面在跳转 —— 下一轮 */ }
        }
        return null;
    }

    // ═══ 账号档案解析 ═══

    /// <summary>
    /// 找登录过抖音的账号档案：指定了会话就用它，否则取**最近更新**的那个抖音账号。
    /// 扫的是 accounts/{会员}/sessions.json（本地注册表镜像），不碰服务端。
    /// </summary>
    private static (Guid SessionId, string ProfileDir, string Label)? ResolveSession(Guid? wanted)
    {
        if (wanted is { } forced)
        {
            // 显式指定：仍然要找到它属于哪个会员目录
            foreach (var memberDir in SafeDirs(PlatformPaths.AccountsRoot))
            {
                var dir = Path.Combine(memberDir, "wv2", forced.ToString("N"));
                if (Directory.Exists(dir)) return (forced, dir, "（指定会话）");
            }
            return (forced, Path.Combine(PlatformPaths.MemberDir(Guid.Empty), "wv2", forced.ToString("N")),
                "（指定会话，档案不存在）");
        }

        (Guid Id, string Dir, string Label, DateTimeOffset Updated)? best = null;

        foreach (var memberDir in SafeDirs(PlatformPaths.AccountsRoot))
        {
            var jsonPath = Path.Combine(memberDir, "sessions.json");
            if (!File.Exists(jsonPath)) continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    if (GetStr(row, "platformKey") != "douyin") continue;
                    if (!Guid.TryParse(GetStr(row, "id"), out var id) || id == Guid.Empty) continue;

                    var label = GetStr(row, "platformName") ?? "";
                    var updated = DateTimeOffset.TryParse(GetStr(row, "updatedAt"), out var t)
                        ? t : DateTimeOffset.MinValue;
                    var dir = Path.Combine(memberDir, "wv2", id.ToString("N"));

                    // 档案目录不存在 = 这台机器没打开过该账号的浏览器，登录态无从谈起
                    if (!Directory.Exists(dir)) continue;

                    if (best is null || updated > best.Value.Updated)
                        best = (id, dir, label, updated);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[DouyinProbe] 读取 {Path} 失败", jsonPath);
            }
        }

        return best is { } b ? (b.Id, b.Dir, b.Label) : null;
    }

    /// <summary>读档案里的 cookie **名字**（不取值 —— 值里有会话令牌，不该落日志）。</summary>
    private static async Task<List<string>> ReadCookieNamesAsync(NativeWebView view)
    {
        var names = new List<string>();
        try
        {
            var manager = view.TryGetCookieManager();
            if (manager is null) return names;
            foreach (var c in await manager.GetCookiesAsync())
            {
                if (!string.IsNullOrWhiteSpace(c.Name)) names.Add(c.Name);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[DouyinProbe] 读 cookie 失败");
        }
        return names;
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch { return []; }
    }

    // ═══ 输出 ═══

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return null; }
    }

    /// <summary>把一种打法的结论压成几行日志 —— 一眼看出「有没有干净的 play_addr」。</summary>
    private static void LogSummary(string label, string? detailJson)
    {
        if (string.IsNullOrWhiteSpace(detailJson))
        {
            Log.Warning("[DouyinProbe] {Label} —— 无响应", label);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(detailJson);
            var root = doc.RootElement;
            var status = GetStr(root, "status");
            var finalUrl = GetStr(root, "url");

            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var raw = root.TryGetProperty("raw", out var r) ? Truncate(r.GetString(), 200) : "";
                Log.Warning("[DouyinProbe] {Label} —— 失败（status={Status}）：{Raw}", label, status, raw);
                Log.Warning("[DouyinProbe]   实际请求 URL：{Url}", Truncate(finalUrl, 260));
                return;
            }

            Log.Information("[DouyinProbe] {Label} —— 成功（status={Status}）作品 {Id}，时长 {Dur}ms",
                label, status, GetStr(root, "awemeId"), GetStr(root, "duration"));

            // 关键判据：页面 SDK 有没有在 send 时把 a_bogus / msToken 追加进去
            var signed = finalUrl?.Contains("a_bogus=", StringComparison.Ordinal) == true;
            Log.Information("[DouyinProbe]   实际请求 URL：{Url}", Truncate(finalUrl, 260));
            Log.Information("[DouyinProbe]   页面是否补了 a_bogus：{Signed}", signed ? "是" : "否");

            // 活动回显（2026-11-09 2.6.10）：空数组 = 平台没回活动 / 该作品未参加活动
            if (root.TryGetProperty("activityList", out var acts))
                Log.Information("[DouyinProbe]   activity_list：{List}",
                    Truncate(acts.GetRawText(), 400));

            LogUrlGroup(root, "play");
            LogUrlGroup(root, "playH264");
            LogUrlGroup(root, "playH265");
            LogUrlGroup(root, "download");

            if (root.TryGetProperty("gears", out var gears) && gears.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in gears.EnumerateArray())
                {
                    var urls = g.TryGetProperty("urls", out var us) && us.ValueKind == JsonValueKind.Array
                        ? us.GetArrayLength() : 0;
                    Log.Information("[DouyinProbe]   gear {Gear}  {W}x{H}  {Kbps}kbps  {Count} 条地址",
                        GetStr(g, "gear"), GetStr(g, "w"), GetStr(g, "h"),
                        ParseLong(GetStr(g, "bitRate")) / 1000, urls);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DouyinProbe] 摘要解析失败（报告文件仍然有效）");
        }
    }

    private static void LogMediaRequests(List<string> mediaRequests)
    {
        Log.Information("[DouyinProbe] 页面已发起的媒体请求 {Count} 条（播放器实际吃的流）", mediaRequests.Count);
        foreach (var u in mediaRequests.Take(6))
            Log.Information("[DouyinProbe]   media {Url}", Truncate(u, 170));
    }

    private static void LogUrlGroup(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        var n = arr.GetArrayLength();
        if (n == 0) return;
        Log.Information("[DouyinProbe]   {Key}: {Count} 条", key, n);
        foreach (var u in arr.EnumerateArray().Take(2))
            Log.Information("[DouyinProbe]     {Url}", Truncate(u.GetString(), 150));
    }

    // ═══ 工具 ═══

    private static string? ExtractVideoId(string target)
    {
        var m = IdPathPattern.Match(target);
        if (m.Success) return m.Groups[1].Value;
        m = IdQueryPattern.Match(target);
        if (m.Success) return m.Groups[1].Value;
        return Regex.IsMatch(target, @"^\d{6,}$") ? target : null;
    }

    private static string? GetStr(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(prop, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                _ => null,
            }
            : null;

    private static string? GetStr(string? json, string prop)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return GetStr(doc.RootElement, prop);
        }
        catch { return null; }
    }

    private static long ParseLong(string? s) => long.TryParse(s, out var v) ? v : 0;

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

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
