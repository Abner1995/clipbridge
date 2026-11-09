namespace CreatFlow.Platforms.Douyin;

/// <summary>
/// 抖音媒体服务 — 已发布视频列表查询（historyMedia API）。
///
/// 对标 Python examples/douyin/media/historyMedia.py。
///
/// API 端点:
///   GET /aweme/v1/view/history/list/?max_cursor=0&amp;min_cursor=0&amp;count=20
///   (Cookie + msToken/a_bogus 查询参数)
///
/// 用途:
///   发布页「自主声明 → 转载信息 → 抖音作品」选择源作品时，列出已发布视频供用户搜索选择。
/// </summary>
public sealed class DouyinMediaService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<DouyinMediaService>? _logger;

    public DouyinMediaService(
        PlatformOptions options,
        ILogger<DouyinMediaService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 查询已发布视频列表（分页）。
    /// </summary>
    /// <param name="cookie">平台 Cookie</param>
    /// <param name="maxCursor">分页游标（首次传 "0"）</param>
    /// <param name="count">每页条数（默认 20）</param>
    /// <param name="msToken">安全 SDK token（URL 查询参数）</param>
    /// <param name="aBogus">安全 SDK 签名（URL 查询参数）</param>
    /// <param name="ct">取消令牌</param>
    public async Task<HistoryMediaResult> ListHistoryAsync(
        string cookie,
        string maxCursor = "0",
        int count = 20,
        string? msToken = null,
        string? aBogus = null,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie);

        var qs = PlatformHttpClient.BuildQueryString(
            new Dictionary<string, string>
            {
                ["max_cursor"] = maxCursor,
                ["min_cursor"] = "0",
                ["count"] = count.ToString(),
            },
            msToken: msToken,
            aBogus: aBogus);
        var url = $"{_options.DouyinCreatorHost}/aweme/v1/view/history/list/?{qs}";

        _logger?.LogDebug("[DouyinMedia] ListHistory → {Url}", url);

        var response = await client.GetJsonAsync<HistoryMediaResponse>(url, ct);
        if (response == null || response.StatusCode != 0)
        {
            _logger?.LogWarning("[DouyinMedia] ListHistory → status_code={Code}", response?.StatusCode);
            return new HistoryMediaResult
            {
                HasMore = false,
                Items = [],
                StatusCode = response?.StatusCode ?? -1,
            };
        }

        var items = response.AwemeList?.Select(i => new HistoryMediaItem(
            i.GroupId ?? "",
            i.Desc ?? "无标题",
            i.CoverUrl,
            i.DurationSeconds ?? 0,
            i.CreateTime ?? 0
        )).ToList() ?? [];

        _logger?.LogDebug("[DouyinMedia] ListHistory → {Count} items, hasMore={HasMore}",
            items.Count, response.HasMore > 0);

        return new HistoryMediaResult
        {
            HasMore = response.HasMore > 0,
            MaxCursor = response.MaxCursor?.ToString() ?? "0",
            Items = items,
        };
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

// ═══ DTO — 抖音 API 响应结构 ═══

file sealed class HistoryMediaResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; init; }

    [JsonPropertyName("has_more")]
    public int HasMore { get; init; }

    [JsonPropertyName("max_cursor")]
    public long? MaxCursor { get; init; }

    [JsonPropertyName("aweme_list")]
    public List<HistoryMediaAweme>? AwemeList { get; init; }
}

file sealed class HistoryMediaAweme
{
    [JsonPropertyName("group_id")]
    public string? GroupId { get; init; }

    /// <summary>视频标题 — API 字段 desc</summary>
    [JsonPropertyName("desc")]
    public string? Desc { get; init; }

    [JsonPropertyName("create_time")]
    public long? CreateTime { get; init; }

    /// <summary>视频时长（秒）— API 字段 video.duration 恒为毫秒，除以 1000</summary>
    public int? DurationSeconds
    {
        get
        {
            var dur = VideoInfo?.Duration;
            if (dur is null) return null;
            // video.duration 恒为毫秒（如 22667ms = 22.6s, 108534ms = 1m48s）
            return dur.Value / 1000;
        }
    }

    /// <summary>封面图 URL（video.cover.url_list[0]）</summary>
    public string? CoverUrl
    {
        get
        {
            var urls = VideoInfo?.Cover?.UrlList;
            return urls is { Count: > 0 } ? urls[0] : null;
        }
    }

    [JsonPropertyName("video")]
    public HistoryMediaVideo? VideoInfo { get; init; }
}

file sealed class HistoryMediaVideo
{
    [JsonPropertyName("duration")]
    public int? Duration { get; init; }

    [JsonPropertyName("cover")]
    public HistoryMediaCover? Cover { get; init; }
}

file sealed class HistoryMediaCover
{
    [JsonPropertyName("url_list")]
    public List<string>? UrlList { get; init; }
}

// ═══ 公共返回类型 ═══

/// <summary>
/// 已发布视频列表查询结果。
/// </summary>
public sealed class HistoryMediaResult
{
    /// <summary>
    /// 0 = 成功；非 0 = 平台返回码（多半是登录凭证过期）；-1 = 响应缺失。
    /// 老版把非 0 也当「空列表」返回，调用方分不清「没有作品」和「凭证过期」——新版补上。
    /// </summary>
    public int StatusCode { get; init; }

    /// <summary>是否有更多数据</summary>
    public bool HasMore { get; init; }

    /// <summary>下一页游标</summary>
    public string MaxCursor { get; init; } = "0";

    /// <summary>视频列表</summary>
    public List<HistoryMediaItem> Items { get; init; } = [];
}

/// <summary>
/// 已发布视频摘要 — 用于自主声明转载源搜索。
/// </summary>
/// <param name="ItemId">视频 ID（group_id = from_item_id）</param>
/// <param name="Title">预览标题</param>
/// <param name="CoverUrl">封面图 URL</param>
/// <param name="DurationS">时长（秒）</param>
/// <param name="CreateTime">发布时间（Unix 秒级时间戳）</param>
public sealed record HistoryMediaItem(
    string ItemId,
    string Title,
    string? CoverUrl,
    int DurationS,
    long CreateTime
);
