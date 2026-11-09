namespace CreatFlow.Platforms.Core;

/// <summary>
/// 平台 HTTP 客户端封装 — 预配置 User-Agent、Accept-Language 等通用请求头。
/// 每个平台实例化时创建，不 shared（可以有不同的 BaseAddress 和 middleware）。
/// </summary>
public sealed class PlatformHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly PlatformOptions _options;
    private readonly ILogger<PlatformHttpClient>? _logger;

    public PlatformHttpClient(
        HttpMessageHandler handler,
        PlatformOptions options,
        ILogger<PlatformHttpClient>? logger = null,
        string? origin = null,
        string? referer = null)
    {
        _options = options;
        _logger = logger;
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };

        // 通用请求头 — 有环境快照用真实指纹（WebView2 登录时采集），无则 fallback 硬编码 Chrome/150
        var env = options.Environment;
        _http.DefaultRequestHeaders.Add("User-Agent", env?.UserAgent ?? options.UserAgent);
        _http.DefaultRequestHeaders.Add("Accept-Language", options.AcceptLanguage);
        _http.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");

        // Origin / Referer — 各平台不同，允许覆盖（默认抖音）
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer",
            referer ?? "https://creator.douyin.com/creator-micro/content/collection/manage");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin",
            origin ?? "https://creator.douyin.com");

        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua",
            env?.SecChUa ?? "\"Not;A=Brand\";v=\"8\", \"Chromium\";v=\"150\", \"Google Chrome\";v=\"150\"");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", env?.SecChUaMobile ?? "?0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform",
            env?.SecChUaPlatform ?? "\"Windows\"");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-dest", "empty");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-mode", "cors");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("sec-fetch-site", "same-origin");
    }

    // ═══ GET ═══

    /// <summary>发送 GET 请求并返回原始响应文本。</summary>
    public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        _logger?.LogDebug("GET {Url}", url);
        var resp = await _http.GetAsync(url, ct);
        // ReadAsByteArray → UTF-8 解码，绕开 ReadAsStringAsync 对 Content-Type charset 的严格校验
        // 抖音 API 偶尔返回不规范的 Content-Type，导致 "The character set provided in ContentType is invalid"
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var body = Encoding.UTF8.GetString(bytes);
        if (!resp.IsSuccessStatusCode)
        {
            var snippet = body.Length > 500 ? body[..500] : body;
            _logger?.LogError("GET {Url} → HTTP {Code}: {Body}",
                url, (int)resp.StatusCode, snippet);
            throw new HttpRequestException(
                $"Response status code {(int)resp.StatusCode}: {snippet}");
        }
        return body;
    }

    /// <summary>发送 GET 请求并反序列化为 JSON。</summary>
    public async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct = default)
    {
        var json = await GetStringAsync(url, ct);
        // 空响应体 → 返回 default (如 historyMedia 翻到末页、POI 无结果等)
        if (string.IsNullOrWhiteSpace(json))
        {
            _logger?.LogDebug("GetJsonAsync<{Type}> → empty body, returning null", typeof(T).Name);
            return default;
        }
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    // ═══ POST ═══

    /// <summary>发送 POST 请求（JSON body）并返回原始响应文本。</summary>
    public async Task<string> PostJsonAsync(
        string url, object? body, CancellationToken ct = default)
    {
        _logger?.LogDebug("POST {Url}", url);
        var content = body is not null
            ? new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8, "application/json")
            : null;

        var resp = await _http.PostAsync(url, content, ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var headers = string.Join("; ", resp.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
            var snippet = responseBody.Length > 1000 ? responseBody[..1000] : responseBody;
            _logger?.LogError("POST {Url} → HTTP {Code}, headers=[{Headers}], body={Body}",
                url, (int)resp.StatusCode, headers, snippet);
            // 提取关键 headers 用于异常消息（完整 headers 记录在日志中）
            var keyHeaders = string.Join("; ", resp.Headers
                .Where(h => h.Key.StartsWith("Sign-Res-") || h.Key.StartsWith("Bd-")
                    || h.Key == "Server" || h.Key == "x-alicdn-da-ups-status")
                .Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
            var msg = $"HTTP {(int)resp.StatusCode}"
                + (snippet.Length > 0 ? $": {snippet[..Math.Min(200, snippet.Length)]}" : " (empty body)")
                + (keyHeaders.Length > 0 ? $" [{keyHeaders}]" : "");
            throw new HttpRequestException(msg);
        }
        return responseBody;
    }

    /// <summary>发送 POST 请求（Form 表单）并返回原始响应文本。</summary>
    public async Task<string> PostFormAsync(
        string url, Dictionary<string, string> form, CancellationToken ct = default)
    {
        _logger?.LogDebug("POST (form) {Url}", url);
        var resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var headers = string.Join("; ", resp.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
            var snippet = responseBody.Length > 1000 ? responseBody[..1000] : responseBody;
            _logger?.LogError("POST (form) {Url} → HTTP {Code}, headers=[{Headers}], body={Body}",
                url, (int)resp.StatusCode, headers, snippet);
            // 提取关键 headers 用于异常消息（完整 headers 记录在日志中）
            var keyHeaders = string.Join("; ", resp.Headers
                .Where(h => h.Key.StartsWith("Sign-Res-") || h.Key.StartsWith("Bd-")
                    || h.Key == "Server" || h.Key == "x-alicdn-da-ups-status")
                .Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
            var msg = $"HTTP {(int)resp.StatusCode}"
                + (snippet.Length > 0 ? $": {snippet[..Math.Min(200, snippet.Length)]}" : " (empty body)")
                + (keyHeaders.Length > 0 ? $" [{keyHeaders}]" : "");
            throw new HttpRequestException(msg);
        }
        return responseBody;
    }

    /// <summary>发送 POST 请求（Form）并反序列化为 JSON。</summary>
    public async Task<T?> PostFormAsJsonAsync<T>(
        string url, Dictionary<string, string> form, CancellationToken ct = default)
    {
        var json = await PostFormAsync(url, form, ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    /// <summary>发送 POST JSON 并反序列化。</summary>
    public async Task<T?> PostJsonAsync<T>(
        string url, object? body, CancellationToken ct = default)
    {
        var json = await PostJsonAsync(url, body, ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    /// <summary>发送 POST 请求（已序列化的 JSON 字符串，不走 SnakeCaseLower）。</summary>
    public async Task<string> PostJsonRawAsync(
        string url, string json, CancellationToken ct = default)
    {
        _logger?.LogDebug("POST (raw) {Url}", url);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(url, content, ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var snippet = responseBody.Length > 1000 ? responseBody[..1000] : responseBody;
            _logger?.LogError("POST (raw) {Url} → HTTP {Code}: {Body}",
                url, (int)resp.StatusCode, snippet);
            throw new HttpRequestException(
                $"Response status code {(int)resp.StatusCode}: {snippet}");
        }
        return responseBody;
    }
    public async Task<string> PutBinaryAsync(
        string url,
        byte[] data,
        Dictionary<string, string>? extraHeaders = null,
        CancellationToken ct = default)
    {
        _logger?.LogDebug("PUT {Url} ({Bytes} bytes)", url, data.Length);
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        if (extraHeaders != null)
        {
            foreach (var (k, v) in extraHeaders)
                content.Headers.TryAddWithoutValidation(k, v);
        }

        var resp = await _http.PutAsync(url, content, ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var snippet = responseBody.Length > 1000 ? responseBody[..1000] : responseBody;
            _logger?.LogError("PUT {Url} → HTTP {Code}: {Body}",
                url, (int)resp.StatusCode, snippet);
            throw new HttpRequestException(
                $"Response status code {(int)resp.StatusCode}: {snippet}");
        }
        return responseBody;
    }

    // ═══ Utility ═══

    /// <summary>构建带浏览器指纹 + 签名的完整 URL query string。</summary>
    public static string BuildQueryString(
        Dictionary<string, string>? extra = null,
        string? msToken = null,
        string? aBogus = null,
        PlatformEnvironmentSnapshot? env = null)
    {
        var qs = BrowserFingerprint.FromSnapshot(env);
        if (!string.IsNullOrWhiteSpace(msToken))
            qs["msToken"] = msToken;
        if (!string.IsNullOrWhiteSpace(aBogus))
            qs["a_bogus"] = aBogus;

        if (extra is not null)
        {
            foreach (var (k, v) in extra)
                qs[k] = v;
        }

        return string.Join("&", qs.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Dispose() => _http.Dispose();
}
