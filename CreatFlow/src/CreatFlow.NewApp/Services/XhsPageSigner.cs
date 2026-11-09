using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using CreatFlow.Platforms.Xiaohongshu;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 小红书 x-s / x-s-common 签名器 —— 在某个账号标签的 WebView2 档案页里执行页面自带的
/// mnsv2（页面 webpack 里的混淆签名函数，纯 C# 无法复算）。
///
/// 自 CreatFlow.App/Views/WebViewLoginWindow.axaml.cs 的 SignXiaohongshuAsync 移植（2026-09-12）：
///   a = API 路径(+query) + 请求体 JSON   d = MD5(a)   t = MD5(路径+query)
///   x3 = window.mnsv2(a, d, t)           x-s  = "XYS_" + customBase64(JSON({x0..x4}))
///   x-s-common = customBase64(JSON({s0,s1,x0..x12})) —— 与老版逐字一致（已实测 201）
///
/// 与老版差异：老版的隐藏窗登录后必定停留在发布页（SDK 常驻），坞内标签页停在哪个页面由
/// 用户决定 —— 故本实现走「探测 → webpack 搜 → 轮询 → 借发布页拉起」四级兜底，
/// 借发布页属于临时导航，签完自动导航回用户原页面。
///
/// 线程约定：InvokeScript / Navigate 必须在 UI 线程调用（由 <see cref="XhsSignerService"/> 保证）。
/// </summary>
public sealed class XhsPageSigner : IXhsSignHost
{
    /// <summary>发布页 —— creator 平台签名模块（mnsv2）随该页 webpack 一起加载（老版同款）</summary>
    private const string PublishPageUrl =
        "https://creator.xiaohongshu.com/publish/publish?source=official";

    private readonly NativeWebView _webView;

    /// <summary>同页串行：并发签名会互相覆盖注入的全局变量（多平台并行同步时可能撞上）</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>临时导航前的用户页面地址（EnsureSdkAsync 借发布页时记录，签完恢复）</summary>
    private Uri? _restoreUri;

    /// <summary>mnsv2 已确认在本页可用 —— 后续签名跳过整套探测（首个请求才付加载代价）</summary>
    private bool _sdkReady;

    public XhsPageSigner(NativeWebView webView) => _webView = webView;

