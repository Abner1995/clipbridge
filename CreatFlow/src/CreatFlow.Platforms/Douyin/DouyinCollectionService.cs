namespace CreatFlow.Platforms.Douyin;

/// <summary>
/// 抖音合集服务 — 封装 creator.douyin.com 合集 CRUD API。
///
/// 实现 ICollectionList / ICollectionCreate / ICollectionUpdate / ICollectionDelete，
/// 对标 Python examples/douyin/collection/collection.py + createcollection.py。
///
/// API 端点：
///   List:   GET  /web/api/mix/list/     (Cookie)
///   Create: POST /web/api/mix/create    (Cookie + csrf_token + msToken/a_bogus)
///   Update: POST /web/api/mix/update    (Cookie + csrf_token + msToken/a_bogus)
///   Delete: POST /web/api/mix/destroy   (Cookie + csrf_token + msToken/a_bogus)
///
/// </summary>
public sealed class DouyinCollectionService :
    ICollectionList, ICollectionCreate, ICollectionUpdate, ICollectionDelete
{
    private readonly PlatformOptions _options;
    private readonly ILogger<DouyinCollectionService>? _logger;

    public DouyinCollectionService(
        PlatformOptions options,
        ILogger<DouyinCollectionService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    // ═══ ICollectionList — 获取合集列表 ═══

    /// <inheritdoc />
    public async Task<CollectionListResult> ListAsync(
        string cookie,
        long cursor = 0,
        int count = 12,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie);

        var path = "/web/api/mix/list/";
        var queryParams = new Dictionary<string, string>
        {
            ["status"] = "0,1,2,3,6",
            ["count"] = count.ToString(),
            ["cursor"] = cursor.ToString(),
        };
        var qs = PlatformHttpClient.BuildQueryString(queryParams);
        var url = $"{_options.DouyinCreatorHost}{path}?{qs}";

        var resp = await client.GetJsonAsync<DouyinListResponse>(url, ct);
        if (resp == null || resp.StatusCode != 0)
        {
            _logger?.LogWarning("Douyin list collection failed: status_code={Code}", resp?.StatusCode);
            return new CollectionListResult([], false, 0);
        }

        var items = resp.MixList?.Select(ToCollectionInfo).ToList() ?? [];

        return new CollectionListResult(
            items,
            resp.HasMore,
            resp.Cursor);
    }

    // ═══ ICollectionCreate — 创建合集 ═══

    /// <inheritdoc />
    public async Task<CreateCollectionResult> CreateAsync(
        string cookie,
        CreateCollectionInfo request,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie, request.CsrfToken);

        var path = "/web/api/mix/create";
        var qs = PlatformHttpClient.BuildQueryString(
            msToken: request.MsToken,
            aBogus: request.ABogus);
        var url = $"{_options.DouyinCreatorHost}{path}?{qs}";

        var form = new Dictionary<string, string>
        {
            ["mix_id"] = "",                                   // 空 = 新建
            ["mix_name"] = request.Title,
            ["mix_desc"] = request.Description ?? "",
            ["mix_cover"] = request.CoverTosKey ?? "",         // TOS 路径
            ["item_ids"] = request.ItemIds is { Count: > 0 }
                ? string.Join(",", request.ItemIds) : "",
        };

        var resp = await client.PostFormAsJsonAsync<DouyinCreateResponse>(url, form, ct);

        // Console.WriteLine($"[CreateAsync] status_code={resp?.StatusCode}, status_msg={resp?.StatusMsg}, mix_id={resp?.MixId}");
        // Console.WriteLine($"[CreateAsync] form: mix_name={form["mix_name"]}, mix_cover={form["mix_cover"]}, mix_desc={form["mix_desc"]}");

        if (resp == null || resp.StatusCode != 0)
        {
            var err = resp?.StatusMsg ?? $"status_code={resp?.StatusCode}";
            _logger?.LogWarning("Douyin create collection failed: {Error}", err);
            return new CreateCollectionResult("", false, err);
        }

        return new CreateCollectionResult(
            resp.MixId ?? "",
            true,
            null);
    }

    // ═══ ICollectionUpdate — 编辑合集 ═══

    /// <inheritdoc />
    public async Task<UpdateCollectionResult> UpdateAsync(
        string cookie,
        UpdateCollectionInfo request,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie, request.CsrfToken);

        var path = "/web/api/mix/update";
        var qs = PlatformHttpClient.BuildQueryString(
            msToken: request.MsToken,
            aBogus: request.ABogus);
        var url = $"{_options.DouyinCreatorHost}{path}?{qs}";

        var form = new Dictionary<string, string>
        {
            ["mix_id"] = request.PlatformCollectionId,
            ["mix_name"] = request.Title,
            ["mix_desc"] = request.Description ?? "",
            ["mix_cover"] = request.CoverTosKey ?? "",
        };

        var resp = await client.PostFormAsJsonAsync<DouyinCreateResponse>(url, form, ct);

        if (resp == null || resp.StatusCode != 0)
        {
            var err = resp?.StatusMsg ?? $"status_code={resp?.StatusCode}";
            _logger?.LogWarning("Douyin update collection failed: {Error}", err);
            return new UpdateCollectionResult(false, err);
        }

        return new UpdateCollectionResult(true, null);
    }

    // ═══ ICollectionDelete — 删除合集 ═══

    /// <inheritdoc />
    public async Task<DeleteCollectionResult> DeleteAsync(
        string cookie,
        DeleteCollectionInfo request,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie, request.CsrfToken);

        var path = "/web/api/mix/destroy";
        var qs = PlatformHttpClient.BuildQueryString(
            msToken: request.MsToken,
            aBogus: request.ABogus);
        var url = $"{_options.DouyinCreatorHost}{path}?{qs}";

        var form = new Dictionary<string, string>
        {
            ["mix_id"] = request.PlatformCollectionId,
        };

        var resp = await client.PostFormAsJsonAsync<DouyinDestroyResponse>(url, form, ct);

        if (resp == null || resp.StatusCode != 0)
        {
            var err = resp?.StatusMsg ?? $"status_code={resp?.StatusCode}";
            _logger?.LogWarning("Douyin delete collection failed: {Error}", err);
            return new DeleteCollectionResult(false, err);
        }

        return new DeleteCollectionResult(true, null);
    }

    // ═══ GetDetail — 获取合集详情（含 CDN 封面图 URL） ═══

    /// <summary>
    /// 获取抖音合集详情。
    ///
    /// 对标: GET /aweme/v1/mix/detail/?mix_id=xxx&msToken=xxx&a_bogus=xxx
    /// 参考: examples/douyin/collection/collectiondetail.py
    ///
    /// 用途：创建合集后，用此接口获取 cover_url.url_list[0]（可访问的 CDN URL），
    /// 而不是 tos-cn-i-dy/xxx 这种 TOS object key。
    /// </summary>
    public async Task<DouyinDetailResult?> DetailAsync(
        string cookie,
        string mixId,
        string? csrfToken,
        string? msToken,
        string? aBogus,
        CancellationToken ct = default)
    {
        // /aweme/v1/ 端点需要额外参数，BrowserFingerprint.Default 不包含
        var extraParams = new Dictionary<string, string>
        {
            ["mix_id"] = mixId,
            ["req_from"] = "pc",
            ["version_code"] = "28.0.0",
            ["app_version"] = "28.0.0",
        };
        var path = "/aweme/v1/mix/detail/";
        var qs = PlatformHttpClient.BuildQueryString(extraParams, msToken, aBogus);
        var url = $"{_options.DouyinCreatorHost}{path}?{qs}";

        // 用独立 HttpClient（不走 BuildClient），手动设置 Referer 含 mixId
        using var handler = new HttpClientHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add("User-Agent",
            _options.Environment?.UserAgent ?? _options.UserAgent);
        http.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        http.DefaultRequestHeaders.Add("Accept-Language", _options.AcceptLanguage);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer",
            $"https://creator.douyin.com/creator-micro/content/collection/detail/{mixId}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (!string.IsNullOrWhiteSpace(csrfToken))
            request.Headers.TryAddWithoutValidation("x-secsdk-csrf-token", csrfToken);

        LogToFile($"[DouyinDetail] GET {url}");
        var response = await http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        LogToFile($"[DouyinDetail] HTTP {(int)response.StatusCode}, body length={json?.Length ?? 0}");
        if (!response.IsSuccessStatusCode)
        {
            LogToFile($"[DouyinDetail] FAIL: {json?[..Math.Min(300, json.Length)]}");
            _logger?.LogWarning("Douyin detail failed: HTTP {Code}", (int)response.StatusCode);
            return null;
        }

        var resp = JsonSerializer.Deserialize<DouyinDetailResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (resp == null || resp.StatusCode != 0 || resp.MixInfo == null)
        {
            LogToFile($"[DouyinDetail] FAIL: status_code={resp?.StatusCode}, mix_info null={resp?.MixInfo == null}, body={json?[..Math.Min(500, json.Length)]}");
            return null;
        }

        var mix = resp.MixInfo;

        // 提取 CDN URL
        string? coverCdnUrl = null;
        string? coverUri = null;
        if (mix.CoverUrl?.ValueKind == JsonValueKind.String)
        {
            coverUri = mix.CoverUrl.Value.GetString();
            coverCdnUrl = coverUri;
        }
        else if (mix.CoverUrl?.ValueKind == JsonValueKind.Object)
        {
            var coverObj = mix.CoverUrl.Value;
            if (coverObj.TryGetProperty("uri", out var uri))
                coverUri = uri.GetString();
            if (coverObj.TryGetProperty("url_list", out var urlList) &&
                urlList.ValueKind == JsonValueKind.Array && urlList.GetArrayLength() > 0)
                coverCdnUrl = urlList[0].GetString();
        }

        LogToFile($"[DouyinDetail] OK: coverCdnUrl={coverCdnUrl ?? "NULL"}, mixName={mix.MixName}");

        return new DouyinDetailResult(
            MixId: mix.MixId,
            MixName: mix.MixName,
            Description: mix.Desc,
            CoverUri: coverUri,
            CoverCdnUrl: coverCdnUrl,
            Status: mix.StatusValue,
            StatusText: StatusToText(mix.StatusValue),
            VideoCount: mix.VideoCount
        );
    }

    /// <summary>调试日志。**2026-10-14 从「桌面 creatflow_debug.log 无界追加」收口到 Serilog**：
    /// 老写法把文件写到用户桌面上且永不清理（两个 App 都在写，一次合集操作三行、每行带
    /// a_bogus 的长 URL），日志清理功能拿它没办法（活跃写入时 mtime 永远是新的）。
    /// 收口后同一批行进 Serilog 文件日志（logging 层已有按天/按大小滚动 + 保留策略），
    /// 存量桌面文件由客户端 LogCleanup 按龄扫掉。</summary>
    private static void LogToFile(string msg) => Serilog.Log.Debug("{Message}", msg);

    // ═══ Private Helpers ═══

    /// <summary>构建基础 HTTP 客户端（Cookie 认证 + 可选 CSRF token）。</summary>
    private PlatformHttpClient BuildClient(string cookie, string? csrfToken = null)
    {
        DelegatingHandler head;
        var cookieHandler = new Core.Handlers.CookieAuthHandler(cookie);
        var loggingHandler = new Core.Handlers.LoggingHandler(null);

        if (!string.IsNullOrWhiteSpace(csrfToken))
        {
            // Handler 管道: CsrfToken → Cookie → Logging → HTTP
            var csrfHandler = new Core.Handlers.CsrfTokenHandler(csrfToken);
            csrfHandler.InnerHandler = cookieHandler;
            cookieHandler.InnerHandler = loggingHandler;
            head = csrfHandler;
        }
        else
        {
            cookieHandler.InnerHandler = loggingHandler;
            head = cookieHandler;
        }

        return new PlatformHttpClient(head, _options, null);
    }

    /// <summary>抖音 MixInfo → 通用 CollectionInfo。</summary>
    private static CollectionInfo ToCollectionInfo(DouyinMixInfo m)
    {
        // cover_url 可能是 JSON object（包含 url_list）或 string
        string? coverUrl = null;
        if (m.CoverUrl?.ValueKind == JsonValueKind.String)
        {
            coverUrl = m.CoverUrl.Value.GetString();
        }
        else if (m.CoverUrl?.ValueKind == JsonValueKind.Object)
        {
            var coverObj = m.CoverUrl.Value;
            if (coverObj.TryGetProperty("url_list", out var urlList) &&
                urlList.ValueKind == JsonValueKind.Array &&
                urlList.GetArrayLength() > 0)
            {
                coverUrl = urlList[0].GetString();
            }
        }

        return new CollectionInfo(
            PlatformCollectionId: m.MixId,
            Title: m.MixName,
            Description: m.Desc,
            CoverUrl: coverUrl,
            Status: m.StatusValue,
            StatusText: StatusToText(m.StatusValue),
            VideoCount: m.VideoCount,
            CreatedAt: UnixSecondsToDateTime(m.CreateTime),
            UpdatedAt: null);       // 抖音列表 API 不返回更新时间
    }

    private static string StatusToText(int status) => status switch
    {
        0 => "审核中",
        1 => "已发布",
        2 => "草稿",
        3 => "审核未通过",
        6 => "已删除",
        _ => $"未知({status})"
    };

    private static DateTime? UnixSecondsToDateTime(long seconds)
    {
        if (seconds <= 0) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch
        {
            return null;
        }
    }
}
