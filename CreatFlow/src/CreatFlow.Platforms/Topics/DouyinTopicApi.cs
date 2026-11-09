using Serilog;

using System.Globalization;
using CreatFlow.Platforms.Douyin;

namespace CreatFlow.Platforms.Topics;

/// <summary>
/// 抖音话题 / 官方活动（2026-11-05 抓包：<c>docs/douyin/20261105/capture-2026-11-05T01-40-33.jsonl</c>）。
///
/// 四个端点：
///   · 搜索 <c>GET /aweme/v1/search/challengesug/</c> —— <c>sug_list[{cha_name,cid,view_count,group_id}]</c>
///     （<c>cid</c> 可能为空串 —— 该条只能落纯文本）
///   · 活动列表 <c>GET /web/api/media/activity/get/?page=1&amp;size=9999&amp;need_challenge=1</c>
///     —— 发布页活动面板用的就是这个；条目带 <c>challenge_ids</c>（选中活动自动带话题）
///   · 活动分类 <c>GET /web/api/v2/creator/activity/tags/query</c> —— <c>query_tags[{id,name}]</c>
///   · 话题关联活动 <c>GET /web/api/media/hashtag/activity/?hashtag_ids=&amp;act_status=0</c>
///
/// 另有一个「创作者活动中心」列表 <c>/web/api/v2/creator/activity/pc/list</c>（首页入口），
/// 与 <c>media/activity/get</c> 字段略有不同（<c>show_name</c>、Unix 秒时间），作为兜底暴露。
///
/// 签名：与 <see cref="DouyinHashtagService"/> 同一条路 —— 本地 <see cref="MsTokenGenerator"/> +
/// <see cref="ABogusGenerator"/>（aid=2906 搜索 / 1128 活动）。抓包证明页面 XHR 也是同样的
/// query 组合；若真账号探针发现被签名拦截，再改走页面宿主（<c>DouyinWebExecutor</c>）。
/// </summary>
public sealed class DouyinTopicApi : ITopicSuggestService, IActivityListService
{
    public static readonly TopicCapability StaticCapability = new(
        SupportsSearch: true,
        SupportsRecommend: false,
        SupportsActivities: true);

    private const string SuggestPath = "/aweme/v1/search/challengesug/";
    private const string ActivityGetPath = "/web/api/media/activity/get/";
    private const string ActivityTagsPath = "/web/api/v2/creator/activity/tags/query";
    private const string ActivityPcListPath = "/web/api/v2/creator/activity/pc/list";
    private const string TopicActivityPath = "/web/api/media/hashtag/activity/";

    /// <summary>搜索接口固定参数（抓包原文：source=challenge_create&amp;aid=2906）</summary>
    private const int SearchAid = 2906;

    /// <summary>活动接口固定参数（抓包原文：aid=1128）</summary>
    private const int ActivityAid = 1128;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly PlatformOptions _options;

    public DouyinTopicApi(PlatformOptions options) => _options = options;

    public string PlatformKey => PlatformNames.Douyin;

    public TopicCapability Capability => StaticCapability;

    public bool Supported => true;

    // ═══ 话题 ═══

