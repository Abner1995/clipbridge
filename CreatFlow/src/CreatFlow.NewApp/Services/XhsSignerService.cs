using Avalonia.Threading;
using CreatFlow.Platforms.Xiaohongshu;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 小红书签名宿主（一个账号标签的档案页 = 一个宿主）。
/// 由 <see cref="Views.DockTabHostView"/> 实现并注册；<see cref="XhsPageSigner"/> 是它的具体载体。
/// </summary>
public interface IXhsSignHost
{
    /// <summary>预热：页面加载完 + mnsv2 可用（在正式 SDK 调用之前做，避开请求超时窗口）</summary>
    Task<bool> PrepareAsync(CancellationToken ct);

    /// <summary>算一次请求签名；null = 不可用</summary>
    Task<XhsSignHeaders?> SignAsync(string urlPath, string? bodyJson, CancellationToken ct);
}

/// <summary>
/// 小红书签名器注册表 + 调度（替代老版的「隐藏登录窗签名」）：
///
///   1. 各账号标签宿主在 WebView 就绪时注册自己（sessionId → 该账号档案页）；
///   2. Platforms 层 XhsSignHandler 经 XhsPlatform.SetSigner 把每次请求转到这里；
///   3. 优先用「请求所属会话」的标签页签（x-s 与请求 Cookie 同档案）；该会话没开过标签
///      → 请账号坞把宿主热起来（WebView 懒加载 + 宿主控件在平台页里），签完退回原视图；
///   4. 会话标签不存在 / 未登录 → 返回 null（调用方按老版语义裸发 → 平台 406，上层以
///      「签名不可用」回报失败原因）。
///
/// 为什么必须走页面：x-s 的 x3 = window.mnsv2(...)（页面 webpack 里的混淆 SDK），
/// 纯 C# 无法复算（见 fangai/xiaohongshu/mnsv2.py 的未验证标注）。
/// 静态类：与 ApiClient 同款（App 层无参单例），由 App 在开壳时挂 HostWarmer。
/// </summary>
public static class XhsSignerService
{
    /// <summary>热宿主等待上限（首次激活 = 建 WebView2 环境 + 首次导航；冷启动实测可达 60s+，
    /// 之后同档案秒级 —— 宁可首次同步慢，也别让首个请求因超时裸发 406）</summary>
    private static readonly TimeSpan HostReadyTimeout = TimeSpan.FromSeconds(75);

    /// <summary>预热总预算（热宿主 + 等页面加载 + 借发布页拉起 mnsv2）</summary>
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromSeconds(120);

    /// <summary>单次签名预算（预热过就是毫秒级；没预热成功也别拖过 HttpClient 超时）</summary>
    private static readonly TimeSpan SignTimeout = TimeSpan.FromSeconds(25);

    private static readonly Dictionary<Guid, IXhsSignHost> _hosts = [];
    private static readonly object _lock = new();

    /// <summary>宿主热身回调（App 开壳时挂账号坞：sessionId → 该会话标签的 WebView 就绪）。
    /// 壳关闭/换会员时置空 —— 坞没了就别再等宿主。</summary>
    public static Func<Guid, TimeSpan, CancellationToken, Task<bool>>? HostWarmer { get; set; }

    // ═══ 注册（标签宿主调用；UI 线程） ═══

    public static void Register(Guid sessionId, IXhsSignHost host)
    {
        lock (_lock) _hosts[sessionId] = host;
        Log.Debug("[XhsSigner] 会话 {Session} 注册签名宿主", sessionId);
    }

    public static void Unregister(Guid sessionId)
    {
        lock (_lock) _hosts.Remove(sessionId);
        Log.Debug("[XhsSigner] 会话 {Session} 注销签名宿主", sessionId);
    }

    // ═══ 入口 ═══

