using System.Net.Http.Headers;
using CreatFlow.Platforms.Core.Handlers;
using CreatFlow.Platforms.Toutiao.Requests;

namespace CreatFlow.Platforms.Toutiao;

/// <summary>
/// 今日头条（创作者平台）API 客户端。
///
/// 认证：Cookie 登录态 + <c>x-secsdk-csrf-token</c>（页面 secsdk 现签，Cookie 里没有；
/// 由 <c>AccountLoginExtractor</c> 从 WebView 请求头抓取后存 extra_data.csrfToken）。
/// 写接口（PublishPseries / DeletePseries）必须带这个头，缺了会被平台拒。
/// </summary>
internal sealed class ToutiaoApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <summary>Cookie 容器 —— 用 Jar 模式才能接住响应里的 Set-Cookie（补 xigua_csrf_token）。</summary>
    private readonly CookieJar _jar;

    private readonly PlatformOptions _options;

    /// <summary>secsdk 现签 token（WebView 抓取），兜底用。</summary>
    private readonly string? _secsdkCsrfToken;

    private bool _csrfWarmupTried;

    public ToutiaoApiClient(
        string cookie, PlatformOptions options, string? csrfToken, string referer)
    {
        // DelegatingHandler 必须接上下一个 handler —— 漏了 InnerHandler 时
        // 每次请求都会抛 "The inner handler has not been assigned."（2026-11-05 实测）。
        _options = options;
        _jar = new CookieJar(cookie);
        var cookieHandler = new CookieAuthHandler(_jar);
        var loggingHandler = new LoggingHandler(null);
        cookieHandler.InnerHandler = loggingHandler;

        _http = new HttpClient(cookieHandler)
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };

        var env = options.Environment;
        _http.DefaultRequestHeaders.Add("User-Agent", env?.UserAgent ?? options.UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", options.AcceptLanguage);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", referer);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", options.ToutiaoHost);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua",
            env?.SecChUa ?? "\"Not=A?Brand\";v=\"99\", \"Google Chrome\";v=\"151\", \"Chromium\";v=\"151\"");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile",
            env?.SecChUaMobile ?? "?0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform",
            env?.SecChUaPlatform ?? "\"Windows\"");

        // 写接口必需；GET 列表接口多带也无害（页面请求同样带）
        _secsdkCsrfToken = csrfToken;
        if (!string.IsNullOrWhiteSpace(csrfToken))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("x-secsdk-csrf-token", csrfToken);

        if (string.IsNullOrWhiteSpace(ExtractCookieValue(_jar.GetCookieString(), "xigua_csrf_token"))
            && !string.IsNullOrWhiteSpace(csrfToken))
        {
            // 缺了它平台会回 403 missing csrf token —— POST 前会尝试从 xigua 页面 Set-Cookie 补
            Serilog.Log.Debug("Toutiao 会话 cookie 缺少 xigua_csrf_token，POST 前尝试补取");
        }
    }

    /// <summary>从 Cookie 串里取一个值（页面侧 decodeURIComponent，这里同样解码）；同名取最后一个（最新）。</summary>
    private static string? ExtractCookieValue(string cookie, string name)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return null;
        string? found = null;
        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (!part.AsSpan(0, eq).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var value = part[(eq + 1)..];
            try { found = Uri.UnescapeDataString(value); }
            catch { found = value; }
        }
        return found;
    }

    /// <summary>
    /// 会话 cookie 里没有 <c>xigua_csrf_token</c> 时，访问一次 xigua 页面让服务端 Set-Cookie。
    /// 该 cookie 按 <c>/profile_v4/xigua</c> 路径下发，账号窗口停在 <c>/profile_v4/index</c> 时取不到。
    /// </summary>
    private async Task<string?> EnsureXiguaCsrfAsync(CancellationToken ct)
    {
        if (_csrfWarmupTried) return null;
        _csrfWarmupTried = true;

        // 页面 HTML 有可能不下发，xigua 的接口（getAuthKey / 列表）也可能带 Set-Cookie —— 全试一遍。
        // 页面请求要带 text/html 的 Accept：默认的 application/json 可能让服务端走 API 分支、不下发 cookie。
        var pageUrls = new[]
        {
            "/profile_v4/xigua/upload-video?index=2",
            "/profile_v4/xigua/content-manage-collections?action=pSeriesManage",
        };
        var apiUrls = new[]
        {
            "/xigua/api/upload/getAuthKey/?params=" + Uri.EscapeDataString(
                "{\"type\":\"image_temp\",\"column\":false,\"ugc\":false,\"useImageX\":true,\"useStsToken\":false}"),
            "/xigua/api/pSeries/GetAlbumInfoByMediaId/?params=" + Uri.EscapeDataString(
                "{\"Offset\":0,\"SortType\":1}"),
        };

        using var pageHttp = CreatePageClient();
        foreach (var path in pageUrls)
        {
            try
            {
                using var resp = await pageHttp.GetAsync(_options.ToutiaoHost + path, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                Serilog.Log.Debug("Toutiao csrf 预热页面 {Path} → HTTP {Status} {FinalUrl} len={Len}",
                    path, (int)resp.StatusCode, resp.RequestMessage?.RequestUri, body.Length);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "Toutiao csrf 预热页面 {Path} 失败", path);
            }

            var token = ExtractCookieValue(_jar.GetCookieString(), "xigua_csrf_token");
            if (!string.IsNullOrWhiteSpace(token))
            {
                Serilog.Log.Debug("Toutiao xigua_csrf_token 已从 xigua 页面补到（{Path}）", path);
                return token;
            }
        }

        foreach (var path in apiUrls)
        {
            try
            {
                using var resp = await _http.GetAsync(_options.ToutiaoHost + path, ct);
                _ = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "Toutiao csrf 预热接口 {Path} 失败", path);
            }

            var token = ExtractCookieValue(_jar.GetCookieString(), "xigua_csrf_token");
            if (!string.IsNullOrWhiteSpace(token))
            {
                Serilog.Log.Debug("Toutiao xigua_csrf_token 已从 xigua 接口补到（{Path}）", path);
                return token;
            }
        }

        Serilog.Log.Warning(
            "Toutiao xigua 预热（页面 + getAuthKey + 列表）后仍未拿到 xigua_csrf_token；Cookie 名={Names}",
            string.Join(",", _jar.GetCookieString()
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Split('=')[0].Trim())
                .Distinct()));
        return null;
    }

    /// <summary>页面预热专用客户端：Accept 用 text/html（默认的 JSON Accept 可能让服务端不下发 cookie）。</summary>
    private HttpClient CreatePageClient()
    {
        var handler = new CookieAuthHandler(_jar);
        handler.InnerHandler = new LoggingHandler(null);
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds) };
        var env = _options.Environment;
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            env?.UserAgent ?? _options.UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", _options.AcceptLanguage);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer",
            _options.ToutiaoHost + "/profile_v4/index");
        return http;
    }

    /// <summary>
    /// 写接口的 <c>x-csrf-token</c>：优先取 Cookie 的 <c>xigua_csrf_token</c>；
    /// 没有就访问 xigua 页面补一次；仍没有则退回 secsdk token（至少能把
    /// 「missing csrf」和「invalid csrf」区分开）。
    /// </summary>
    private async Task<string?> ResolveXiguaCsrfAsync(CancellationToken ct)
    {
        var fromCookie = ExtractCookieValue(_jar.GetCookieString(), "xigua_csrf_token");
        if (!string.IsNullOrWhiteSpace(fromCookie)) return fromCookie;

        var warmed = await EnsureXiguaCsrfAsync(ct);
        if (!string.IsNullOrWhiteSpace(warmed)) return warmed;

        if (!string.IsNullOrWhiteSpace(_secsdkCsrfToken))
        {
            Serilog.Log.Warning(
                "Toutiao xigua_csrf_token 仍缺失，x-csrf-token 暂用 secsdk token 兜底（若平台回 invalid csrf 即说明两者不等价）");
            return _secsdkCsrfToken;
        }
        return null;
    }

    public async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct = default)
    {
        var body = await GetStringAsync(url, ct);
        return string.IsNullOrWhiteSpace(body)
            ? default
            : JsonSerializer.Deserialize<T>(body, JsonOpts);
    }

    /// <summary>GET 原始响应文本（非 2xx 抛 <see cref="HttpRequestException"/>）。</summary>
    public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Toutiao GET {url} → HTTP {(int)resp.StatusCode}: {Snippet(body)}");
        return body;
    }

    /// <summary>
    /// 当前登录用户的 <c>user_id</c>（文章合集接口都要它；TOS 的 X-Storage-U 也用这个值）。
    /// </summary>
    public async Task<string?> GetUserIdAsync(CancellationToken ct = default)
    {
        var info = await GetJsonAsync<ToutiaoUserInfoResponse>(
            _options.ToutiaoHost + "/mp/agw/general/user/get_user_info", ct);
        return info?.Data?.Data?.UserId;
    }

    /// <summary>
    /// multipart 上传（文章合集封面：POST /spice/image，字段名 image）。
    /// 与普通 JSON 请求共用 CookieJar 和 secsdk 头。
    /// </summary>
    public async Task<T?> PostMultipartAsync<T>(
        string url, string fieldName, byte[] data, string fileName, string contentType,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, fieldName, fileName);

        using var resp = await _http.PostAsync(url, form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Toutiao multipart POST {url} → HTTP {(int)resp.StatusCode}: {Snippet(body)}");
        return string.IsNullOrWhiteSpace(body)
            ? default
            : JsonSerializer.Deserialize<T>(body, JsonOpts);
    }

    /// <summary>
    /// 表单 POST（文章合集删除是 x-www-form-urlencoded）。
    /// 不抛 HTTP 异常 —— 调用方需要根据平台壳里的业务码判成功。
    /// </summary>
    public async Task<(int Status, string Body)> PostFormAllowErrorAsync(
        string url, IDictionary<string, string> fields, CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var resp = await _http.PostAsync(url, content, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    public async Task<T?> PostJsonAsync<T>(string url, object body, CancellationToken ct = default)
    {
        var json = await PostJsonRawAsync(url, body, ct);
        return string.IsNullOrWhiteSpace(json)
            ? default
            : JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    public async Task<string> PostJsonRawAsync(
        string url, object body, CancellationToken ct = default)
    {
        var (status, responseBody) =
            await SendJsonAllowErrorAsync(url, body, useXiguaCsrf: true, ct);
        if (status is < 200 or >= 300)
            throw new HttpRequestException(
                $"Toutiao POST {url} → HTTP {status}: {Snippet(responseBody)}");
        return responseBody;
    }

    /// <summary>
    /// 发送 JSON POST 并返回原始状态码/响应体（**不抛 HTTP 异常**）。
    ///
    /// <paramref name="useXiguaCsrf"/>：xigua 写接口（视频合集 pSeries）需要
    /// Cookie 里的 <c>xigua_csrf_token</c>；文章合集接口（/mp/fe_api/*）只用
    /// secsdk 头，传 false 可跳过 xigua 预热，避免多打一串无关请求。
    /// </summary>
    public async Task<(int Status, string Body)> SendJsonAllowErrorAsync(
        string url, object body, bool useXiguaCsrf = true, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);

        if (!useXiguaCsrf)
            return await SendPostAsync(url, json, xiguaCsrf: null, ct);

        // 只给 POST 带（页面 requestJson 也是只有 POST 拼这个头）
        var xiguaCsrf = await ResolveXiguaCsrfAsync(ct);
        var (status, responseBody) = await SendPostAsync(url, json, xiguaCsrf, ct);

        // 平台可能在 403 响应里下新的 xigua_csrf_token（CookieJar 已接住 Set-Cookie）——
        // 若确实拿到了新值，带它重试一次（缺 → 有 的常见自愈路径）。
        if (status == 403
            && responseBody.Contains("missing csrf token", StringComparison.OrdinalIgnoreCase))
        {
            var retryToken = ExtractCookieValue(_jar.GetCookieString(), "xigua_csrf_token");
            if (!string.IsNullOrWhiteSpace(retryToken) && retryToken != xiguaCsrf)
            {
                Serilog.Log.Debug(
                    "Toutiao 403 missing csrf token → 用响应新下发的 xigua_csrf_token 重试一次");
                (status, responseBody) = await SendPostAsync(url, json, retryToken, ct);
            }
        }

        return (status, responseBody);
    }

    private async Task<(int Status, string Body)> SendPostAsync(
        string url, string json, string? xiguaCsrf, CancellationToken ct)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(xiguaCsrf))
            request.Headers.TryAddWithoutValidation("x-csrf-token", xiguaCsrf);

        using var resp = await _http.SendAsync(request, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private static string Snippet(string body) =>
        body.Length <= 500 ? body : body[..500];

    public void Dispose() => _http.Dispose();
}
