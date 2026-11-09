using System.Text.Json;
using Avalonia.Controls;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 快手 sig3 / sig4 签名器 —— 在某个账号标签的 WebView2 档案页里执行页面自带的
/// webpack 签名模块（页面 JS 混淆体，纯 C# 无法复算）。
///
/// 自 CreatFlow.App/Views/WebViewLoginWindow.axaml.cs 的 LoadKuaishouSdkAsync /
/// SignKuaishouAsync 移植（老版用登录后常驻的隐藏签名窗；坞时代改由账号标签页承担）。
///
/// 取模块的手法改自老版 node 签名器（2026-11-09 实测）：
///   旧 App 的「遍历 webpack 缓存」脚本在新版 runtime 下找不到模块（不暴露 r.c），
///   但模块 29924 确实随 chunk-common 加载了。可靠做法 = 往 webpackChunk 数组里
///   push 一个假 chunk，webpack 会把 __webpack_require__ 作为回调参数传进来，
///   直接 r(29924) 拿导出（PJ / nM / u$）。
///
/// 与老版差异：老版登录后必定停留在发布页（签名模块常驻），坞内标签页停在哪个页面由
/// 用户决定 —— 本实现走「探测 → require 捕获 → 轮询 → 借发布页拉起」四级兜底。
/// 借发布页后**不恢复原页面**：webpack 签名模块只在发布页加载，恢复会导致每次签名都
/// 重新导航（批量发布不可接受）；账号标签停在发布页对用户无害。
///
/// 线程约定：InvokeScript / Navigate 必须在 UI 线程调用（由 <see cref="KuaishouSignerService"/> 保证）。
/// </summary>
public sealed class KuaishouPageSigner : IKuaishouSignHost
{
    /// <summary>发布页 —— 快手创作者平台的 webpack 签名模块随该页加载（老版同款）</summary>
    private const string PublishPageUrl = "https://cp.kuaishou.com/article/publish/video";

    /// <summary>签名模块在快手 webpack 里的固定模块 ID（老版 node 签名器同款）</summary>
    private const int SigModuleId = 29924;

    private readonly NativeWebView _webView;

    /// <summary>同页串行：并发签名会互相覆盖注入的全局变量（多平台并行同步时可能撞上）</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>签名模块已确认在本页可用 —— 后续签名跳过整套探测（首个请求才付加载代价）</summary>
    private bool _sdkReady;

    public KuaishouPageSigner(NativeWebView webView) => _webView = webView;

    /// <summary>
    /// 预热：把「页面加载完 + 签名模块可用」提前做到位，让随后每个请求的签名只花几十毫秒。
    /// 调用时机在 SDK 调用之前（上传/发布/合集同步之前），避开 HttpClient 超时窗口。
    /// 返回 false 表示当前不可用（调用方继续走 SDK 也行 —— 请求会裸发）。
    /// </summary>
    public async Task<bool> PrepareAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await EnsureSdkAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[KsSign] 预热签名模块异常");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 计算一次请求签名。返回 null = 签名不可用（调用方按老版语义裸发 → 平台大概率拒绝，
    /// 由上层把「签名不可用」作为失败原因回报）。
    /// </summary>
    public async Task<string?> SignAsync(string reqJson, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_sdkReady && !await EnsureSdkAsync(ct))
            {
                Log.Warning("[KsSign] 签名模块不可用（当前页 {Url}），本次不签名", _webView.Source);
                return null;
            }

            var signed = await SignOnceAsync(reqJson, ct);
            if (signed == null) _sdkReady = false; // 页内签名失败（页面被导航走等）→ 下次重新探测
            return signed;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[KsSign] 页面签名异常");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ═══ SDK 就绪（等页面 → 探测 → require 捕获 → 轮询 → 借发布页） ═══