    /// <summary>
    /// 预热：把「页面加载完 + mnsv2 可用」提前做到位，让随后每个请求的签名只花几十毫秒。
    /// 调用时机在 SDK 调用之前（Credentials 拉取等），避开 HttpClient 超时窗口。
    /// 返回 false 表示当前不可用（调用方继续走 SDK 也行 —— 请求会裸发）。
    /// </summary>
    public async Task<bool> PrepareAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _restoreUri = null;
            return await EnsureSdkAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[XhsSign] 预热签名模块异常");
            return false;
        }
        finally
        {
            RestorePage();
            _gate.Release();
        }
    }

    /// <summary>
    /// 计算一次请求签名。返回 null = 签名不可用（调用方按老版语义裸发 → 平台大概率 406，
    /// 由上层把「签名不可用」作为失败原因回报）。
    /// </summary>
    public async Task<XhsSignHeaders?> SignAsync(string urlPath, string? bodyJson, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _restoreUri = null;
            if (!_sdkReady && !await EnsureSdkAsync(ct))
            {
                Log.Warning("[XhsSign] mnsv2 不可用（当前页 {Url}），本次不签名", _webView.Source);
                return null;
            }

            var headers = await SignOnceAsync(urlPath, bodyJson, ct);
            if (headers == null) _sdkReady = false; // 页内签名失败（页面被导航走等）→ 下次重新探测
            return headers;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[XhsSign] 页面签名异常 {Path}", urlPath);
            return null;
        }
        finally
        {
            RestorePage();
            _gate.Release();
        }
    }

    /// <summary>借了发布页 → 导航回用户原页面（失败也不抛：签名的副作用不该污染调用方）</summary>
    private void RestorePage()
    {
        if (_restoreUri == null) return;
        try { _webView.Navigate(_restoreUri); }
        catch (Exception ex) { Log.Warning(ex, "[XhsSign] 恢复原页面失败"); }
        _restoreUri = null;
    }

    // ═══ SDK 就绪（等页面 → 探测 → webpack 搜 → 轮询 → 借发布页） ═══

    private async Task<bool> EnsureSdkAsync(CancellationToken ct)
    {
        if (_sdkReady) return true;

        // 0. InvokeScript 要求页面已加载（Avalonia 会抛「before any page was loaded」）——
        //    冷启动（刚激活标签建的 WebView）等首个 NavigationCompleted
        if (!await WaitPageLoadedAsync(TimeSpan.FromSeconds(20), ct))
        {
            Log.Warning("[XhsSign] 等待页面加载超时（{Url}）", _webView.Source);
            return false;
        }

        // 1. 页面已就绪（页内 SDK 由页面自身加载：首页/发布页都会拉）
        if (await HasMnsv2Async()) { _sdkReady = true; return true; }

        // 2. 页面 webpack 模块里有函数体（SDK 已加载但没挂到 window）
        await SearchWebpackForMnsv2Async();
        if (await HasMnsv2Async()) { _sdkReady = true; return true; }

        // 3. 页面自身流量可能正在把 SDK 拉起来（首页加载 → 首个带签请求 → 动态 script）
        if (await PollMnsv2Async(TimeSpan.FromSeconds(5), ct)) { _sdkReady = true; return true; }

        // 4. 兜底：临时导航到发布页（老版登录后常驻此页，SDK 必然加载）
        //    无论成败都由 finally 导航回原页面
        var back = _webView.Source;
        if (back is not null && back.AbsoluteUri.StartsWith(PublishPageUrl, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("[XhsSign] 发布页内未见 mnsv2（{Url}）—— 可能未登录/页面异常", back);
            return false; // 已经在发布页仍没有 SDK —— 再导航一次也没用
        }
        if (back is not null) _restoreUri = back;
        Log.Information("[XhsSign] 当前页（{Back}）无 mnsv2，临时导航发布页加载签名模块", back);
        await NavigateAndWaitAsync(PublishPageUrl, TimeSpan.FromSeconds(20), ct);
        if (await PollMnsv2Async(TimeSpan.FromSeconds(10), ct)) { _sdkReady = true; return true; }
        Log.Warning("[XhsSign] 发布页仍未就绪 mnsv2（{Url}）", _webView.Source);
        return false;
    }

    private async Task<bool> HasMnsv2Async()
    {
        try
        {
            var t = Unescape(await _webView.InvokeScript("typeof window.mnsv2"));
            return t == "function";
        }
        catch { return false; }
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

    /// <summary>轮询等待 mnsv2 挂到 window（页面加载慢/懒加载 SDK 时兜底）。</summary>
    private async Task<bool> PollMnsv2Async(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;
            await Task.Delay(500, ct);
            if (await HasMnsv2Async()) return true;
        }
        return false;
    }

    /// <summary>
    /// 在页面 webpack chunk 缓存里搜 mnsv2 函数并挂到 window（老版 LoadXhsSdkAsync 逐字移植）。
    /// 页面已加载 webpack 运行时但 SDK 尚未自挂载时命中。
    /// </summary>
    private async Task SearchWebpackForMnsv2Async()
    {
        try
        {
            var found = Unescape(await _webView.InvokeScript("""
                (function() {
                    if (typeof window.mnsv2 === 'function') return 'window';
                    for (const key of Object.keys(self)) {
                        if (!key.startsWith('webpackChunk')) continue;
                        const chunkArr = self[key];
                        if (!Array.isArray(chunkArr)) continue;
                        const pushFn = chunkArr.push;
                        if (pushFn === Array.prototype.push) continue;

                        const visited = new Set();
                        const queue = [{obj: chunkArr}, {obj: pushFn}];
                        while (queue.length > 0) {
                            const item = queue.shift();
                            const obj = item.obj;
                            if (!obj || (typeof obj !== 'object' && typeof obj !== 'function')) continue;
                            if (visited.has(obj)) continue;
                            visited.add(obj);
                            for (const prop of Object.getOwnPropertyNames(obj)) {
                                if (prop === 'prototype' || prop === '__proto__') continue;
                                try {
                                    const val = obj[prop];
                                    if (typeof val === 'function') {
                                        const cache = val.c || val.m;
                                        if (cache && typeof cache === 'object') {
                                            for (const modId of Object.keys(cache)) {
                                                try {
                                                    const exp = cache[modId] && cache[modId].exports;
                                                    if (!exp || typeof exp !== 'object') continue;
                                                    for (const ek of Object.keys(exp)) {
                                                        const ev = exp[ek];
                                                        if (typeof ev === 'function' && ev.name === 'mnsv2') {
                                                            window.mnsv2 = ev;
                                                            return 'webpack';
                                                        }
                                                    }
                                                } catch(e) {}
                                            }
                                        }
                                    }
                                    if (typeof val === 'object' || typeof val === 'function') {
                                        if (queue.length < 100) queue.push({obj: val});
                                    }
                                } catch(e) {}
                            }
                        }
                    }
                    return 'none';
                })()
                """));
            if (found is "window" or "webpack")
                Log.Debug("[XhsSign] mnsv2 命中：{Source}", found);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[XhsSign] webpack 搜索 mnsv2 失败");
        }
    }

    // ═══ 签名执行 ═══

    private async Task<XhsSignHeaders?> SignOnceAsync(string urlPath, string? bodyJson, CancellationToken ct)
    {
        // a = 路径(+query) + 请求体（无 body 时仅路径）；d = MD5(a)；t = MD5(路径+query)
        // REF: fangai/xiaohongshu/mnsv2.py + library-launcher.js 函数 Z
        var hasBody = !string.IsNullOrEmpty(bodyJson);
        var a = hasBody ? urlPath + bodyJson : urlPath;
        var d = Md5Hex(a);
        var t = Md5Hex(urlPath);

        var script = $$"""
            (function() {
                function customBase64Encode(data) {
                    var customAlphabet = "ZmserbBoHQtNP+wOcza/LpngG8yJq42KWYj0DSfdikx3VT16IlUAFM97hECvuRX5";
                    var stdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
                    var bytes = [];
                    for (var i = 0; i < data.length; i++) bytes.push(data.charCodeAt(i));
                    var result = '';
                    for (var j = 0; j < bytes.length; j += 3) {
                        var chunk = (bytes[j] << 16) + (bytes[j+1] << 8 || 0) + (bytes[j+2] || 0);
                        result += stdAlphabet[(chunk >> 18) & 63];
                        result += stdAlphabet[(chunk >> 12) & 63];
                        result += j + 1 < bytes.length ? stdAlphabet[(chunk >> 6) & 63] : '=';
                        result += j + 2 < bytes.length ? stdAlphabet[chunk & 63] : '=';
                    }
                    var custom = '';
                    for (var k = 0; k < result.length; k++) {
                        var c = result[k];
                        if (c === '=') { custom += '='; continue; }
                        custom += customAlphabet[stdAlphabet.indexOf(c)];
                    }
                    return custom;
                }
                try {
                    if (typeof window.mnsv2 !== 'function') return JSON.stringify({ok:false, error:'mnsv2 missing'});
                    var a = {{JsonSerializer.Serialize(a)}};
                    var d = {{JsonSerializer.Serialize(d)}};
                    var t = {{JsonSerializer.Serialize(t)}};
                    var hasBody = {{(hasBody ? "true" : "false")}};

                    var x3 = window.mnsv2(a, d, t);
                    var now = Date.now();
                    var xs = 'XYS_' + customBase64Encode(JSON.stringify({
                        x0: "2.0.0", x1: "ugc", x2: "Windows", x3: x3, x4: hasBody ? 'object' : ''
                    }));
                    var xsc = customBase64Encode(JSON.stringify({
                        s0: "PC", s1: "",
                        x0: "", x1: "2.0.0", x2: "Windows", x3: "ugc", x4: "1.14.0",
                        x5: "", x6: "", x7: "", x8: "", x9: 0,
                        x10: 1, x11: "normal", x12: now + ";"
                    }));
                    return JSON.stringify({ ok: true, xs: xs, xsc: xsc, xt: String(now), x3len: x3 ? x3.length : 0 });
                } catch(e) {
                    return JSON.stringify({ ok: false, error: (e && e.message) ? e.message : String(e) });
                }
            })()
            """;

        var raw = Unescape(await _webView.InvokeScript(script));
        if (string.IsNullOrWhiteSpace(raw)) return null;

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var err = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
            Log.Warning("[XhsSign] 页内签名失败：{Error}", err);
            return null;
        }

        var xs = root.TryGetProperty("xs", out var x) ? x.GetString() ?? "" : "";
        var xsc = root.TryGetProperty("xsc", out var c) ? c.GetString() ?? "" : "";
        var xt = root.TryGetProperty("xt", out var tt) ? tt.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(xs)) return null;

        Log.Debug("[XhsSign] 签名完成 {Path}（x3len={Len}）",
            urlPath, root.TryGetProperty("x3len", out var l) ? l.GetInt32() : 0);
        return new XhsSignHeaders(xs, xsc, xt);
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

    /// <summary>小写 MD5 hex（REF: mnsv2.py — d/t 均为 MD5）。</summary>
    private static string Md5Hex(string input) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(input)));
}
