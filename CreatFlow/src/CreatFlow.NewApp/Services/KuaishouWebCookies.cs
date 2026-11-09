using System.Collections;
using CreatFlow.NewApp.Services.AccountLogin;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 快手「活 Cookie」宿主 —— 本账号档案页的 CookieManager（按 sessionId 注册）。
///
/// 为什么要它：快手作品列表接口认 Cookie 里的 <c>kuaishou.web.cp.api_st</c> ——
/// **缺 → result=109，过期 → 500002「请稍后重试」**（2026-11-09 抓包实测；参数、签名、
/// UA、kpn/subBiz/kpf 全部排查过，同参数同签名换一份新 Cookie 立刻成功）。
/// 服务端 <c>x_platform_sessions</c> 存的是「同步那一刻」的快照，页面里的 api_st 会随
/// 浏览器会话刷新 —— 只读服务端快照，过一段时间就会失效。档案页的 CookieManager 才是最新的。
///
/// 与 <see cref="DouyinWebExecutor"/> 同款：**按 sessionId 精确注册**（多账号不串），
/// 该会话没开过标签 → 返回 null，调用方退回服务端快照。
/// </summary>
public interface IKuaishouCookieHost
{
    /// <summary>读本账号档案页的 Cookie（含 HttpOnly）；失败返回 null。</summary>
    Task<string?> ReadCookieHeaderAsync(CancellationToken ct);
}

/// <summary>
/// 快手活 Cookie 注册表 + 合并 —— 静态类（与 <see cref="DouyinWebExecutor"/> 同款，
/// App 层无参单例）。取不到活 Cookie 时**原样返回服务端快照**，绝不抛。
/// </summary>
public static class KuaishouWebCookies
{
    /// <summary>单次读取预算 —— CookieManager 是本机调用，8 秒足够；卡住不能让发布悬着。</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(8);

    private static readonly Dictionary<Guid, IKuaishouCookieHost> _hosts = [];
    private static readonly object _lock = new();

    public static void Register(Guid sessionId, IKuaishouCookieHost host)
    {
        lock (_lock) _hosts[sessionId] = host;
        Log.Debug("[KuaishouCookie] 会话 {Session} 注册活 Cookie 宿主", sessionId);
    }

    public static void Unregister(Guid sessionId)
    {
        lock (_lock) _hosts.Remove(sessionId);
        Log.Debug("[KuaishouCookie] 会话 {Session} 注销活 Cookie 宿主", sessionId);
    }

    /// <summary>该会话是否有可用宿主（只做诊断/日志用）。</summary>
    public static bool Has(Guid? sessionId)
    {
        lock (_lock) return sessionId is { } sid && _hosts.ContainsKey(sid);
    }

    /// <summary>
    /// 用档案页的活 Cookie 覆盖/补齐服务端快照，返回合并后的 Cookie 串。
    ///
    /// 没有宿主 / 读取超时 / 异常 → **原样返回 fallback**（发布不能因为取不到活 Cookie 中断）。
    /// 合并语义见 <see cref="KuaishouCookieMerge.Merge"/>。
    /// </summary>
    public static async Task<string> MergeWithLiveAsync(
        Guid? sessionId, string fallbackCookie, CancellationToken ct)
    {
        var host = Resolve(sessionId);
        if (host is null) return fallbackCookie;

        string? live = null;
        try
        {
            Task<string?> job;
            try
            {
                // CookieManager 是原生对象，和 WebView 一样必须在 UI 线程访问
                job = Dispatcher.UIThread.InvokeAsync(() => host.ReadCookieHeaderAsync(ct));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[KuaishouCookie] UI 线程调度失败 —— 用服务端快照");
                return fallbackCookie;
            }

            var done = await Task.WhenAny(job, Task.Delay(ReadTimeout, ct));
            if (done != job)
            {
                Log.Warning("[KuaishouCookie] 读档案页 Cookie 超时 {Seconds}s —— 用服务端快照",
                    ReadTimeout.TotalSeconds);
                _ = job.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return fallbackCookie;
            }

            live = await job;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "[KuaishouCookie] 读档案页 Cookie 失败 —— 用服务端快照");
            return fallbackCookie;
        }

