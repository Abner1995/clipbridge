using Serilog;

using CreatFlow.Platforms.Kuaishou;

namespace CreatFlow.Platforms.Topics;

/// <summary>
/// 快手话题 / 官方活动（2026-11-05 抓包：<c>docs/kuaishou/20261105/capture-2026-11-05T03-09-09.jsonl</c>）。
///
/// 端点：
///   · 搜索 <c>POST /rest/cp/works/v2/video/pc/tag/search</c>
///     body <c>{count, keyword:"关键词 ", page:"0"}</c> —— **关键词末尾必须追加 NBSP**
///     （浏览器同款，服务端按它做补全）；首条是关键词回显 <c>id=0</c>，要过滤。
///   · 推荐 <c>POST /rest/cp/works/v2/video/pc/upload/recTag</c> body <c>{editSessionId}</c>
///     —— 必须有上传会话 ID（上传完成后才有）。
///   · 活动列表 <c>POST /rest/v2/creator/activity/pc/list</c>
///     body <c>{page,count,category,rewardType,sortType,pageSource:2}</c>。
///   · 活动分类 <c>POST /rest/v2/creator/activity/pc/tab</c>；
///     筛选字典 <c>POST /rest/v2/creator/activity/pc/filter</c>。
///
/// 签名：走 <see cref="KuaishouApiClient"/> 的 Handler 链（Cookie → ApiPh → Sig → …），
/// 与发布链路同一条；活动接口的 Referer 用**发布页**（抓包原文），不是默认管理页。
/// </summary>
public sealed class KuaishouTopicApi : ITopicSuggestService, IActivityListService
{
    public static readonly TopicCapability StaticCapability = new(
        SupportsSearch: true,
        SupportsRecommend: true,
        SupportsActivities: true,
        RecommendNeedsUpload: true);

    private const string TagSearchPath = "/rest/cp/works/v2/video/pc/tag/search";
    private const string RecTagPath = "/rest/cp/works/v2/video/pc/upload/recTag";
    private const string ActivityListPath = "/rest/v2/creator/activity/pc/list";
    private const string ActivityTabPath = "/rest/v2/creator/activity/pc/tab";
    private const string ActivityFilterPath = "/rest/v2/creator/activity/pc/filter";

    /// <summary>活动接口抓包 Referer：发布页（不是管理页）</summary>
    private const string PublishReferer = "https://cp.kuaishou.com/article/publish/video";

    /// <summary>
    /// 关键词后缀 —— 浏览器在 tag/search 里发的是「关键词 + U+00A0」，
    /// 服务端按它识别「正在输入」并给补全（现有 <c>KuaishouHelperService</c> 同款）。
    /// </summary>
    private const string KeywordSuffix = "\u00A0";

    private readonly PlatformOptions _options;

    public KuaishouTopicApi(PlatformOptions options) => _options = options;

    public string PlatformKey => PlatformNames.Kuaishou;

    public TopicCapability Capability => StaticCapability;

    public bool Supported => true;

    // ═══ 话题 ═══

