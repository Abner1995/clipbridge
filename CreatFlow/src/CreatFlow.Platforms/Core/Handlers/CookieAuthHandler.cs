namespace CreatFlow.Platforms.Core.Handlers;

/// <summary>
/// Cookie 认证 Handler — 将 Cookie 注入到每个请求的 Cookie 头，
/// 并从响应的 Set-Cookie 头自动更新 CookieJar。
///
/// 支持两种模式：
///   1. CookieJar 模式 — 响应 Set-Cookie 自动回写 jar
///   2. 静态字符串/Func 模式 — 向后兼容
/// </summary>
public sealed class CookieAuthHandler : DelegatingHandler
{
    private readonly Func<string> _cookieProvider;
    private readonly CookieJar? _jar;

    /// <summary>CookieJar 模式 — 响应 Set-Cookie 自动回写。</summary>
    public CookieAuthHandler(CookieJar jar)
    {
        _jar = jar ?? throw new ArgumentNullException(nameof(jar));
        _cookieProvider = () => jar.GetCookieString();
    }

    /// <summary>Func 模式 — 每次请求动态获取 cookie。</summary>
    public CookieAuthHandler(Func<string> cookieProvider)
    {
        _cookieProvider = cookieProvider ?? (() => "");
    }

    /// <summary>静态字符串模式 — 向后兼容。</summary>
    public CookieAuthHandler(string cookie)
        : this(() => cookie) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var cookie = _cookieProvider();
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        var response = await base.SendAsync(request, ct);

        // CookieJar 模式：捕获 Set-Cookie 自动更新
        if (_jar != null && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            _jar.FeedSetCookies(setCookies);
        }

        return response;
    }
}
