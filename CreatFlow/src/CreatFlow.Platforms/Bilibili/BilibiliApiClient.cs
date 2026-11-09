using CreatFlow.Platforms.Core.Handlers;

namespace CreatFlow.Platforms.Bilibili;

/// <summary>
/// B站（哔哩哔哩创作中心）API 客户端。
///
/// 认证 = Cookie（<c>SESSDATA</c> 登录态 + <c>bili_jct</c> csrf），写接口在 query 里带
/// <c>csrf</c>；**没有 WBI（w_rid/wts）签名** —— 2026-11-04 抓包：视频投稿
/// <c>add/v3</c> 与封面 <c>cover/up</c> 都只带 csrf（WBI 只出现在专栏图片上传）。
///
/// 本客户端先只承载 2.5.5 的封面上传；阶段 4 接入视频投稿时在同一客户端上扩展
/// preupload / UPOS（见进度表 4.1）。
/// </summary>
internal sealed class BilibiliApiClient : IDisposable
{
    private readonly HttpClient _http;

    public BilibiliApiClient(string cookie, PlatformOptions options)
    {
        var loggingHandler = new LoggingHandler(null);
        var cookieHandler = new CookieAuthHandler(cookie);
        cookieHandler.InnerHandler = loggingHandler;

        _http = new HttpClient(cookieHandler)
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };

        var env = options.Environment;
        _http.DefaultRequestHeaders.Add("User-Agent", env?.UserAgent ?? options.UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", options.AcceptLanguage);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", options.BilibiliHost);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua",
            env?.SecChUa ?? "\"Not;A=Brand\";v=\"8\", \"Chromium\";v=\"150\", \"Google Chrome\";v=\"150\"");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile",
            env?.SecChUaMobile ?? "?0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform",
            env?.SecChUaPlatform ?? "\"Windows\"");
    }

    /// <summary>
    /// GET 一个 member 域接口并返回原始响应文本（非 2xx 抛 <see cref="BilibiliApiException"/>）。
    /// 话题 / 标签推荐都是 GET（抓包 2026-11-04：<c>/x/vupre/web/topic/type/v2</c>、
    /// <c>/x/vupre/web/tag/recommend</c>），只带 Cookie + csrf 之外的公共 query。
    /// </summary>
    public async Task<string> GetStringAsync(string url, string referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", referer);

        using var resp = await _http.SendAsync(req, ct);
        var body = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync(ct));
        if (!resp.IsSuccessStatusCode)
            throw new BilibiliApiException(
                $"B站 API HTTP {(int)resp.StatusCode}：{Snippet(body)}", (int)resp.StatusCode, body);
        return body;
    }

    /// <summary>
    /// POST 一个 JSON 请求并返回原始响应文本（非 2xx 抛 <see cref="BilibiliApiException"/>）。
    /// <c>add/v3</c> 投稿、UPOS 合并前的 archive 接口都走这条。
    /// </summary>
    public async Task<string> PostJsonAsync(
        string url, string json, string referer, CancellationToken ct)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.TryAddWithoutValidation("Referer", referer);

        using var resp = await _http.SendAsync(req, ct);
        var body = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync(ct));
        if (!resp.IsSuccessStatusCode)
            throw new BilibiliApiException(
                $"B站 API HTTP {(int)resp.StatusCode}：{Snippet(body)}", (int)resp.StatusCode, body);
        return body;
    }

    /// <summary>POST 一个 multipart 请求并返回原始响应文本（非 2xx 抛 <see cref="BilibiliApiException"/>）。</summary>
    public async Task<string> PostMultipartAsync(
        string url, HttpContent content, string referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.TryAddWithoutValidation("Referer", referer);

        using var resp = await _http.SendAsync(req, ct);
        // ReadAsByteArray + UTF-8 解码，绕开 Content-Type charset 不规范时的严格校验
        //（与 PlatformHttpClient 同口径）
        var body = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync(ct));
        if (!resp.IsSuccessStatusCode)
            throw new BilibiliApiException(
                $"B站 API HTTP {(int)resp.StatusCode}：{Snippet(body)}", (int)resp.StatusCode, body);
        return body;
    }

    public void Dispose() => _http.Dispose();

    private static string Snippet(string text) =>
        text.Length > 300 ? text[..300] + "…" : text;
}