        if (string.IsNullOrWhiteSpace(live)) return fallbackCookie;
        var merged = KuaishouCookieMerge.Merge(fallbackCookie, live);
        Log.Debug("[KuaishouCookie] 会话 {Session} 活 Cookie 已合并（长度 {Before} → {After}）",
            sessionId, fallbackCookie.Length, merged.Length);
        return merged;
    }

    /// <summary>
    /// 精确会话命中。**不做**「退而求其次用别的账号标签」——多账号下会拿 A 的浏览器
    /// 去打 B 的请求。宁可退回快照（可能过期，有明确 109/500002 提示），也不能串号。
    /// </summary>
    private static IKuaishouCookieHost? Resolve(Guid? sessionId)
    {
        lock (_lock)
        {
            if (sessionId is { } sid && _hosts.TryGetValue(sid, out var exact)) return exact;
        }
        return null;
    }
}

/// <summary>
/// Cookie 合并 + 校验（纯函数，自检直接断言）。
/// </summary>
public static class KuaishouCookieMerge
{
    /// <summary>
    /// 合并两份 Cookie：**活 Cookie 优先**（同名覆盖），快照里独有的保留，
    /// 活 Cookie 独有的追加；非法名字与空值一律丢弃。
    ///
    /// 为什么保留快照独有项：档案页可能因清理策略丢了某个 cookie，而服务端快照里有；
    /// 两边都不丢才能覆盖「档案没开」「档案缺项」两种情形。
    /// </summary>
    public static string Merge(string? fallback, string? live)
    {
        var order = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string? cookieString, bool overwrite)
        {
            if (string.IsNullOrEmpty(cookieString)) return;
            foreach (var part in cookieString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var name = part[..eq].Trim();
                var value = part[(eq + 1)..].Trim();
                if (value.Length == 0) continue;
                if (!AccountLoginExtractor.IsValidCookieName(name)) continue;

                if (values.ContainsKey(name))
                {
                    if (overwrite) values[name] = value;
                }
                else
                {
                    order.Add(name);
                    values[name] = value;
                }
            }
        }

        Add(fallback, overwrite: false);
        Add(live, overwrite: true);
        return string.Join("; ", order.Select(n => $"{n}={values[n]}"));
    }

    /// <summary>Cookie 域是否属于快手 —— 防把档案里别的域的 cookie 注进请求。</summary>
    public static bool IsKuaishouDomain(string? domain) =>
        !string.IsNullOrWhiteSpace(domain)
        && domain.Contains("kuaishou", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 快手档案页 Cookie 读取器 —— 用反射读 <c>GetCookiesAsync()</c> 的结果
/// （与 <see cref="AccountLoginExtractor"/> 同一套跨版本兼容写法：不同 WebView 包版本的
/// Cookie 类型属性名可能不同，只认 Name/Value/Domain 三个常见的）。
///
/// 线程约定：必须在 UI 线程调用（由 <see cref="KuaishouWebCookies.MergeWithLiveAsync"/> 保证）。
/// </summary>
public sealed class KuaishouCookieHost : IKuaishouCookieHost
{
    private readonly NativeWebView _webView;

    public KuaishouCookieHost(NativeWebView webView) => _webView = webView;

    public async Task<string?> ReadCookieHeaderAsync(CancellationToken ct)
    {
        var manager = _webView.TryGetCookieManager();
        if (manager is null) return null;

        var getCookiesMethod = manager.GetType().GetMethod("GetCookiesAsync")
            ?? manager.GetType().GetMethod("GetAllCookiesAsync");
        if (getCookiesMethod is null) return null;

        var parameters = getCookiesMethod.GetParameters();
        object?[] args = parameters.Length > 0
            ? [_webView.Source?.ToString() ?? ""]
            : [];
        var task = (Task?)getCookiesMethod.Invoke(manager, args);
        if (task is null) return null;

        await task;
        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        if (result is not IEnumerable cookies) return null;

        var parts = new List<string>();
        foreach (var c in cookies)
        {
            var type = c?.GetType();
            if (type is null) continue;
            var name = type.GetProperty("Name")?.GetValue(c)?.ToString();
            var value = type.GetProperty("Value")?.GetValue(c)?.ToString();
            var domain = type.GetProperty("Domain")?.GetValue(c)?.ToString();

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(value)) continue;
            if (!AccountLoginExtractor.IsValidCookieName(name)) continue;
            if (!KuaishouCookieMerge.IsKuaishouDomain(domain)) continue;

            parts.Add($"{name}={value}");
        }

        return parts.Count > 0 ? string.Join("; ", parts) : null;
    }
}
