using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Core.Handlers;
using CreatFlow.Platforms.Xiaohongshu.Handlers;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书 API 客户端 — 封装 HTTP 请求、Cookie 认证、x-s 签名。
///
/// Handler 链: CookieAuth → XhsSign → Logging → HTTP
/// 对标 Python fangai/xiaohongshu/api.py 的 XhsAPI 类。
///
/// API 端点分配:
///   - creator.xiaohongshu.com — 上传凭证 (permit)
///   - edith.xiaohongshu.com   — 合集 CRUD、视频管理、笔记操作
/// </summary>
internal sealed class XhsApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly PlatformOptions _options;
    private readonly string _cookie;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Creator Referer — 同 Python 逆向的 referer: https://creator.xiaohongshu.com/</summary>
    private string CreatorReferer => $"{_options.XhsCreatorHost}/";

    public XhsApiClient(
        string cookie,
        PlatformOptions options,
        ILogger<PlatformHttpClient>? logger = null)
    {
        _cookie = cookie;
        _options = options;

        // Handler chain (inner → outer): HTTP → Logging → XhsSign → CookieAuth
        var loggingHandler = new LoggingHandler(null);

        var signHandler = new XhsSignHandler(options.SessionId);
        signHandler.InnerHandler = loggingHandler;

        var cookieHandler = new CookieAuthHandler(cookie);
        cookieHandler.InnerHandler = signHandler;

        _http = new HttpClient(cookieHandler)
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };

        // Browser-matching headers
        // 环境快照优先（与 PlatformHttpClient 同口径，2026-10-16）：有快照时 UA/client hints
        // 跟随登录 WebView2 的真实指纹；缺快照回落硬编码
        var env = options.Environment;
        _http.DefaultRequestHeaders.Add("User-Agent", env?.UserAgent ?? options.UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", options.AcceptLanguage);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", ""); // empty auth (matches browser)
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", options.XhsCreatorHost);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua",
            env?.SecChUa ?? "\"Not=A?Brand\";v=\"99\", \"Google Chrome\";v=\"151\", \"Chromium\";v=\"151\"");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", env?.SecChUaMobile ?? "?0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", env?.SecChUaPlatform ?? "\"Windows\"");
    }

    // ═══ Public API ═══

    /// <summary>发送 POST JSON 请求到 edith.xiaohongshu.com。</summary>
    public async Task<T> PostEdithAsync<T>(string path, object? body, CancellationToken ct = default)
        where T : class
    {
        var url = $"{_options.XhsEdithHost}{path}";
        return await PostJsonInternalAsync<T>(url, path, body, ct);
    }

    /// <summary>发送 GET 请求到 edith.xiaohongshu.com。</summary>
    public async Task<T> GetEdithAsync<T>(string path, Dictionary<string, string>? query = null, CancellationToken ct = default)
        where T : class
    {
        var url = BuildUrl(_options.XhsEdithHost, path, query);
        return await GetJsonInternalAsync<T>(url, path, ct);
    }

    /// <summary>发送 POST JSON 请求到 creator.xiaohongshu.com。</summary>
    public async Task<T> PostCreatorAsync<T>(string path, object? body, CancellationToken ct = default)
        where T : class
    {
        var url = $"{_options.XhsCreatorHost}{path}";
        return await PostJsonInternalAsync<T>(url, path, body, ct);
    }

    /// <summary>发送 GET 请求到 creator.xiaohongshu.com。</summary>
    public async Task<T> GetCreatorAsync<T>(string path, Dictionary<string, string>? query = null, CancellationToken ct = default)
        where T : class
    {
        var url = BuildUrl(_options.XhsCreatorHost, path, query);
        return await GetJsonInternalAsync<T>(url, path, ct);
    }

    /// <summary>发送 POST JSON 请求返回原始 JsonElement（无固定结构）。</summary>
    public async Task<JsonElement> PostEdithRawAsync(string path, object? body, CancellationToken ct = default)
    {
        var url = $"{_options.XhsEdithHost}{path}";
        var content = SerializeBody(body);
        using var request = CreateRequest(HttpMethod.Post, url, content, path);
        return await SendRawAsync(request, path, ct);
    }

    /// <summary>发送 PUT JSON 请求返回原始 JsonElement（无固定结构，用于 note/update）。</summary>
    public async Task<JsonElement> PutEdithRawAsync(string path, object? body, CancellationToken ct = default)
    {
        var url = $"{_options.XhsEdithHost}{path}";
        var content = SerializeBody(body);
        using var request = CreateRequest(HttpMethod.Put, url, content, path);
        return await SendRawAsync(request, path, ct);
    }

    /// <summary>发送 GET 请求返回原始 JsonElement。</summary>
    public async Task<JsonElement> GetEdithRawAsync(string path, Dictionary<string, string>? query = null, CancellationToken ct = default)
    {
        var url = BuildUrl(_options.XhsEdithHost, path, query);
        using var request = CreateRequest(HttpMethod.Get, url, null, path);
        return await SendRawAsync(request, path, ct);
    }

    /// <summary>发送 GET 请求返回原始 JsonElement（creator host）。</summary>
    public async Task<JsonElement> GetCreatorRawAsync(string path, Dictionary<string, string>? query = null, CancellationToken ct = default)
    {
        var url = BuildUrl(_options.XhsCreatorHost, path, query);
        using var request = CreateRequest(HttpMethod.Get, url, null, path);
        return await SendRawAsync(request, path, ct);
    }

    /// <summary>发送 POST JSON 请求返回原始 JsonElement（creator host）。</summary>
    public async Task<JsonElement> PostCreatorRawAsync(string path, object? body, CancellationToken ct = default)
    {
        var url = $"{_options.XhsCreatorHost}{path}";
        var content = SerializeBody(body);
        using var request = CreateRequest(HttpMethod.Post, url, content, path);
        return await SendRawAsync(request, path, ct);
    }

    private async Task<JsonElement> SendRawAsync(HttpRequestMessage request, string path, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, ct);
        var bodyText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw StatusError("XHS", path, bodyText, (int)response.StatusCode);

        using var doc = JsonDocument.Parse(bodyText);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// 非 2xx 统一抛错。风控码（461/462/465/471）翻成人话，别把假成功壳原样甩给用户
    /// （2026-10-25 实测三连 461，队列行里只显示一段 {"success":true} 的 JSON，看不出该怎么办）。
    /// </summary>
    private static HttpRequestException StatusError(string prefix, string path, string bodyText, int status)
    {
        if (XhsRiskBlock.HintFor(status) is { } hint)
            return new HttpRequestException($"{hint}（{path} → HTTP {status}）");

        var snippet = bodyText.Length > 500 ? bodyText[..500] : bodyText;
        return new HttpRequestException($"{prefix} {path} → HTTP {status}: {snippet}");
    }

    // ═══ Internal ═══

    private async Task<T> PostJsonInternalAsync<T>(string url, string path, object? body, CancellationToken ct)
        where T : class
    {
        var content = SerializeBody(body);
        using var request = CreateRequest(HttpMethod.Post, url, content, path);
        var response = await _http.SendAsync(request, ct);
        var bodyText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw StatusError("XHS", path, bodyText, (int)response.StatusCode);

        var result = JsonSerializer.Deserialize<T>(bodyText, JsonOpts);
        return result ?? throw new Exception($"XHS {path}: null or invalid JSON response");
    }

    private async Task<T> GetJsonInternalAsync<T>(string url, string path, CancellationToken ct)
        where T : class
    {
        using var request = CreateRequest(HttpMethod.Get, url, null, path);
        var response = await _http.SendAsync(request, ct);
        var bodyText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw StatusError("XHS GET", path, bodyText, (int)response.StatusCode);

        var result = JsonSerializer.Deserialize<T>(bodyText, JsonOpts);
        return result ?? throw new Exception($"XHS GET {path}: null or invalid JSON response");
    }

    // ═══ Helpers ═══

    private static string BuildUrl(string baseHost, string path, Dictionary<string, string>? query)
    {
        var url = $"{baseHost}{path}";
        if (query is { Count: > 0 })
        {
            var qs = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            url = $"{url}?{qs}";
        }
        return url;
    }

    private static HttpContent? SerializeBody(object? body)
    {
        if (body is null) return null;
        var json = JsonSerializer.Serialize(body, JsonOpts);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.TryAddWithoutValidation("Content-Type", "application/json;charset=UTF-8");
        return content;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, HttpContent? content, string path)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.TryAddWithoutValidation("Referer", CreatorReferer);
        return req;
    }

    public void Dispose() => _http.Dispose();
}
