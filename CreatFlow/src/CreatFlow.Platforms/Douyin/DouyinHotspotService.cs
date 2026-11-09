namespace CreatFlow.Platforms.Douyin;

/// <summary>
/// 抖音关联热点搜索服务。
///
/// 对标 Python examples/douyin/search/search.py + search.json。
///
/// API 端点:
///   GET /aweme/v1/hotspot/search/?query=xxx&amp;count=50
///   (Cookie + msToken/a_bogus 查询参数)
///
/// 用途:
///   发布页「关联热点」搜索热点词，选中后:
///     hashtag_source = "search/search/search"
///     hot_sentence   = 选中热点的 word
///   未选时:
///     hashtag_source = "recommend/recommend/recommend"（默认）
/// </summary>
public sealed class DouyinHotspotService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<DouyinHotspotService>? _logger;

    public DouyinHotspotService(
        PlatformOptions options,
        ILogger<DouyinHotspotService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 获取推荐/热门热点（无需关键词）。
    ///
    /// 对标抖音 creator 页面：聚焦热点输入框时，不输入任何关键词，
    /// 自动弹出推荐热点下拉。调用 /aweme/v1/hotspot/search/ 但不传 query。
    /// </summary>
    /// <param name="cookie">平台 Cookie</param>
    /// <param name="count">返回条数（默认 20）</param>
    /// <param name="msToken">安全 SDK token（URL 查询参数）</param>
    /// <param name="aBogus">安全 SDK 签名（URL 查询参数）</param>
    /// <param name="ct">取消令牌</param>
    public async Task<HotspotSearchResult> GetTrendingAsync(
        string cookie,
        int count = 20,
        string? msToken = null,
        string? aBogus = null,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie);

        // 只拼 count，不拼 query — 抖音会返回推荐热点
        var qs = PlatformHttpClient.BuildQueryString(
            new Dictionary<string, string>
            {
                ["count"] = count.ToString(),
            },
            msToken: msToken,
            aBogus: aBogus);
        var url = $"{_options.DouyinCreatorHost}/aweme/v1/hotspot/search/?{qs}";

        _logger?.LogDebug("[DouyinHotspot] GetTrending → count={Count}", count);

        var response = await client.GetJsonAsync<HotspotSearchResponse>(url, ct);
        if (response == null || response.StatusCode != 0)
        {
            _logger?.LogWarning("[DouyinHotspot] GetTrending → status_code={Code}",
                response?.StatusCode);
            return new HotspotSearchResult { Items = [] };
        }

        var items = response.Sentences?.Select(s => new HotspotItem(
            s.Word ?? "",
            s.HotValue,
            s.VideoCount
        )).ToList() ?? [];

        _logger?.LogDebug("[DouyinHotspot] GetTrending → {Count} results", items.Count);
        return new HotspotSearchResult { Items = items };
    }

    /// <summary>
    /// 按关键词搜索热点。
    /// </summary>
    /// <param name="cookie">平台 Cookie</param>
    /// <param name="query">搜索关键词</param>
    /// <param name="count">返回条数（默认 50）</param>
    /// <param name="msToken">安全 SDK token（URL 查询参数）</param>
    /// <param name="aBogus">安全 SDK 签名（URL 查询参数）</param>
    /// <param name="ct">取消令牌</param>
    public async Task<HotspotSearchResult> SearchAsync(
        string cookie,
        string query,
        int count = 50,
        string? msToken = null,
        string? aBogus = null,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie);

        var qs = PlatformHttpClient.BuildQueryString(
            new Dictionary<string, string>
            {
                ["query"] = query,
                ["count"] = count.ToString(),
            },
            msToken: msToken,
            aBogus: aBogus);
        var url = $"{_options.DouyinCreatorHost}/aweme/v1/hotspot/search/?{qs}";

        _logger?.LogDebug("[DouyinHotspot] Search → query={Query}", query);

        var response = await client.GetJsonAsync<HotspotSearchResponse>(url, ct);
        if (response == null || response.StatusCode != 0)
        {
            _logger?.LogWarning("[DouyinHotspot] Search → status_code={Code}", response?.StatusCode);
            return new HotspotSearchResult { Items = [] };
        }

        var items = response.Sentences?.Select(s => new HotspotItem(
            s.Word ?? "",
            s.HotValue,
            s.VideoCount
        )).ToList() ?? [];

        _logger?.LogDebug("[DouyinHotspot] Search → {Count} results", items.Count);

        return new HotspotSearchResult { Items = items };
    }

    // ── HTTP client builder ──

    private PlatformHttpClient BuildClient(string cookie)
    {
        var cookieHandler = new Core.Handlers.CookieAuthHandler(cookie);
        var loggingHandler = new Core.Handlers.LoggingHandler(null);
        cookieHandler.InnerHandler = loggingHandler;

        return new PlatformHttpClient(cookieHandler, _options, null);
    }
}

// ═══ DTO — 抖音热点搜索 API 响应结构 ═══

file sealed class HotspotSearchResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; init; }

    [JsonPropertyName("sentences")]
    public List<HotspotSentence>? Sentences { get; init; }
}

file sealed class HotspotSentence
{
    /// <summary>热点词文本</summary>
    [JsonPropertyName("word")]
    public string? Word { get; init; }

    /// <summary>热度值（越大越热）</summary>
    [JsonPropertyName("hot_value")]
    public long HotValue { get; init; }

    /// <summary>关联视频数</summary>
    [JsonPropertyName("video_count")]
    public long VideoCount { get; init; }

    /// <summary>热点 ID</summary>
    [JsonPropertyName("sentence_id")]
    public string? SentenceId { get; init; }
}

// ═══ 公共返回类型 ═══

/// <summary>
/// 热点搜索结果。
/// </summary>
public sealed class HotspotSearchResult
{
    /// <summary>匹配的热点词列表，按热度降序</summary>
    public List<HotspotItem> Items { get; init; } = [];
}

/// <summary>
/// 热点词摘要 — 用于发布页「关联热点」选择。
/// </summary>
/// <param name="Word">热点词文本 — 填入 item.common.hot_sentence</param>
/// <param name="HotValue">热度值（UI 展示用，如"444 万"）</param>
/// <param name="VideoCount">关联视频数</param>
public sealed record HotspotItem(
    string Word,
    long HotValue,
    long VideoCount
)
{
    /// <summary>热度显示文本，如 "444 万" / "4.4 万"</summary>
    public string HotValueDisplay => HotValue switch
    {
        >= 10_000_000 => $"{HotValue / 10_000_000.0:F1} 千万",
        >= 10_000 => $"{HotValue / 10_000.0:F1} 万",
        _ => HotValue.ToString(),
    };
}
