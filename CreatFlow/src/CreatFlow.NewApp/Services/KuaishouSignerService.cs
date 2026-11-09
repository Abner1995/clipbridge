using Avalonia.Threading;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 快手签名宿主（一个账号标签的档案页 = 一个宿主）。
/// 由 <see cref="Views.DockTabHostView"/> 实现并注册；<see cref="KuaishouPageSigner"/> 是它的具体载体。
/// </summary>
public interface IKuaishouSignHost
{
    /// <summary>预热：页面加载完 + 签名模块可用（在正式 SDK 调用之前做，避开请求超时窗口）</summary>
    Task<bool> PrepareAsync(CancellationToken ct);

    /// <summary>算一次请求签名；null = 不可用</summary>
    Task<string?> SignAsync(string reqJson, CancellationToken ct);
}

/// <summary>
/// 快手签名器注册表 + 调度（替代老版的「隐藏登录窗签名 + node.exe 回退」）：
///
///   1. 各账号标签宿主在 WebView 就绪时注册自己（sessionId → 该账号档案页）；
///   2. Platforms 层 KuaishouSigHandler 经 KuaishouPlatform.SetSigner 把每次请求转到这里；
///   3. 优先用「请求所属会话」的标签页签；该会话没开过标签 → 请账号坞把宿主热起来；
///   4. 会话标签不存在 / 未登录 → 返回 null（调用方按老版语义裸发 → 平台拒绝，
///      上层以「签名不可用」回报失败原因）。
///
/// 为什么必须走页面：sig3/sig4 的签名模块是快手页面 webpack 里的混淆体，
/// 纯 C# 无法复算（老版用 node.exe 加载抓下来的 chunk.js —— 那是平台版权代码，
/// 2026-11-09 开源前移除，改为页面里实时执行）。
///
/// 静态类：与 ApiClient / XhsSignerService 同款（App 层无参单例），由 App 在开壳时挂 HostWarmer。
/// </summary>
public static class KuaishouSignerService
{
    /// <summary>热宿主等待上限（首次激活 = 建 WebView2 环境 + 首次导航；之后同档案秒级）</summary>
    private static readonly TimeSpan HostReadyTimeout = TimeSpan.FromSeconds(75);

    /// <summary>预热总预算（热宿主 + 等页面加载 + 借发布页拉起签名模块）</summary>
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromSeconds(120);

    /// <summary>单次签名预算（预热过就是毫秒级；没预热成功也别拖过 HttpClient 超时）</summary>
    private static readonly TimeSpan SignTimeout = TimeSpan.FromSeconds(25);

    private static readonly Dictionary<Guid, IKuaishouSignHost> _hosts = [];
    private static readonly object _lock = new();

    /// <summary>宿主热身回调（App 开壳时挂账号坞：sessionId → 该会话标签的 WebView 就绪）。
    /// 壳关闭/换会员时置空 —— 坞没了就别再等宿主。</summary>
    public static Func<Guid, TimeSpan, CancellationToken, Task<bool>>? HostWarmer { get; set; }

    // ═══ 注册（标签宿主调用；UI 线程） ═══

    public static void Register(Guid sessionId, IKuaishouSignHost host)
    {
        lock (_lock) _hosts[sessionId] = host;
        Log.Debug("[KsSigner] 会话 {Session} 注册签名宿主", sessionId);
    }

    public static void Unregister(Guid sessionId)
    {
        lock (_lock) _hosts.Remove(sessionId);
        Log.Debug("[KsSigner] 会话 {Session} 注销签名宿主", sessionId);
    }

    // ═══ 入口 ═══

    /// <summary>
    /// 预热签名宿主（SDK 调用之前调；失败不阻断 —— 请求仍会裸发，由平台判）。
    /// 首个请求前把 WebView 建好、页面加载完、签名模块就位，正式签名只花毫秒。
    /// </summary>
    public static Task<bool> PrepareAsync(Guid? sessionId, CancellationToken ct) =>
        InvokeUiAsync<bool>(() => PrepareCoreAsync(sessionId, ct), PrepareTimeout, "预热", false);

    /// <summary>签名入口（KuaishouPlatform.SetSigner 注入，任意线程）</summary>
    public static Task<string?> SignAsync(
        Guid? sessionId, string reqJson, CancellationToken ct) =>
        InvokeUiAsync<string?>(() => SignCoreAsync(sessionId, reqJson, ct),
            SignTimeout, "签名", null);

    /// <summary>
    /// UI 线程调度（WebView2 的 InvokeScript/Navigate 只能在 UI 线程；坞的状态也在 UI 线程）。
    /// 带超时兜底：调度器/宿主卡住时不能让同步流程悬着 —— 超时即按「签名不可用」收场。
    /// </summary>
    private static async Task<T> InvokeUiAsync<T>(
        Func<Task<T>> work, TimeSpan timeout, string what, T fallback)
    {
        Task<T> job;
        try
        {
            // 显式泛型实参：消除 InvokeAsync(Func<TResult>) / InvokeAsync(Func<Task<TResult>>) 的重载歧义
            job = Dispatcher.UIThread.InvokeAsync<T>(work);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[KsSign] UI 线程调度 {What} 失败", what);
            return fallback;
        }

        var done = await Task.WhenAny(job, Task.Delay(timeout));
        if (done != job)
        {
            Log.Warning("[KsSign] UI 线程 {What} 超时 {Seconds}s —— 按不可用收场", what, timeout.TotalSeconds);
            _ = job.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return fallback;
        }
        try { return await job; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[KsSign] UI 线程 {What} 异常", what);
            return fallback;
        }
    }

    private static async Task<bool> PrepareCoreAsync(Guid? sessionId, CancellationToken ct)
    {
        var host = await ResolveOrWarmAsync(sessionId, ct);
        if (host == null)
        {
            Log.Warning("[KsSign] 无可用签名宿主（会话 {Session}）—— 跳过预热", sessionId);
            return false;
        }

        var ok = await host.PrepareAsync(ct);
        Log.Debug("[KsSign] 预热结果：{Result}（会话 {Session}）", ok ? "就绪" : "不可用", sessionId);
        return ok;
    }

    private static async Task<string?> SignCoreAsync(
        Guid? sessionId, string reqJson, CancellationToken ct)
    {
        var host = await ResolveOrWarmAsync(sessionId, ct);
        if (host == null)
        {
            Log.Warning("[KsSign] 无可用签名宿主（会话 {Session}）—— 本次请求不签名", sessionId);
            return null;
        }

        return await host.SignAsync(reqJson, ct);
    }

    /// <summary>精确会话优先；该会话没有可用宿主 → 请求坞热宿主；再不行退回任一已打开的快手
    /// 账号标签（sig3/sig4 不绑定 Cookie，跨账号签名可用 —— 兜底，非首选）。</summary>
    private static async Task<IKuaishouSignHost?> ResolveOrWarmAsync(Guid? sessionId, CancellationToken ct)
    {
        var host = Resolve(sessionId);
        if (host != null) return host;

        if (sessionId is { } sid && HostWarmer is { } warmer)
        {
            Log.Debug("[KsSigner] 会话 {Session} 无签名宿主，请求坞热宿主", sid);
            if (await warmer(sid, HostReadyTimeout, ct)) return Resolve(sid);
        }

        return Resolve(null);
    }

    private static IKuaishouSignHost? Resolve(Guid? sessionId)
    {
        lock (_lock)
        {
            if (sessionId is { } sid && _hosts.TryGetValue(sid, out var exact))
                return exact;
            return _hosts.Count > 0 ? _hosts.Values.First() : null;
        }
    }
}