    public async Task<TopicFetchResult> SearchAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Keyword)) return TopicFetchResult.Empty;

        try
        {
            using var client = CreateClient(credentials);
            var body = new
            {
                count = Math.Clamp(query.Count, 1, 50),
                keyword = query.Keyword + KeywordSuffix,
                // 快手是 0-based 页码；我们对外统一 1-based
                page = Math.Max(0, query.Page - 1).ToString(),
            };
            var data = await client.PostAsync<KuaishouTagSearchData>(TagSearchPath, body, ct);

            var items = (data.Tags ?? [])
                .Where(t => t.Tag is { Id: > 0 } && !string.IsNullOrWhiteSpace(t.Tag.Name))
                .Select(t => new TopicSuggestion
                {
                    Name = t.Tag!.Name!,
                    PlatformId = t.Tag.Id.ToString(),
                    ViewCount = t.ViewCount,
                })
                .ToList();

            return new TopicFetchResult(items);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[KuaishouTopic] 搜索失败 keyword={Keyword}", query.Keyword);
            return TopicFetchResult.Fail(Describe(ex, "快手话题搜索"));
        }
    }

    public async Task<TopicFetchResult> RecommendAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.EditSessionId))
        {
            return TopicFetchResult.Fail(
                "快手推荐话题需要上传完成后的会话 ID —— 先选视频进入上传，上传完成后再获取推荐");
        }

        try
        {
            using var client = CreateClient(credentials);
            var body = new { editSessionId = query.EditSessionId };
            var data = await client.PostAsync<KuaishouRecTagData>(RecTagPath, body, ct);

            var items = (data.Tags ?? [])
                .Where(t => t.Tag is { Id: > 0 } && !string.IsNullOrWhiteSpace(t.Tag.Name))
                .Select(t => new TopicSuggestion
                {
                    Name = t.Tag!.Name!,
                    PlatformId = t.Tag.Id.ToString(),
                    ViewCount = t.ViewCount,
                })
                .ToList();

            return new TopicFetchResult(items);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[KuaishouTopic] 推荐失败");
            return TopicFetchResult.Fail(Describe(ex, "快手推荐话题"));
        }
    }

    // ═══ 官方活动 ═══

    public async Task<ActivityFetchResult> ListAsync(
        PlatformCredentials credentials, ActivityQuery query, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient(credentials);
            var body = new
            {
                page = Math.Max(1, query.Page),
                count = Math.Clamp(query.Count, 1, 100),
                category = query.CategoryId ?? 0,
                rewardType = query.RewardType ?? 0,
                sortType = query.SortType ?? 0,
                pageSource = 2,
            };
            var data = await client.PostAsync<KuaishouActivityListData>(
                ActivityListPath, body, PublishReferer, ct);

            return new ActivityFetchResult((data.List ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a.ActivityId))
                .Select(ToActivity)
                .ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[KuaishouTopic] 活动列表失败");
            return ActivityFetchResult.Fail(Describe(ex, "快手活动列表"));
        }
    }

    /// <summary>活动分类（综合/泛娱乐/房产家居…）—— tab 接口。</summary>
    public async Task<IReadOnlyList<ActivityCategory>> GetCategoriesAsync(
        PlatformCredentials credentials, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient(credentials);
            var data = await client.PostAsync<KuaishouActivityTabData>(
                ActivityTabPath, new { }, PublishReferer, ct);
            return MapOptions(data.List);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[KuaishouTopic] 活动分类失败");
            return [];
        }
    }

    /// <summary>活动筛选字典（分类 / 奖励类型 / 排序 / 活动类型）—— filter 接口。</summary>
    public async Task<ActivityFilterOptions> GetFiltersAsync(
        PlatformCredentials credentials, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient(credentials);
            var data = await client.PostAsync<KuaishouActivityFilterData>(
                ActivityFilterPath, new { }, PublishReferer, ct);
            return new ActivityFilterOptions(
                Categories: MapOptions(data.TabList),
                RewardTypes: MapOptions(data.RewardType),
                SortTypes: MapOptions(data.SortType),
                TagTypes: MapOptions(data.TagType));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[KuaishouTopic] 活动筛选字典失败");
            return ActivityFilterOptions.Empty;
        }
    }

    // ═══ 纯映射 ═══

    /// <summary>快手活动条目 → <see cref="PlatformActivity"/>（公开 static 供自检钉字段映射）。</summary>
    public static PlatformActivity ToActivity(KuaishouActivityItem a)
    {
        var topics = (a.TagConditionList ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => new TopicSuggestion
            {
                // 活动关联话题只有名字（topics 字段本次抓包为 null）——
                // 选活动后按名字走一次 tag/search 拿 ID（2.6.4 的「自动带话题」）。
                Name = t,
            })
            .ToList();

        return new PlatformActivity
        {
            Id = a.ActivityId ?? a.Id.ToString(),
            Name = a.Title ?? "",
            CoverUrl = a.HeadUrl,
            Reward = string.IsNullOrWhiteSpace(a.TopGain) ? null : a.TopGain,
            StartTime = FromUnixMs(a.BeginTime),
            EndTime = FromUnixMs(a.EndTime),
            ParticipantCount = a.Number,
            CustomTag = a.CustomTag,
            Topics = topics,
            ExtraJson = JsonSerializer.Serialize(new
            {
                id = a.Id,
                status = a.Status,
                participated = a.Participated,
                activity_level = a.ActivityLevel,
                reward_type = a.RewardType,
                tag_labels = a.TagLabels,
                brief = a.Brief,
            }),
        };
    }

    private static IReadOnlyList<ActivityCategory> MapOptions(List<KuaishouOptionItem>? options) =>
        (options ?? [])
            .Where(o => o.Label is { Length: > 0 })
            .Select(o => new ActivityCategory(o.Value.ToString(), o.Label!))
            .ToList();

    private static DateTimeOffset? FromUnixMs(long ms) =>
        ms <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();

    // ═══ HTTP ═══

    private KuaishouApiClient CreateClient(PlatformCredentials credentials)
    {
        var apiPh = ExtractCookieValue(credentials.Cookie, "kuaishou.web.cp.api_ph");
        return new KuaishouApiClient(credentials.Cookie, _options, apiPh);
    }

    private static string? ExtractCookieValue(string cookie, string name)
    {
        foreach (var part in cookie.Split(';'))
        {
            var trimmed = part.Trim();
            var eq = trimmed.IndexOf('=');
            if (eq > 0 && trimmed[..eq] == name) return trimmed[(eq + 1)..];
        }
        return null;
    }

    private static string Describe(Exception ex, string what) =>
        ex is TaskCanceledException or OperationCanceledException
            ? $"{what}超时（网络或签名服务无响应）"
            : $"{what}失败：{ex.Message}";
}

