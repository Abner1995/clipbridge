using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Core.Handlers;
using CreatFlow.Platforms.Kuaishou.Handlers;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手 API 客户端 — 封装 HTTP 请求、签名、Cookie 认证。
///
/// Handler 链: CookieAuth → KuaishouApiPh → KuaishouSig → Logging → KuaishouPrimary → (WebView2 | HTTP)
/// 对标 Python fangai/kuaishou/api.py 的 KuaishouAPI 类。
/// </summary>
internal sealed class KuaishouApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly PlatformOptions _options;
    private readonly string _apiPh;
    private readonly string _cookie;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>管理页 Referer — 列表/删除/编辑操作使用。</summary>
    private string ManageReferer => $"{_options.KuaishouCpHost}/article/manage/video?status=0&from=publish";

    /// <summary>发布页 Referer — 上传/提交操作使用。</summary>
    private string PublishReferer => $"{_options.KuaishouCpHost}/article/publish/video?tabType=1";

    /// <summary>需要管理页 Referer 的路径特征。</summary>
    private static readonly string[] ManagePaths =
    [
        "/photo/list", "/delete", "/unpublished/delete",
        "/edit/", "/publish",  // /publish = 待发布→立即发布，也是管理页操作
    ];

    /// <summary>需要发布页 Referer 的路径特征。</summary>
    private static readonly string[] PublishPaths =
    [
        "/upload/", "/submit",
    ];

    public KuaishouApiClient(
        string cookie,
        PlatformOptions options,
        string? apiPh = null,
        ILogger<PlatformHttpClient>? logger = null,
        TimeSpan? timeout = null)
    {
        _cookie = cookie;
        _options = options;
        _apiPh = apiPh ?? "393eb14dff89997d06918e8c11c9e4946041";

        // Build handler chain (inner → outer): HTTP → Logging → Sig → ApiPh → CookieAuth
        var loggingHandler = new LoggingHandler(null);

        var sigHandler = new KuaishouSigHandler(options.SessionId);
        sigHandler.InnerHandler = loggingHandler;

        var apiPhHandler = new KuaishouApiPhHandler(_apiPh);
        apiPhHandler.InnerHandler = sigHandler;

        var cookieHandler = new CookieAuthHandler(cookie);
        cookieHandler.InnerHandler = apiPhHandler;

        _http = new HttpClient(cookieHandler)
        {
            // 封面图等二进制上传要按体积放行，调用方显式传 timeout 覆盖（默认沿用全局 30s）
            Timeout = timeout ?? TimeSpan.FromSeconds(options.TimeoutSeconds),
        };

        // Headers — exact match with browser (from Python examples)
        // 环境快照优先（与 PlatformHttpClient 同口径，2026-10-16）：有快照时 UA/client hints
        // 跟随登录 WebView2 的真实指纹；缺快照回落硬编码
        var env = options.Environment;
        _http.DefaultRequestHeaders.Add("User-Agent", env?.UserAgent ?? options.UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", options.KuaishouCpHost);
        // Referer is set per-request (see GetRefererForPath) — NOT hardcoded here
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("returnSetRootDomainLoginUrl", "true");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua",
            env?.SecChUa ?? "\"Not=A?Brand\";v=\"99\", \"Google Chrome\";v=\"151\", \"Chromium\";v=\"151\"");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", env?.SecChUaMobile ?? "?0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", env?.SecChUaPlatform ?? "\"Windows\"");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Connection", "keep-alive");

        // Add kww header from cookie
        var kww = ExtractCookieValue(cookie, "kwfv1");
        if (!string.IsNullOrWhiteSpace(kww))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("kww", kww);
    }

    // ═══ Referer 自动匹配 ═══

    /// <summary>根据 API 路径返回正确的 Referer。</summary>
    private string GetRefererForPath(string path)
    {
        foreach (var p in PublishPaths)
            if (path.Contains(p, StringComparison.OrdinalIgnoreCase))
                return PublishReferer;
        foreach (var m in ManagePaths)
            if (path.Contains(m, StringComparison.OrdinalIgnoreCase))
                return ManageReferer;
        // 默认管理页（userInfo 等）
        return ManageReferer;
    }

    /// <summary>创建带正确 Referer 的 HttpRequestMessage（<paramref name="referer"/> 非空时覆盖默认推断）。</summary>
    private HttpRequestMessage CreateRequest(
        HttpMethod method, string url, HttpContent? content, string path, string? referer = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.TryAddWithoutValidation("Referer", referer ?? GetRefererForPath(path));
        return req;
    }

    // ═══ POST JSON (with envelope check) ═══

    /// <summary>发送 POST JSON 请求，自动签名 + 检查 envelope result==1。</summary>
    public Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default)
        where T : class
        => PostAsync<T>(path, body, referer: null, ct);

    /// <summary>
    /// 同 <see cref="PostAsync{T}(string, object?, CancellationToken)"/>，但可指定 Referer。
    /// 活动中心（<c>/rest/v2/creator/activity/*</c>）页面发出的 Referer 是发布页，
    /// 与 <see cref="GetRefererForPath"/> 的默认管理页不同 —— 平台对 Referer 敏感，按抓包原文来。
    /// </summary>
    public async Task<T> PostAsync<T>(
        string path, object? body, string? referer, CancellationToken ct = default)
        where T : class
    {
        var url = $"{_options.KuaishouCpHost}{path}";
        HttpContent? content = null;
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonOpts);
            content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json;charset=UTF-8");
        }

        using var request = CreateRequest(HttpMethod.Post, url, content, path, referer);
        var response = await _http.SendAsync(request, ct);
        var bodyText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var snippet = bodyText.Length > 500 ? bodyText[..500] : bodyText;
            throw new HttpRequestException($"Kuaishou {path} → HTTP {(int)response.StatusCode}: {snippet}");
        }

        var envelope = JsonSerializer.Deserialize<Kuaishou.ApiEnvelope<T>>(bodyText, JsonOpts);
        if (envelope is null)
            throw new Exception($"Kuaishou {path}: invalid JSON response");

        if (envelope.Result != 1)
            throw new KuaishouApiException(
                $"Kuaishou {path}: result={envelope.Result}, msg={envelope.Message ?? "?"}",
                envelope.Result, bodyText);

        if (envelope.Data is null)
            throw new Exception($"Kuaishou {path}: null data in response");

        return envelope.Data;
    }

    /// <summary>POST JSON — 无 envelope（用于 poi/search、submit）。</summary>
    public async Task<T> PostRawAsync<T>(string path, object? body, CancellationToken ct = default)
        where T : class
    {
        var url = $"{_options.KuaishouCpHost}{path}";
        HttpContent? content = null;
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonOpts);
            content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json;charset=UTF-8");
        }

        using var request = CreateRequest(HttpMethod.Post, url, content, path);
        var response = await _http.SendAsync(request, ct);
        var bodyText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var snippet = bodyText.Length > 500 ? bodyText[..500] : bodyText;
            throw new HttpRequestException($"Kuaishou {path} → HTTP {(int)response.StatusCode}: {snippet}");
        }

        var result = JsonSerializer.Deserialize<T>(bodyText, JsonOpts);
        return result ?? throw new Exception($"Kuaishou {path}: invalid JSON response");
    }

    /// <summary>
    /// POST JSON — 自定义序列化选项 + 无 envelope（用于特殊响应格式）。
    /// </summary>
    public async Task<T> PostWithOptionsAsync<T>(
        string path, object? body, JsonSerializerOptions options, CancellationToken ct = default)
        where T : class
    {
        var url = $"{_options.KuaishouCpHost}{path}";
        HttpContent? content = null;
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, options);
            content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json;charset=UTF-8");
        }

        using var request = CreateRequest(HttpMethod.Post, url, content, path);
        var response = await _http.SendAsync(request, ct);
        var bodyText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var snippet = bodyText.Length > 500 ? bodyText[..500] : bodyText;
            throw new HttpRequestException($"Kuaishou {path} → HTTP {(int)response.StatusCode}: {snippet}");
        }

        // Check envelope
        var envelope = JsonSerializer.Deserialize<Kuaishou.ApiEnvelope<T>>(bodyText, options);
        if (envelope is null)
            throw new Exception($"Kuaishou {path}: invalid JSON response");

        if (envelope.Result != 1)
            throw new KuaishouApiException(
                $"Kuaishou {path}: result={envelope.Result}, msg={envelope.Message ?? "?"}",
                envelope.Result, bodyText);

        if (envelope.Data is null)
            throw new Exception($"Kuaishou {path}: null data in response");

        return envelope.Data;
    }

    /// <summary>
    /// POST multipart/form-data（封面图等二进制上传）→ 原始响应文本。
    ///
    /// 与 JSON 请求走同一条 Handler 链（Cookie / Referer 照常注入），但
    /// <see cref="Handlers.KuaishouSigHandler"/> 对 multipart **不签名** ——
    /// 2026-11-05 抓包证实页面里的 FormData 请求不带 __NS_sig3。
    /// 响应形状由调用方解析（封面端点抓包未覆盖，见 <see cref="KuaishouCoverService"/>）。
    /// </summary>
    public async Task<string> PostMultipartTextAsync(
        string path, HttpContent content, CancellationToken ct = default)
    {
        var url = $"{_options.KuaishouCpHost}{path}";
        using var request = CreateRequest(HttpMethod.Post, url, content, path);
        var response = await _http.SendAsync(request, ct);
        var bodyText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var snippet = bodyText.Length > 500 ? bodyText[..500] : bodyText;
            throw new HttpRequestException($"Kuaishou {path} → HTTP {(int)response.StatusCode}: {snippet}");
        }

        return bodyText;
    }

    /// <summary>发送 POST 请求返回原始响应文本（CDN 上传用）。</summary>
    public async Task<string> PostRawTextAsync(
        string url,
        HttpContent content,
        CancellationToken ct = default)
    {
        var response = await _http.PostAsync(url, content, ct);
        var bodyText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var snippet = bodyText.Length > 500 ? bodyText[..500] : bodyText;
            throw new HttpRequestException($"Kuaishou CDN → HTTP {(int)response.StatusCode}: {snippet}");
        }
        return bodyText;
    }

    /// <summary>发送 GET 请求返回原始响应文本。</summary>
    public async Task<string> GetRawTextAsync(string url, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(url, ct);
        var bodyText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var snippet = bodyText.Length > 500 ? bodyText[..500] : bodyText;
            throw new HttpRequestException($"Kuaishou GET → HTTP {(int)response.StatusCode}: {snippet}");
        }
        return bodyText;
    }

    public void Dispose() => _http.Dispose();

    private static string? ExtractCookieValue(string cookie, string name)
    {
        foreach (var item in cookie.Split(';'))
        {
            var trimmed = item.Trim();
            var eq = trimmed.IndexOf('=');
            if (eq > 0 && trimmed[..eq] == name)
                return trimmed[(eq + 1)..];
        }
        return null;
    }
}

/// <summary>快手 API 错误。</summary>
public sealed class KuaishouApiException : Exception
{
    public int ResultCode { get; }
    public string? ResponseBody { get; }

    public KuaishouApiException(string message, int resultCode, string? responseBody = null) : base(message)
    {
        ResultCode = resultCode;
        ResponseBody = responseBody;
    }
}

internal static class ApiEnvelopeFactory { }

internal sealed class ApiEnvelope<T>
{
    [System.Text.Json.Serialization.JsonPropertyName("result")]
    public int Result { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public T? Data { get; set; }
}
