namespace CreatFlow.Platforms.Douyin;

/// <summary>
/// 抖音「配乐」服务（2026-11-10）：发布页「背景音乐」从手填音乐 ID 改成
/// 「搜索 / 分类列表 → 点选」。
///
/// 端点来自 creator 发布页 JS（async chunk 358，模块 70858 / 2xxxx）原文：
/// <code>
///   GET /web/api/media/music/category          → { categories:[{category_name, category_id, type}] }
///   GET /web/api/media/music/list              → { songs:[{music_id, music_name, music_author, duration, …}], cursor, has_more }
///   GET /web/api/media/aweme/search/post/auth  → { signature }   // tsearch 的 Agw-Auth
///   GET https://tsearch.amemv.com/openapi/aweme/v1/music/search/
///        ?aid=1128&amp;count=20&amp;search_source=normal_search&amp;keyword=…&amp;cursor=…
///        头：Agw-Auth: &lt;signature&gt; / Openapi-Omit-Shark: 1
/// </code>
///
/// 与热点词一样，**选中结果只写既有字段** <c>common.music_id</c>（<see cref="PlatformParams.MusicId"/>），
/// wire 与存档不变；本服务只负责把人话搜索变成 music_id。
/// </summary>
public sealed class DouyinMusicService
{
    /// <summary>tsearch 搜索域名（页面 axios 用绝对 URL，withCredentials=false）。</summary>
    private const string TSearchHost = "https://tsearch.amemv.com";

    private readonly PlatformOptions _options;
    private readonly ILogger<DouyinMusicService>? _logger;

    public DouyinMusicService(PlatformOptions options, ILogger<DouyinMusicService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 配乐分类（发布页音乐面板的二级页签：例如「我的收藏」「推荐」「热歌榜」）。
    /// 分类为空时调用方应回落「搜索」。
    /// </summary>
    public async Task<IReadOnlyList<MusicCategory>> GetCategoriesAsync(
        string cookie, CancellationToken ct = default)
    {
        using var client = BuildClient(cookie);
        var url = $"{_options.DouyinCreatorHost}/web/api/media/music/category?{BuildSignedQuery()}";

        var response = await client.GetJsonAsync<MusicCategoryResponse>(url, ct);
        if (response is null || response.StatusCode != 0)
        {
            Serilog.Log.Warning(
                "[DouyinMusic] music/category 非 0：status={Status}", response?.StatusCode);
            return [];
        }

        return (response.Categories ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.CategoryId))
            .Select(c => new MusicCategory(
                c.CategoryName ?? "", c.CategoryId ?? "", c.Type ?? ""))
            .ToList();
    }

    /// <summary>按分类翻页取音乐（cursor 首次传 0）。</summary>
    public async Task<MusicListPage> ListAsync(
        string cookie,
        MusicCategory category,
        long cursor = 0,
        int count = 20,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie);

        var extra = new Dictionary<string, string>
        {
            ["cursor"] = cursor.ToString(),
            ["count"] = count.ToString(),
            ["category_id"] = category.CategoryId,
            ["type"] = category.Type,
        };
        var url = $"{_options.DouyinCreatorHost}/web/api/media/music/list?{BuildSignedQuery(extra)}";

        var response = await client.GetJsonAsync<MusicListResponse>(url, ct);
        if (response is null || response.StatusCode != 0)
        {
            Serilog.Log.Warning(
                "[DouyinMusic] music/list 非 0：status={Status} category={Category}",
                response?.StatusCode, category.Name);
            return new MusicListPage { Items = [], Cursor = cursor, HasMore = false };
        }