    /// <summary>关键词联想（challengesug）。<c>cid</c> 为空的话题照样返回，只是不能结构化下发。</summary>
    public async Task<TopicFetchResult> SearchAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Keyword)) return TopicFetchResult.Empty;

        try
        {
            var extra = new Dictionary<string, string>
            {
                ["source"] = "challenge_create",
                ["aid"] = SearchAid.ToString(),
                ["keyword"] = query.Keyword,
            };
            var json = await GetSignedAsync(credentials.Cookie, SuggestPath, extra, SearchAid, ct);
            return ParseSearch(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DouyinTopic] 搜索失败 keyword={Keyword}", query.Keyword);
            return TopicFetchResult.Fail(Describe(ex, "抖音话题搜索"));
        }
    }

    /// <summary>抖音没有「按标题推荐」的话题接口（<c>hashtag/rec</c> 吃已上传视频帧，不在这里暴露）。</summary>
    public Task<TopicFetchResult> RecommendAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
        => Task.FromResult(TopicFetchResult.Fail("抖音没有按标题推荐话题的接口，请用搜索或手动输入"));
    // 注：抖音确有 /web/api/media/hashtag/rec/?frames=（上传后按视频画面推荐），
    // 但它的输入是上传返回的帧 URI，不属于「编辑标题阶段」的话题推荐；接入发布链路后再补。

    // ═══ 官方活动 ═══

    /// <summary>活动列表 —— 发布页同款 <c>media/activity/get</c>（size=9999 一次拉全）。</summary>
    public async Task<ActivityFetchResult> ListAsync(
        PlatformCredentials credentials, ActivityQuery query, CancellationToken ct = default)
    {
        try
        {
            var extra = new Dictionary<string, string>
            {
                ["page"] = Math.Max(1, query.Page).ToString(),
                ["size"] = Math.Max(1, query.Count).ToString(),
                ["need_challenge"] = "1",
            };
            var json = await GetSignedAsync(credentials.Cookie, ActivityGetPath, extra, ActivityAid, ct);
            var items = ParseActivityGet(json);
            return new ActivityFetchResult(items);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DouyinTopic] 活动列表失败");
            return ActivityFetchResult.Fail(Describe(ex, "抖音活动列表"));
        }
    }

    /// <summary>活动分类（综合/随拍/家装…）—— UI 筛选条用。</summary>
    public async Task<IReadOnlyList<ActivityCategory>> GetCategoriesAsync(
        PlatformCredentials credentials, CancellationToken ct = default)
    {
        try
        {
            var json = await GetSignedAsync(
                credentials.Cookie, ActivityTagsPath, new Dictionary<string, string>(), ActivityAid, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("query_tags", out var arr)
                || arr.ValueKind != JsonValueKind.Array) return [];
            return [.. arr.EnumerateArray()
                .Select(t => new ActivityCategory(
                    t.TryGetProperty("id", out var id) ? id.ToString() : "",
                    Str(t, "name") ?? ""))
                .Where(c => c.Id.Length > 0 && c.Name.Length > 0)];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DouyinTopic] 活动分类失败");
            return [];
        }
    }

    /// <summary>
    /// 创作者活动中心列表（首页入口）—— 与 <see cref="ListAsync"/> 二选一。
    /// 字段差异：<c>show_name</c> / Unix 秒时间 / 没有 <c>reward_type</c>；作为兜底保留。
    /// </summary>
    public async Task<ActivityFetchResult> ListActivityCenterAsync(
        PlatformCredentials credentials, ActivityQuery query, CancellationToken ct = default)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var extra = new Dictionary<string, string>
            {
                ["start_time"] = now.AddDays(-30).ToUnixTimeSeconds().ToString(),
                ["end_time"] = now.AddDays(60).ToUnixTimeSeconds().ToString(),
            };
            var json = await GetSignedAsync(credentials.Cookie, ActivityPcListPath, extra, ActivityAid, ct);
            return new ActivityFetchResult(ParseActivityCenter(json));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DouyinTopic] 活动中心列表失败");
            return ActivityFetchResult.Fail(Describe(ex, "抖音活动中心列表"));
        }
    }

    /// <summary>
    /// 话题关联活动（<c>hashtag/activity</c>）。抓包样本的 <c>activity</c> 是空对象字符串
    /// <c>"{}"</c>，本次没有非空样本 —— 解析器容忍「对象 / JSON 字符串 / 空」三种形态，
    /// 认不出结构时返回空表（不猜、不静默报错）。
    /// </summary>
    public async Task<ActivityFetchResult> GetTopicActivitiesAsync(
        PlatformCredentials credentials, IEnumerable<string> topicIds, CancellationToken ct = default)
    {
        var ids = topicIds.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (ids.Length == 0) return ActivityFetchResult.Empty;

        try
        {
            var extra = new Dictionary<string, string>
            {
                ["hashtag_ids"] = string.Join(",", ids),
                ["act_status"] = "0",
            };
            var json = await GetSignedAsync(credentials.Cookie, TopicActivityPath, extra, ActivityAid, ct);
            return new ActivityFetchResult(ParseTopicActivity(json));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DouyinTopic] 话题关联活动失败");
            return ActivityFetchResult.Fail(Describe(ex, "抖音话题关联活动"));
        }
    }

    // ═══ 纯解析（自检可直测） ═══

    /// <summary>解析 challengesug（<c>sug_list[{cha_name,cid,view_count,group_id}]</c>）。</summary>
    public static TopicFetchResult ParseSearch(string json)
    {
        var resp = JsonSerializer.Deserialize<DouyinChallengeSugResponse>(json, JsonOpts);
        if (resp?.StatusCode is not (null or 0))
            return TopicFetchResult.Fail($"抖音话题联想失败：status_code={resp.StatusCode}");

        var items = (resp?.SugList ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.ChaName))
            .Select(s => new TopicSuggestion
            {
                Name = s.ChaName!,
                // 联想样本里 cid 可能是空串（该条只能落纯文本）
                PlatformId = string.IsNullOrWhiteSpace(s.Cid) ? null : s.Cid,
                ViewCount = s.ViewCount,
                GroupId = string.IsNullOrWhiteSpace(s.GroupId) ? null : s.GroupId,
            })
            .ToList();

        return new TopicFetchResult(items);
    }

    /// <summary>解析 <c>media/activity/get</c>。公开 static 供自检钉死字段映射。</summary>
    public static IReadOnlyList<PlatformActivity> ParseActivityGet(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("activity_list", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return [];

        var list = new List<PlatformActivity>();
        foreach (var item in arr.EnumerateArray())
        {
            var id = Str(item, "activity_id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            var challengeIds = LongArray(item, "challenge_ids");
            var challengeNames = StringArray(item, "challenge");
            var topics = new List<TopicSuggestion>();
            for (var i = 0; i < challengeIds.Count; i++)
            {
                var cid = challengeIds[i];
                if (cid <= 0) continue;   // 抓包里无话题的活动是 [0]
                topics.Add(new TopicSuggestion
                {
                    Name = i < challengeNames.Count ? challengeNames[i] : $"话题 {cid}",
                    PlatformId = cid.ToString(),
                });
            }

            var rewardType = Int(item, "reward_type");
            list.Add(new PlatformActivity
            {
                Id = id,
                Name = Str(item, "activity_name") ?? "",
                CoverUrl = Str(item, "cover_image"),
                Reward = rewardType > 0 ? "有奖活动" : null,
                StartTime = ParseDotDate(Str(item, "show_start_time")),
                EndTime = ParseDotDate(Str(item, "show_end_time"), endOfDay: true),
                HotScore = Long(item, "hot_score"),
                Topics = topics,
                ExtraJson = JsonSerializer.Serialize(new
                {
                    activity_type = Int(item, "activity_type"),
                    activity_level = Int(item, "activity_level"),
                    activity_status = Int(item, "activity_status"),
                    reward_type = rewardType,
                    query_tag = Long(item, "query_tag"),
                    jump_type = Int(item, "jump_type"),
                    jump_link = Str(item, "jump_link"),
                }),
            });
        }
        return list;
    }

    /// <summary>解析「创作者活动中心」<c>activity/pc/list</c>（<c>list[{show_name,show_start_time…}]</c>）。</summary>
    public static IReadOnlyList<PlatformActivity> ParseActivityCenter(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("list", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return [];

        var list = new List<PlatformActivity>();
        foreach (var item in arr.EnumerateArray())
        {
            var id = Str(item, "activity_id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            var challengeIds = LongArray(item, "challenge_ids");
            var topics = challengeIds.Where(cid => cid > 0)
                .Select(cid => new TopicSuggestion
                {
                    Name = $"话题 {cid}",
                    PlatformId = cid.ToString(),
                }).ToList();

            list.Add(new PlatformActivity
            {
                Id = id,
                Name = Str(item, "show_name") ?? "",
                CoverUrl = Str(item, "head_image"),
                StartTime = UnixSeconds(item, "show_start_time"),
                EndTime = UnixSeconds(item, "show_end_time"),
                HotScore = Long(item, "hot_score"),
                Topics = topics,
                ExtraJson = JsonSerializer.Serialize(new
                {
                    activity_level = Int(item, "activity_level"),
                    activity_type = Int(item, "activity_type"),
                    jump_type = Int(item, "jump_type"),
                    jump_link = Str(item, "jump_link"),
                    query_tag = Long(item, "query_tag"),
                }),
            });
        }
        return list;
    }

    /// <summary>解析 <c>hashtag/activity</c>（<c>activity</c> = 对象 or JSON 字符串 or "{}"）。</summary>
    public static IReadOnlyList<PlatformActivity> ParseTopicActivity(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("activity", out var el)) return [];

        // 形状一：activity 是对象；形状二：字符串里再套一份 JSON（抓包就是 "{}"）
        if (el.ValueKind == JsonValueKind.String)
        {
            var raw = el.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return [];
            using var inner = JsonDocument.Parse(raw);
            return ParseTopicActivityObject(inner.RootElement);
        }
        return ParseTopicActivityObject(el);
    }

    private static IReadOnlyList<PlatformActivity> ParseTopicActivityObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return [];
        if (!root.EnumerateObject().Any()) return [];   // "<空活动>" 不是错误

        // 非空样本未抓到 —— 兼容 activity_id / activity_name 的常见形状，认不出就返回空。
        var id = Str(root, "activity_id") ?? Str(root, "id");
        if (string.IsNullOrWhiteSpace(id)) return [];
        return
        [
            new PlatformActivity
            {
                Id = id,
                Name = Str(root, "activity_name") ?? Str(root, "name") ?? "",
                CoverUrl = Str(root, "cover_image") ?? Str(root, "cover"),
                ExtraJson = root.GetRawText(),
            },
        ];
    }

    // ═══ HTTP ═══

    /// <summary>
    /// 带 msToken + a_bogus 的 GET（与 <see cref="DouyinHashtagService"/> 同一条签名路）。
    /// a_bogus 只对「业务参数 + 浏览器指纹」签名，msToken/a_bogus 自身不进 base 串。
    /// </summary>
    private async Task<string> GetSignedAsync(
        string cookie, string path,
        Dictionary<string, string> extra, int aid, CancellationToken ct)
    {
        var baseParams = PlatformHttpClient.BuildQueryString(extra, env: _options.Environment);
        var msToken = MsTokenGenerator.Generate();
        var aBogus = ABogusGenerator.Generate(
            baseParams, _options.Environment?.UserAgent ?? _options.UserAgent, aid: aid);

        var qs = PlatformHttpClient.BuildQueryString(
            extra, msToken: msToken, aBogus: aBogus, env: _options.Environment);
        var url = $"{_options.DouyinCreatorHost}{path}?{qs}";

        using var client = BuildClient(cookie);
        return await client.GetStringAsync(url, ct);
    }

    private PlatformHttpClient BuildClient(string cookie)
    {
        var loggingHandler = new Core.Handlers.LoggingHandler(null);
        var cookieHandler = new Core.Handlers.CookieAuthHandler(cookie);
        cookieHandler.InnerHandler = loggingHandler;
        return new PlatformHttpClient(cookieHandler, _options, null);
    }

    // ═══ JSON 小工具 ═══

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var i) ? i : 0;

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var l) ? l : 0;

    private static List<long> LongArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<long>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var l)) list.Add(l);
            else if (el.ValueKind == JsonValueKind.String
                     && long.TryParse(el.GetString(), out var s)) list.Add(s);
        }
        return list;
    }

    private static List<string> StringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return [.. arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(s => s.Length > 0)];
    }

    /// <summary>Unix 秒 → 本地时间（抓包 <c>show_start_time:1793271601</c>）。</summary>
    private static DateTimeOffset? UnixSeconds(JsonElement obj, string name)
    {
        var v = Long(obj, name);
        return v <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(v).ToLocalTime();
    }

    /// <summary>「2026.11.15」→ 本地时间；<paramref name="endOfDay"/> = 当天 23:59:59.9999999。</summary>
    private static DateTimeOffset? ParseDotDate(string? text, bool endOfDay = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateTime.TryParseExact(text, "yyyy.MM.dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) return null;
        var local = endOfDay
            ? new DateTime(date.Year, date.Month, date.Day, 23, 59, 59, 999).AddTicks(9999)
            : date;
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static string Describe(Exception ex, string what) =>
        ex is TaskCanceledException or OperationCanceledException
            ? $"{what}超时（网络或签名服务无响应）"
            : $"{what}失败：{ex.Message}";
}

// ═══ challengesug DTO（与 DouyinHashtagService 的 DTO 同形；这里自带一份避免跨文件耦合） ═══

file sealed class DouyinChallengeSugResponse
{
    [JsonPropertyName("sug_list")]
    public List<DouyinChallengeSugItem>? SugList { get; set; }

    [JsonPropertyName("status_code")]
    public int? StatusCode { get; set; }
}

file sealed class DouyinChallengeSugItem
{
    [JsonPropertyName("cha_name")]
    public string? ChaName { get; set; }

    [JsonPropertyName("cid")]
    public string? Cid { get; set; }

    [JsonPropertyName("view_count")]
    public long ViewCount { get; set; }

    [JsonPropertyName("group_id")]
    public string? GroupId { get; set; }
}
