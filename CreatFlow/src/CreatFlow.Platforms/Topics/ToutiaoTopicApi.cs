using Serilog;

using CreatFlow.Platforms.Toutiao;

namespace CreatFlow.Platforms.Topics;

/// <summary>
/// 头条话题（2026-11-05 抓包：<c>docs/toutiao/20261105/capture-2026-11-05T01-02-37.jsonl</c>）。
///
/// 唯一端点：<c>GET /video/app/hashtag/rec?vid={Vid}</c> —— **上传完成后**按视频推荐话题：
/// <code>{ "code":0, "data":{ "hashtag_list":[…], "recommend_id":"…" }, "message":"" }</code>
///
/// 两次样本的 <c>hashtag_list</c> 都是空（一次连键都没有）—— **元素结构未抓到**：
/// 解析器按 <c>name/tag_name/hashtag_name/title/text</c> 与
/// <c>id/hashtag_id/topic_id/tag_id</c> 宽容取值，认不出就跳过（不猜、不静默）。
/// 真账号探针拿到非空样本后再收窄字段（见进度表 2.6.1 探针项）。
///
/// 头条**没有话题搜索 / 没有视频活动接口**（<c>activity_tag</c> 是文章字段）——
/// 搜索/活动分别返回明确原因，UI 按 <see cref="Capability"/> 不显示搜索框与活动块。
/// </summary>
public sealed class ToutiaoTopicApi : ITopicSuggestService, IActivityListService
{
    public static readonly TopicCapability StaticCapability = new(
        SupportsSearch: false,
        SupportsRecommend: true,
        SupportsActivities: false,
        RecommendNeedsUpload: true);

    private const string HashtagRecPath = "/video/app/hashtag/rec";

    /// <summary>抓包 Referer 的稳定前缀（后面是 pgc_id，随作品变，不带也能过）</summary>
    private const string Referer = "https://mp.toutiao.com/profile_v4/xigua/upload-video";

    private readonly PlatformOptions _options;

    public ToutiaoTopicApi(PlatformOptions options) => _options = options;

    public string PlatformKey => PlatformNames.Toutiao;

    public TopicCapability Capability => StaticCapability;

    public bool Supported => false;

    // ═══ 话题 ═══

    public Task<TopicFetchResult> SearchAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
        => Task.FromResult(TopicFetchResult.Fail(
            "头条没有话题搜索接口 —— 请在正文直接输入 #话题，或上传视频后获取平台推荐"));

    public async Task<TopicFetchResult> RecommendAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.VideoId))
        {
            return TopicFetchResult.Fail(
                "视频上传完成后才能获取头条推荐话题（接口依赖 Vid）");
        }

        try
        {
            using var client = new ToutiaoApiClient(
                credentials.Cookie, _options, credentials.CsrfToken, Referer);
            var url = $"{_options.ToutiaoHost}{HashtagRecPath}"
                      + $"?vid={Uri.EscapeDataString(query.VideoId)}";
            var resp = await client.GetJsonAsync<ToutiaoHashtagRecResponse>(url, ct);

            if (resp is null)
                return TopicFetchResult.Fail("头条推荐话题：响应为空");
            if (resp.Code != 0)
                return TopicFetchResult.Fail($"头条推荐话题失败：code={resp.Code}，{resp.Message}");

            return new TopicFetchResult(ParseHashtagList(resp.Data?.HashtagList));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ToutiaoTopic] 推荐失败 vid={Vid}", query.VideoId);
            return TopicFetchResult.Fail(Describe(ex, "头条推荐话题"));
        }
    }

    /// <summary>解析 <c>hashtag_list</c>（元素结构未抓到 —— 宽容取名字/ID，认不出跳过）。</summary>
    public static IReadOnlyList<TopicSuggestion> ParseHashtagList(List<JsonElement>? items)
    {
        if (items is null || items.Count == 0) return [];

        var list = new List<TopicSuggestion>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var name = FirstString(item, "name", "tag_name", "hashtag_name", "title", "text");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var id = FirstScalar(item, "id", "hashtag_id", "topic_id", "tag_id");
            list.Add(new TopicSuggestion
            {
                Name = name,
                PlatformId = string.IsNullOrWhiteSpace(id) ? null : id,
                ExtraJson = item.GetRawText(),
            });
        }
        return list;
    }

    // ═══ 官方活动（视频发布体没有字段） ═══

    public Task<ActivityFetchResult> ListAsync(
        PlatformCredentials credentials, ActivityQuery query, CancellationToken ct = default)
        => Task.FromResult(ActivityFetchResult.Fail(
            "头条视频发布没有官方活动接口（activity_tag 是文章字段）"));

    public Task<IReadOnlyList<ActivityCategory>> GetCategoriesAsync(
        PlatformCredentials credentials, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ActivityCategory>>([]);

    // ═══ 小工具 ═══

    private static string? FirstString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString();
        }
        return null;
    }

    private static string? FirstScalar(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Number) return v.ToString();
        }
        return null;
    }

    private static string Describe(Exception ex, string what) =>
        ex is TaskCanceledException or OperationCanceledException
            ? $"{what}超时（网络或签名服务无响应）"
            : $"{what}失败：{ex.Message}";
}

// ═══ 头条 hashtag/rec DTO（data.hashtag_list 元素结构未抓到，保留 JsonElement） ═══

internal sealed class ToutiaoHashtagRecResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public ToutiaoHashtagRecData? Data { get; set; }
}

internal sealed class ToutiaoHashtagRecData
{
    [JsonPropertyName("hashtag_list")]
    public List<JsonElement>? HashtagList { get; set; }

    [JsonPropertyName("recommend_id")]
    public string? RecommendId { get; set; }
}