        var items = (response.Songs ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.MusicName))
            .Where(s => s.MusicId is not null and > 0)
            .Select(s => new MusicItem(
                s.MusicId!.Value.ToString(),
                s.MusicName ?? "",
                s.MusicAuthor ?? "",
                s.Duration ?? 0,
                s.CoverUrl))
            .ToList();

        return new MusicListPage
        {
            Items = items,
            Cursor = response.Cursor ?? 0,
            HasMore = response.HasMore == 1,
        };
    }

    /// <summary>
    /// 按关键词搜索音乐（发布页搜索框）。
    ///
    /// 两步：先拿 <c>/web/api/media/aweme/search/post/auth</c> 的 signature 当 <c>Agw-Auth</c>，
    /// 再打 tsearch 的 <c>openapi/aweme/v1/music/search</c>（**不带 cookie**，页面就是
    /// <c>withCredentials:false</c>）。
    /// </summary>
    public async Task<MusicListPage> SearchAsync(
        string cookie,
        string keyword,
        long cursor = 0,
        int count = 20,
        CancellationToken ct = default)
    {
        // ① 取 Agw-Auth 签名（带 cookie + msToken/a_bogus 的 creator 接口）
        string? signature;
        using (var authClient = BuildClient(cookie))
        {
            var authUrl =
                $"{_options.DouyinCreatorHost}/web/api/media/aweme/search/post/auth?{BuildSignedQuery()}";
            var auth = await authClient.GetJsonAsync<SearchAuthResponse>(authUrl, ct);
            signature = auth?.Signature;
            if (string.IsNullOrWhiteSpace(signature))
            {
                Serilog.Log.Warning(
                    "[DouyinMusic] post/auth 未拿到 signature：status={Status}", auth?.StatusCode);
                return new MusicListPage { Items = [], Cursor = cursor, HasMore = false };
            }
        }

        // ② tsearch 音乐搜索（响应字段与 creator 音乐库不同：music[] / id_str / play_url.url_list）
        using var client = BuildClient(cookie: null);
        var qs = string.Join("&", new Dictionary<string, string>
        {
            ["aid"] = "1128",                     // 页面常量 DOUYIN = 1128
            ["count"] = count.ToString(),
            ["search_source"] = "normal_search",
            ["keyword"] = keyword,
            ["cursor"] = cursor.ToString(),
        }.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{TSearchHost}/openapi/aweme/v1/music/search/?{qs}";

        var headers = new Dictionary<string, string>
        {
            ["Agw-Auth"] = signature,
            ["Openapi-Omit-Shark"] = "1",
        };

        var response = await client.GetJsonAsync<MusicSearchResponse>(url, headers, ct);
        if (response is null || response.StatusCode != 0)
        {
            Serilog.Log.Warning(
                "[DouyinMusic] tsearch music/search 非 0：status={Status} keyword={Keyword}",
                response?.StatusCode, keyword);
            return new MusicListPage { Items = [], Cursor = cursor, HasMore = false };
        }

        var items = (response.Music ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Title))
            .Select(m => new MusicItem(
                m.IdStr ?? "",
                m.Title ?? "",
                m.Author ?? "",
                m.Duration ?? 0,
                m.CoverThumb?.UrlList?.FirstOrDefault()))
            .Where(m => m.Id.Length > 0)
            .ToList();

        Serilog.Log.Information(
            "[DouyinMusic] music/search 返回 {Count} 条（keyword={Keyword}）", items.Count, keyword);

        return new MusicListPage
        {
            Items = items,
            Cursor = response.Cursor ?? 0,
            HasMore = response.HasMore == 1,
        };
    }

    // ── 签名 / client ──

    /// <summary>
    /// creator 域名的查询串：msToken 本地生成、a_bogus 本地计算（aid=1128，
    /// 与热点词 / 话题同一条签名路）。签名用的 base 串与最终 URL 必须是同一份参数。
    /// </summary>
    private string BuildSignedQuery(Dictionary<string, string>? extra = null)
    {
        extra ??= [];
        var env = _options.Environment;
        var userAgent = env?.UserAgent ?? _options.UserAgent;
        var baseParams = PlatformHttpClient.BuildQueryString(extra, env: env);
        var msToken = MsTokenGenerator.Generate();
        var aBogus = ABogusGenerator.Generate(baseParams, userAgent, aid: 1128);
        return PlatformHttpClient.BuildQueryString(extra, msToken, aBogus, env);
    }

    /// <summary>
    /// <paramref name="cookie"/> 为 null 时不挂 CookieHandler —— tsearch 是跨域接口，
    /// 页面就是 <c>withCredentials:false</c>（浏览器不发送 cookie）。
    /// </summary>
    private PlatformHttpClient BuildClient(string? cookie)
    {
        var logging = new Core.Handlers.LoggingHandler(null);
        HttpMessageHandler handler = logging;
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            handler = new Core.Handlers.CookieAuthHandler(cookie) { InnerHandler = logging };
        }
        return new PlatformHttpClient(handler, _options, null);
    }
}

// ═══ DTO — creator 音乐库 / tsearch 音乐搜索响应结构 ═══

file sealed class MusicCategoryResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; init; }

    [JsonPropertyName("categories")]
    public List<MusicCategoryDto>? Categories { get; init; }
}