    private async Task<bool> EnsureSdkAsync(CancellationToken ct)
    {
        if (_sdkReady) return true;

        // 0. InvokeScript 要求页面已加载（Avalonia 会抛「before any page was loaded」）——
        //    冷启动（刚激活标签建的 WebView）等首个 NavigationCompleted
        if (!await WaitPageLoadedAsync(TimeSpan.FromSeconds(20), ct))
        {
            Log.Warning("[KsSign] 等待页面加载超时（{Url}）", _webView.Source);
            return false;
        }

        // 1. 页面已就绪（发布页/管理页可能已加载 webpack 签名模块）
        if (await HasSdkAsync()) { _sdkReady = true; return true; }

        // 2. 注入假 chunk 捕获 require → r(29924) 拿签名模块
        await CaptureSigModuleAsync();
        if (await HasSdkAsync()) { _sdkReady = true; return true; }

        // 3. 页面自身流量可能正在把 SDK 拉起来
        if (await PollSdkAsync(TimeSpan.FromSeconds(5), ct)) { _sdkReady = true; return true; }

        // 4. 兜底：临时导航到发布页（老版登录后常驻此页，签名模块必然加载）
        var back = _webView.Source;
        if (back is not null && back.AbsoluteUri.StartsWith(PublishPageUrl, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("[KsSign] 发布页内未见签名模块（{Url}）—— 可能未登录/页面异常", back);
            return false; // 已经在发布页仍没有 SDK —— 再导航一次也没用
        }
        Log.Information("[KsSign] 当前页（{Back}）无签名模块，导航发布页加载", back);
        await NavigateAndWaitAsync(PublishPageUrl, TimeSpan.FromSeconds(20), ct);
        await CaptureSigModuleAsync();
        if (await PollSdkAsync(TimeSpan.FromSeconds(10), ct)) { _sdkReady = true; return true; }
        Log.Warning("[KsSign] 发布页仍未就绪签名模块（{Url}），最后错误：{Error}",
            _webView.Source, await GetLastErrorAsync());
        return false;
    }

    private async Task<bool> HasSdkAsync()
    {
        try
        {
            var loaded = Unescape(await _webView.InvokeScript("String(window.__ks_sdk_loaded === true)"));
            if (loaded == "true") return true;

            var type = Unescape(await _webView.InvokeScript("typeof window.__ks_sig_module__"));
            return type == "object";
        }
        catch { return false; }
    }

    private async Task<string?> GetLastErrorAsync()
    {
        try { return Unescape(await _webView.InvokeScript("String(window.__ks_sdk_error || '')")); }
        catch { return null; }
    }

    /// <summary>等页面可执行脚本：先直接试一次（已加载过则立刻返回），否则等 NavigationCompleted。</summary>
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

    /// <summary>轮询等待签名模块挂到 window（页面加载慢/懒加载 SDK 时兜底）。
    /// 每秒重跑一次 require 捕获 —— 页面导航完成后 chunks 是陆续加载的，抓一次会漏。</summary>
    private async Task<bool> PollSdkAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var nextCapture = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;
            if (DateTime.UtcNow >= nextCapture)
            {
                await CaptureSigModuleAsync();
                nextCapture = DateTime.UtcNow.AddSeconds(1);
            }
            if (await HasSdkAsync()) return true;
            await Task.Delay(500, ct);
        }
        return false;
    }

    /// <summary>
    /// 往快手 webpack 的 chunk 数组里注入一个假 chunk —— webpack 处理它时会把
    /// <c>__webpack_require__</c> 作为回调参数传进来，借此直接 <c>r(29924)</c> 取签名模块。
    /// （老版 node_kuaishou_signer.js 的「注入假 chunk 捕获 require」同款手法。
    ///   旧 App 的遍历缓存脚本在新版 runtime 下失效，因为 runtime 不暴露 r.c。）
    /// </summary>
    private async Task CaptureSigModuleAsync()
    {
        try
        {
            await _webView.InvokeScript($$"""
                (function() {
                    if (window.__ks_sdk_loaded === true && window.__ks_sig_module__) return 'ready';

                    var arr = self['webpackChunkks_fe_creator_platform'];
                    if (!arr || !Array.isArray(arr)) {
                        for (const key of Object.keys(self)) {
                            if (key.indexOf('webpackChunk') === 0 && Array.isArray(self[key])) {
                                arr = self[key];
                                break;
                            }
                        }
                    }
                    if (!arr) { window.__ks_sdk_error = 'webpack chunk array not found'; return 'no-array'; }
                    if (arr.push === Array.prototype.push) {
                        window.__ks_sdk_error = 'webpack runtime not installed yet';
                        return 'not-ready';
                    }

                    try {
                        arr.push([['__ks_probe_{{SigModuleId}}'], {}, function(r) {
                            try {
                                var m = r({{SigModuleId}});
                                if (m && typeof m.u$ === 'function' && typeof m.nM === 'function') {
                                    window.__ks_sig_module__ = m;
                                    window.__ks_sdk_loaded = true;
                                } else {
                                    window.__ks_sdk_error = 'module {{SigModuleId}} has no sig exports';
                                }
                            } catch (e) {
                                // 缓存兜底：模块可能已被别的 chunk 执行过（webpack 5 的 r.c）
                                try {
                                    if (r.c) {
                                        for (var id in r.c) {
                                            var ex = r.c[id] && r.c[id].exports;
                                            if (ex && typeof ex.u$ === 'function' && typeof ex.nM === 'function') {
                                                window.__ks_sig_module__ = ex;
                                                window.__ks_sdk_loaded = true;
                                                return;
                                            }
                                        }
                                    }
                                } catch (e2) {}
                                window.__ks_sdk_error = String(e && e.message || e);
                            }
                        }]);
                    } catch (e) {
                        window.__ks_sdk_error = 'push failed: ' + (e && e.message || e);
                        return 'push-failed';
                    }
                    return window.__ks_sdk_loaded === true ? 'require' : 'pushed';
                })()
                """);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[KsSign] 捕获 webpack require 失败");
        }
    }

    // ═══ 签名执行 ═══

    private async Task<string?> SignOnceAsync(string reqJson, CancellationToken ct)
    {
        await _webView.InvokeScript(
            "window.__ks_sign_req = " + JsonSerializer.Serialize(reqJson) + ";");
        await _webView.InvokeScript("window.__ks_sign_done = false; window.__ks_sign_result = null;");
        await _webView.InvokeScript("""
            (async function() {
                try {
                    var req = JSON.parse(window.__ks_sign_req);
                    var sigMod = window.__ks_sig_module__;
                    if (!sigMod) {
                        window.__ks_sign_result = JSON.stringify({ok:false, error:'Signature module not found'});
                        window.__ks_sign_done = true;
                        return;
                    }
                    var result;
                    if (req.action === 'sig4') {
                        result = await sigMod.nM({url: req.url, type: req.type, params: req.params});
                    } else {
                        result = await sigMod.u$({url: req.url, type: req.type, params: req.params});
                    }
                    window.__ks_sign_result = JSON.stringify({ok:true, signed_url: result});
                } catch(e) {
                    window.__ks_sign_result = JSON.stringify({ok:false, error: e.message || String(e)});
                }
                window.__ks_sign_done = true;
            })();
            """);

        // 轮询结果（最多 15 秒）
        for (var i = 0; i < 30; i++)
        {
            if (ct.IsCancellationRequested) return null;
            await Task.Delay(500, ct);
            var done = Unescape(await _webView.InvokeScript("String(window.__ks_sign_done === true)"));
            if (done != "true") continue;

            var raw = Unescape(await _webView.InvokeScript("window.__ks_sign_result")) ?? "";
            if (string.IsNullOrWhiteSpace(raw)) return null;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                if (ok)
                {
                    var signed = root.TryGetProperty("signed_url", out var s) ? s.GetString() : null;
                    return string.IsNullOrWhiteSpace(signed) ? null : signed;
                }

                var err = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                Log.Warning("[KsSign] 页内签名失败：{Error}", err);
                return null;
            }
            catch
            {
                return raw; // Fallback: return raw string
            }
        }

        Log.Warning("[KsSign] 页面签名超时");
        return null;
    }

    // ═══ 导航与工具 ═══

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
