namespace CreatFlow.Platforms.Douyin;

/// <summary>
/// 抖音 POI（位置）搜索服务。
///
/// 对标 Python examples/douyin/poi/poi.py + poi.json。
///
/// API 端点:
///   GET /aweme/v1/life/video_api/search/poi/
///   (Cookie + msToken/a_bogus 查询参数 + 经纬度 + 城市代码)
///
/// 用途:
///   发布页「位置」搜索抖音 POI，获取 poi_id 用于 item.anchor。
/// </summary>
public sealed class DouyinPoiService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<DouyinPoiService>? _logger;

    public DouyinPoiService(
        PlatformOptions options,
        ILogger<DouyinPoiService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 按关键词搜索 POI 位置。
    /// </summary>
    /// <param name="cookie">平台 Cookie</param>
    /// <param name="keyword">搜索关键词</param>
    /// <param name="count">返回条数（默认 12）</param>
    /// <param name="page">页码（默认 1）</param>
    /// <param name="latitude">GPS 纬度（v1 默认 0，后续可接高德定位）</param>
    /// <param name="longitude">GPS 经度</param>
    /// <param name="msToken">安全 SDK token（URL 查询参数）</param>
    /// <param name="aBogus">安全 SDK 签名（URL 查询参数）</param>
    /// <param name="ct">取消令牌</param>
    public async Task<PoiSearchResult> SearchAsync(
        string cookie,
        string keyword,
        int count = 12,
        int page = 1,
        double latitude = 0,
        double longitude = 0,
        string? msToken = null,
        string? aBogus = null,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie);

        // 对标 poi.py 的完整参数列表
        var qs = PlatformHttpClient.BuildQueryString(
            new Dictionary<string, string>
            {
                ["keywords"] = keyword,
                ["count"] = count.ToString(),
                ["page"] = page.ToString(),
                ["search_type"] = "0",
                ["poi_anchor_tab"] = "2",
                ["from_webapp"] = "1",
                ["get_current_loc"] = "1",
                ["poi_mode"] = "2",
                ["latitude"] = latitude.ToString("F6"),
                ["longitude"] = longitude.ToString("F6"),
            },
            msToken: msToken,
            aBogus: aBogus);
        var url = $"{_options.DouyinCreatorHost}/aweme/v1/life/video_api/search/poi/?{qs}";

        _logger?.LogDebug("[DouyinPoi] Search → keywords={Keyword}, page={Page}", keyword, page);

        var rawBody = await client.GetStringAsync(url, ct);

        if (string.IsNullOrWhiteSpace(rawBody))
        {
            _logger?.LogWarning("[DouyinPoi] Search → empty response body");
            return new PoiSearchResult { Items = [] };
        }

        var response = JsonSerializer.Deserialize<PoiSearchResponse>(rawBody, JsonOptions);
        if (response == null || response.StatusCode != 0)
        {
            _logger?.LogWarning("[DouyinPoi] Search → status_code={Code}", response?.StatusCode);
            return new PoiSearchResult { Items = [] };
        }

        var items = response.PoiList?.Select(p => new PoiItem(
            p.PoiId ?? "",
            p.PoiName ?? "",
            p.AddressInfo?.SimpleAddr ?? p.AddressInfo?.Address ?? ""
        )).ToList() ?? [];

        _logger?.LogDebug("[DouyinPoi] Search → {Count} results", items.Count);

        return new PoiSearchResult { Items = items };
    }

    // ── HTTP client builder ──

    private PlatformHttpClient BuildClient(string cookie)
    {
        var cookieHandler = new Core.Handlers.CookieAuthHandler(cookie);
        var loggingHandler = new Core.Handlers.LoggingHandler(null);
        cookieHandler.InnerHandler = loggingHandler;

        return new PlatformHttpClient(cookieHandler, _options, null);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

// ═══ DTO — 抖音 POI 搜索 API 响应结构 ═══

file sealed class PoiSearchResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; init; }

    [JsonPropertyName("poi_list")]
    public List<PoiSearchEntry>? PoiList { get; init; }
}

file sealed class PoiSearchEntry
{
    [JsonPropertyName("poi_id")]
    public string? PoiId { get; init; }

    [JsonPropertyName("poi_name")]
    public string? PoiName { get; init; }

    [JsonPropertyName("address_info")]
    public PoiAddressInfo? AddressInfo { get; init; }
}

file sealed class PoiAddressInfo
{
    /// <summary>完整地址，如"盛泽镇南三环路1979号"</summary>
    [JsonPropertyName("address")]
    public string? Address { get; init; }

    /// <summary>简化地址，如"江苏省苏州市吴江区南三环路1979号"</summary>
    [JsonPropertyName("simple_addr")]
    public string? SimpleAddr { get; init; }

    [JsonPropertyName("city")]
    public string? City { get; init; }

    [JsonPropertyName("province")]
    public string? Province { get; init; }

    [JsonPropertyName("district")]
    public string? District { get; init; }
}

// ═══ 公共返回类型 ═══

/// <summary>
/// POI 搜索结果。
/// </summary>
public sealed class PoiSearchResult
{
    /// <summary>匹配的 POI 列表</summary>
    public List<PoiItem> Items { get; init; } = [];
}

/// <summary>
/// POI 位置摘要 — 用于发布页「位置」选择。
/// </summary>
/// <param name="PoiId">抖音 POI ID — 填入 item.anchor.poi_id</param>
/// <param name="PoiName">POI 名称 — 填入 item.anchor.poi_name</param>
/// <param name="Address">地址展示（simple_addr，UI 列表+回填用）</param>
public sealed record PoiItem(
    string PoiId,
    string PoiName,
    string Address
);