file sealed class MusicCategoryDto
{
    [JsonPropertyName("category_name")]
    public string? CategoryName { get; init; }

    [JsonPropertyName("category_id")]
    public string? CategoryId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

file sealed class MusicListResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; init; }

    [JsonPropertyName("songs")]
    public List<MusicSongDto>? Songs { get; init; }

    /// <summary>实测是**字符串**（如 "0"），别按数字读 —— 用宽容转换器接两种</summary>
    [JsonPropertyName("cursor")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? Cursor { get; init; }

    /// <summary>平台给 0/1（不是 bool；类型也可能浮动）</summary>
    [JsonPropertyName("has_more")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? HasMore { get; init; }
}

file sealed class MusicSongDto
{
    [JsonPropertyName("music_id")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? MusicId { get; init; }

    [JsonPropertyName("music_name")]
    public string? MusicName { get; init; }

    [JsonPropertyName("music_author")]
    public string? MusicAuthor { get; init; }

    [JsonPropertyName("duration")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Duration { get; init; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; init; }
}

file sealed class SearchAuthResponse
{
    [JsonPropertyName("status_code")]
    public int? StatusCode { get; init; }

    /// <summary>tsearch 的 Agw-Auth 值</summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; init; }
}

file sealed class MusicSearchResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; init; }

    [JsonPropertyName("music")]
    public List<MusicSearchItemDto>? Music { get; init; }

    [JsonPropertyName("cursor")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? Cursor { get; init; }

    [JsonPropertyName("has_more")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? HasMore { get; init; }
}

file sealed class MusicSearchItemDto
{
    [JsonPropertyName("id_str")]
    public string? IdStr { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("duration")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Duration { get; init; }

    [JsonPropertyName("cover_thumb")]
    public UrlListDto? CoverThumb { get; init; }
}

file sealed class UrlListDto
{
    [JsonPropertyName("url_list")]
    public List<string>? UrlList { get; init; }
}

// ═══ 公共返回类型 ═══

/// <summary>配乐分类（发布页音乐面板二级页签）。</summary>
/// <param name="Name">显示名，例如「我的收藏」</param>
/// <param name="CategoryId">平台分类 ID</param>
/// <param name="Type">平台分类类型（与 category_id 一起构成 tab key）</param>
public sealed record MusicCategory(string Name, string CategoryId, string Type);

/// <summary>一首可选的配乐。</summary>
/// <param name="Id">music_id — 选中后写 <c>common.music_id</c></param>
/// <param name="Title">歌名</param>
/// <param name="Author">作者</param>
/// <param name="DurationSeconds">时长（秒）</param>
/// <param name="CoverUrl">封面（UI 展示用，可为空）</param>
public sealed record MusicItem(
    string Id,
    string Title,
    string Author,
    int DurationSeconds,
    string? CoverUrl)
{
    /// <summary>时长显示文本，如 "03:24"</summary>
    public string DurationDisplay => DurationSeconds > 0
        ? TimeSpan.FromSeconds(DurationSeconds).ToString(@"mm\:ss")
        : "";
}

/// <summary>一页音乐结果（分类列表 / 搜索共用）。</summary>
public sealed class MusicListPage
{
    public List<MusicItem> Items { get; init; } = [];

    /// <summary>翻页游标（下一页请求带上）</summary>
    public long Cursor { get; init; }

    public bool HasMore { get; init; }
}

// ═══ 宽容数字转换器 ═══

/// <summary>
/// 抖音音乐接口的数字字段类型不稳：2026-11-10 实测 <c>music/list</c> 的 <c>cursor</c>
/// 回的是**字符串** <c>"0"</c>，按 long 读会 JsonException 把整个列表打掉。
/// number / string / 布尔 / null 全接住，解析不了给 null 而不是抛。
/// </summary>
file sealed class FlexibleLongConverter : JsonConverter<long?>
{
    public override long? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var n)) return n;
                return reader.TryGetDouble(out var d) ? (long)d : null;
            case JsonTokenType.String:
                return long.TryParse(reader.GetString(), out var parsed) ? parsed : null;
            case JsonTokenType.True:
                return 1;
            case JsonTokenType.False:
                return 0;
            case JsonTokenType.Null:
                return null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}

/// <summary><see cref="FlexibleLongConverter"/> 的 int 版（越界钳到 int 范围）。</summary>
file sealed class FlexibleIntConverter : JsonConverter<int?>
{
    public override int? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var n)) return n;
                return reader.TryGetInt64(out var l)
                    ? (int)Math.Clamp(l, int.MinValue, int.MaxValue)
                    : null;
            case JsonTokenType.String:
                return int.TryParse(reader.GetString(), out var parsed) ? parsed : null;
            case JsonTokenType.True:
                return 1;
            case JsonTokenType.False:
                return 0;
            case JsonTokenType.Null:
                return null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}
