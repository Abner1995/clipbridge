namespace CreatFlow.Platforms.Douyin;

using CreatFlow.Platforms.Core;
using Serilog;

/// <summary>
/// 抖音话题搜索服务 — 对标 Python challengesug.py。
///
/// 端点: GET /aweme/v1/search/challengesug/?keyword=xxx
/// 用途: 根据 #话题 名称获取 cid (hashtag_id)，用于构造 create_v2 的 text_extra + challenges。
/// </summary>
public sealed class DouyinHashtagService
{
    private readonly PlatformOptions _options;

    public DouyinHashtagService(PlatformOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 搜索话题建议 — 对标浏览器输入 # 时的自动补全。
    /// GET creator.douyin.com/aweme/v1/search/challengesug/?keyword=xxx
    ///
    /// 自动生成 msToken + a_bogus（使用正确的 query params）。
    /// </summary>
    /// <param name="cookieJar">平台 Cookie</param>
    /// <param name="keyword">搜索关键词（不带 #）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>话题列表，按相关度排序</returns>
    public async Task<List<DouyinHashtagResult>> SearchAsync(
        CookieJar cookieJar,
        string keyword,
        CancellationToken ct = default)
    {
        // Build query params FIRST, then generate aBogus against those exact params
        var extra = new Dictionary<string, string>
        {
            ["source"] = "challenge_create",
            ["aid"] = "2906",
            ["keyword"] = keyword,
        };
        // a_bogus 与请求头 UA 必须同源（与 PublishPipeline 同口径）：有环境快照用快照 UA，缺则硬编码兜底
        var baseParams = PlatformHttpClient.BuildQueryString(extra, env: _options.Environment);
        var msToken = MsTokenGenerator.Generate();
        var aBogus = ABogusGenerator.Generate(baseParams,
            _options.Environment?.UserAgent ?? _options.UserAgent, aid: 2906);

        using var client = BuildClient(cookieJar);

        var path = "/aweme/v1/search/challengesug/";
        var qs = PlatformHttpClient.BuildQueryString(
            extra,
            msToken: msToken,
            aBogus: aBogus,
            env: _options.Environment);
        var url = $"{_options.DouyinCreatorHost}{path}?{qs}";

        Log.Debug("ChallengeSug → keyword={Keyword}", keyword);

        try
        {
            var json = await client.GetStringAsync(url, ct);
            var resp = JsonSerializer.Deserialize<DouyinChallengeSugResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (resp?.SugList == null || resp.SugList.Count == 0)
            {
                Log.Debug("ChallengeSug: no results for '{Keyword}'", keyword);
                return [];
            }

            return resp.SugList
                .Select(s => new DouyinHashtagResult(s.ChaName, s.Cid, s.ViewCount))
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "ChallengeSug HTTP error for '{Keyword}'", keyword);
            return [];
        }
    }

    private PlatformHttpClient BuildClient(CookieJar jar)
    {
        var loggingHandler = new Core.Handlers.LoggingHandler(null);
        var cookieHandler = new Core.Handlers.CookieAuthHandler(jar);
        var secSdkHandler = new Handlers.SecSdkHandler();

        secSdkHandler.InnerHandler = cookieHandler;
        cookieHandler.InnerHandler = loggingHandler;

        return new PlatformHttpClient(secSdkHandler, _options, null);
    }
}

/// <summary>挑战搜索 API 响应 — 对标 challengesug.json</summary>
public sealed class DouyinChallengeSugResponse
{
    [JsonPropertyName("sug_list")]
    public List<DouyinChallengeSugItem>? SugList { get; set; }

    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }
}

/// <summary>话题建议条目</summary>
public sealed class DouyinChallengeSugItem
{
    /// <summary>话题名称，如 "美丽的花"</summary>
    [JsonPropertyName("cha_name")]
    public string ChaName { get; set; } = "";

    /// <summary>话题 ID (hashtag_id / cid)</summary>
    [JsonPropertyName("cid")]
    public string Cid { get; set; } = "";

    /// <summary>播放量</summary>
    [JsonPropertyName("view_count")]
    public long ViewCount { get; set; }
}

/// <summary>话题搜索结果（简化）</summary>
public sealed record DouyinHashtagResult(
    string Name,
    string Cid,
    long ViewCount
);
