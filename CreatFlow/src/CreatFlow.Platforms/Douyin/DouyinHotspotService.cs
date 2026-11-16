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

    /// <summary>
    /// **推荐热点 · 本地签名版**（2026-11-10）：<c>/aweme/v1/hotspot/recommend/</c>。
    /// 这是 creator 发布页真正用来出「热门热点」列表的端点（抓包
    /// <c>capture-2026-11-05T01-40-33.jsonl</c>）：响应字段是 <c>all_sentences</c>
    /// （54 条样本），**不是** <c>sentences</c>。
    /// <para>
    /// 注意：`hotspot/search` 不传关键词会回 <c>status_code=0</c> 但 `sentences` 为空
    /// （2026-11-10 真账号实测）—— 「热门列表」必须走这个端点。
    /// </para>
    /// </summary>
    public async Task<HotspotSearchResult> QueryTrendingSignedAsync(
        string cookie,
        int count = 30,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie);

        // 抓包原文参数：zip_uri 空串 + aweme_name=aweme；aid=1128 由指纹默认值带上
        var extra = new Dictionary<string, string>
        {
            ["zip_uri"] = "",
            ["aweme_name"] = "aweme",
        };

        var env = _options.Environment;
        var userAgent = env?.UserAgent ?? _options.UserAgent;
        var baseParams = PlatformHttpClient.BuildQueryString(extra, env: env);
        var msToken = MsTokenGenerator.Generate();
        var aBogus = ABogusGenerator.Generate(baseParams, userAgent, aid: 1128);
        var qs = PlatformHttpClient.BuildQueryString(extra, msToken, aBogus, env);

        var url = $"{_options.DouyinCreatorHost}/aweme/v1/hotspot/recommend/?{qs}";

        var response = await client.GetJsonAsync<HotspotRecommendResponse>(url, ct);
        if (response == null || response.StatusCode != 0)
        {
            Serilog.Log.Warning(
                "[DouyinHotspot] hotspot/recommend 非 0：status={Status}", response?.StatusCode);
            return new HotspotSearchResult { Items = [] };
        }

        // 抓包：all_sentences = 全量热门；rec_sentences = 推荐子集。优先 all_sentences。
        var sentences = response.AllSentences ?? response.RecSentences ?? [];
        var items = sentences
            .Where(s => !string.IsNullOrWhiteSpace(s.Word))
            .Take(count)
            .Select(s => new HotspotItem(s.Word!, s.HotValue, s.VideoCount))
            .ToList();

        Serilog.Log.Information("[DouyinHotspot] hotspot/recommend 返回 {Count} 条", items.Count);
        return new HotspotSearchResult { Items = items };
    }

    /// <summary>
    /// **关键词搜索 · 本地签名版**（新 App 用，2026-11-10）：msToken 本地生成、
    /// a_bogus 本地计算（aid=1128，与 creator 发布页 / Topics 的 DouyinTopicApi 同一条签名路），
    /// 不需要页面抓来的 token。**必须传 <paramref name="query"/>** —— 空 query 平台回空列表。
    ///
    /// 老 App 的 <see cref="SearchAsync"/> / <see cref="GetTrendingAsync"/> 保持原样
    /// （生产在用，签名参数由页面快照提供）。
    /// </summary>
    public async Task<HotspotSearchResult> QuerySignedAsync(
        string cookie,
        string? query,
        int count = 30,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie);

        var extra = new Dictionary<string, string> { ["count"] = count.ToString() };
        if (!string.IsNullOrWhiteSpace(query)) extra["query"] = query;

        // 指纹参数（aid=1128 / support_h265=1 等）由 BuildQueryString 统一带上；
        // 签名用的 base 串与最终 URL 必须是同一份参数（msToken/a_bogus 自身不进 base）。
        var env = _options.Environment;
        var userAgent = env?.UserAgent ?? _options.UserAgent;
        var baseParams = PlatformHttpClient.BuildQueryString(extra, env: env);
        var msToken = MsTokenGenerator.Generate();
        var aBogus = ABogusGenerator.Generate(baseParams, userAgent, aid: 1128);
        var qs = PlatformHttpClient.BuildQueryString(extra, msToken, aBogus, env);

        var url = $"{_options.DouyinCreatorHost}/aweme/v1/hotspot/search/?{qs}";

        _logger?.LogDebug("[DouyinHotspot] QuerySigned → query={Query} count={Count}", query, count);

        var response = await client.GetJsonAsync<HotspotSearchResponse>(url, ct);
        if (response == null || response.StatusCode != 0)
        {
            // 桌面端构造服务时不传 ILogger（_logger 为 null），走 Serilog 兜底 ——
            // 返回空时用户能在客户端日志里看到真实原因，不用瞎猜。
            Serilog.Log.Warning(
                "[DouyinHotspot] hotspot/search 非 0：status={Status} query={Query}",
                response?.StatusCode, query ?? "(推荐)");
            return new HotspotSearchResult { Items = [] };
        }

        // 局部函数里可以用 file 本地 DTO（类成员签名里不行，CS9051）
        static HotspotSearchResult Map(HotspotSearchResponse response) => new()
        {
            Items = response.Sentences?.Select(s => new HotspotItem(
                s.Word ?? "",
                s.HotValue,
                s.VideoCount
            )).ToList() ?? [],
        };

        var result = Map(response);
        Serilog.Log.Information(
            "[DouyinHotspot] hotspot/search 返回 {Count} 条（query={Query}）",
            result.Items.Count, query ?? "(推荐)");
        return result;
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

/// <summary>
/// `hotspot/recommend` 的响应（抓包原文：
/// <c>{"all_sentences":[…54 条…],"rec_sentences":[…],"status_code":0}</c>）。
/// </summary>
file sealed class HotspotRecommendResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; init; }

    /// <summary>全量热门（creator 页面「热门热点」列表用它）</summary>
    [JsonPropertyName("all_sentences")]
    public List<HotspotSentence>? AllSentences { get; init; }

    /// <summary>推荐子集（可能为空；all_sentences 缺失时兜底）</summary>
    [JsonPropertyName("rec_sentences")]
    public List<HotspotSentence>? RecSentences { get; init; }
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