/// <summary>快手活动筛选字典（filter 接口）。</summary>
public sealed record ActivityFilterOptions(
    IReadOnlyList<ActivityCategory> Categories,
    IReadOnlyList<ActivityCategory> RewardTypes,
    IReadOnlyList<ActivityCategory> SortTypes,
    IReadOnlyList<ActivityCategory> TagTypes)
{
    public static ActivityFilterOptions Empty { get; } = new([], [], [], []);
}

// ═══ 快手响应 DTO ═══

internal sealed class KuaishouTagSearchData
{
    [JsonPropertyName("tags")]
    public List<KuaishouTagEntry>? Tags { get; set; }
}

internal sealed class KuaishouRecTagData
{
    [JsonPropertyName("tags")]
    public List<KuaishouTagEntry>? Tags { get; set; }
}

internal sealed class KuaishouTagEntry
{
    [JsonPropertyName("tag")]
    public KuaishouTagInfo? Tag { get; set; }

    [JsonPropertyName("viewCount")]
    public long ViewCount { get; set; }
}

internal sealed class KuaishouTagInfo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class KuaishouActivityListData
{
    [JsonPropertyName("list")]
    public List<KuaishouActivityItem>? List { get; set; }
}

/// <summary>快手活动条目（公开：<see cref="KuaishouTopicApi.ToActivity"/> 自检直测用）。</summary>
public sealed class KuaishouActivityItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("activityId")]
    public string? ActivityId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("headUrl")]
    public string? HeadUrl { get; set; }

    [JsonPropertyName("number")]
    public long Number { get; set; }

    [JsonPropertyName("beginTime")]
    public long BeginTime { get; set; }

    [JsonPropertyName("endTime")]
    public long EndTime { get; set; }

    [JsonPropertyName("topGain")]
    public string? TopGain { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("participated")]
    public bool Participated { get; set; }

    [JsonPropertyName("customTag")]
    public string? CustomTag { get; set; }

    [JsonPropertyName("tagConditionList")]
    public List<string>? TagConditionList { get; set; }

    [JsonPropertyName("tagLabels")]
    public List<string>? TagLabels { get; set; }

    [JsonPropertyName("brief")]
    public string? Brief { get; set; }

    [JsonPropertyName("activityLevel")]
    public int ActivityLevel { get; set; }

    [JsonPropertyName("rewardType")]
    public List<int>? RewardType { get; set; }
}

internal sealed class KuaishouActivityTabData
{
    [JsonPropertyName("list")]
    public List<KuaishouOptionItem>? List { get; set; }
}

internal sealed class KuaishouActivityFilterData
{
    [JsonPropertyName("tabList")]
    public List<KuaishouOptionItem>? TabList { get; set; }

    [JsonPropertyName("rewardType")]
    public List<KuaishouOptionItem>? RewardType { get; set; }

    [JsonPropertyName("sortType")]
    public List<KuaishouOptionItem>? SortType { get; set; }

    [JsonPropertyName("tagType")]
    public List<KuaishouOptionItem>? TagType { get; set; }
}

internal sealed class KuaishouOptionItem
{
    [JsonPropertyName("value")]
    public int Value { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}