    /// <summary>
    /// 预热签名宿主（SDK 调用之前调；失败不阻断 —— 请求仍会裸发，由平台判）。
    /// 首个请求前把 WebView 建好、页面加载完、mnsv2 就位，正式签名只花毫秒 ——
    /// 冷启动的十几秒加载不该压在 HttpClient 超时窗口里。
    /// </summary>
    public static Task<bool> PrepareAsync(Guid? sessionId, CancellationToken ct) =>
        InvokeUiAsync<bool>(() => PrepareCoreAsync(sessionId, ct), PrepareTimeout, "预热", false);

    /// <summary>签名入口（XhsPlatform.SetSigner 注入，任意线程）</summary>
    public static Task<XhsSignHeaders?> SignAsync(
        Guid? sessionId, string urlPath, string? bodyJson, CancellationToken ct) =>
        InvokeUiAsync<XhsSignHeaders?>(() => SignCoreAsync(sessionId, urlPath, bodyJson, ct),
            SignTimeout, "签名", null);

    /// <summary>
    /// UI 线程调度（WebView2 的 InvokeScript/Navigate 只能在 UI 线程；坞的状态也在 UI 线程）。
    /// 带超时兜底：调度器/宿主卡住时不能让同步流程悬着 —— 超时即按「签名不可用」收场
    /// （请求裸发 → 平台 406 → 上层回报失败原因，用户可重试），绝不挂死「正在同步」。
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
            Log.Warning(ex, "[XhsSign] UI 线程调度 {What} 失败", what);
            return fallback;
        }

        var done = await Task.WhenAny(job, Task.Delay(timeout));
        if (done != job)
        {
            Log.Warning("[XhsSign] UI 线程 {What} 超时 {Seconds}s —— 按不可用收场", what, timeout.TotalSeconds);
            _ = job.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return fallback;
        }
        try { return await job; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[XhsSign] UI 线程 {What} 异常", what);
            return fallback;
        }
    }

    private static async Task<bool> PrepareCoreAsync(Guid? sessionId, CancellationToken ct)
    {
        var host = await ResolveOrWarmAsync(sessionId, ct);
        if (host == null)
        {
            Log.Warning("[XhsSign] 无可用签名宿主（会话 {Session}）—— 跳过预热", sessionId);
            return false;
        }

        var ok = await host.PrepareAsync(ct);
        Log.Debug("[XhsSign] 预热结果：{Result}（会话 {Session}）", ok ? "就绪" : "不可用", sessionId);
        return ok;
    }

    private static async Task<XhsSignHeaders?> SignCoreAsync(
        Guid? sessionId, string urlPath, string? bodyJson, CancellationToken ct)
    {
        var host = await ResolveOrWarmAsync(sessionId, ct);
        if (host == null)
        {
            Log.Warning("[XhsSign] 无可用签名宿主（会话 {Session}）—— 本次请求不签名", sessionId);
            return null;
        }

        return await host.SignAsync(urlPath, bodyJson, ct);
    }

    /// <summary>精确会话优先；该会话没有可用宿主 → 请求坞热宿主；再不行退回任一已打开的小红书
    /// 账号标签（跨账号签名与请求 Cookie 不同档案 —— 兜底可用，非首选）。</summary>
    private static async Task<IXhsSignHost?> ResolveOrWarmAsync(Guid? sessionId, CancellationToken ct)
    {
        var host = Resolve(sessionId);
        if (host != null) return host;

        if (sessionId is { } sid && HostWarmer is { } warmer)
        {
            Log.Debug("[XhsSigner] 会话 {Session} 无签名宿主，请求坞热宿主", sid);
            if (await warmer(sid, HostReadyTimeout, ct)) return Resolve(sid);
        }

        return Resolve(null);
    }

    private static IXhsSignHost? Resolve(Guid? sessionId)
    {
        lock (_lock)
        {
            if (sessionId is { } sid && _hosts.TryGetValue(sid, out var exact))
                return exact;
            return _hosts.Count > 0 ? _hosts.Values.First() : null;
        }
    }
}
