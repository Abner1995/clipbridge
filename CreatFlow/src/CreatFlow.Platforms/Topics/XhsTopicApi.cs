using Serilog;

namespace CreatFlow.Platforms.Topics;

/// <summary>
/// 小红书话题 / 官方活动（2026-11-05 抓包：<c>docs/xiaohongshu/20261105/capture-2026-11-05T03-40-55.jsonl</c>）。
///
/// 端点：
///   · 推荐 <c>POST /api/galaxy/v2/creator/recommend/suggest/topics</c>
///     body <c>{title, desc, file_ids, topic_round_start_time}</c>
///     → <c>data.topic_dto_list[{id,name,discuss_num,discuss_num_desc,hot_topic,topic_type_v2,view_num}]</c>
///   · 搜索 <c>POST /api/galaxy/v2/creator/servicegw/v2/search/topics</c>
///     body <c>{keyword, topic_round_start_time, content}</c>
///     → <c>data.topics[].topic_info_dto{…}</c>
///   · 活动 <c>GET /api/galaxy/v2/creator/activity_center/list?sort=1&amp;type=1&amp;source=3&amp;topic_activity=0</c>
///     → <c>data.activity_list[{activity_id,activity_name,picture_link,end_time,topic_infos[…]}]</c>
///
/// 签名：走 <see cref="XhsApiClient"/> 的 x-s Handler（App 层 <c>XhsSignerService</c> 页面签名）。
/// 手输 <c>#话题</c> 平台**不上报** <c>hash_tag</c> —— 只有选中这里返回的条目才会带结构化 ID，
/// 所以本服务返回的 <see cref="TopicSuggestion.HasPlatformId"/> 很重要。
///
/// 搜索降级：2026-11-05 抓到的 <c>servicegw/v2</c> 端点失败时，退回生产在用的
/// <see cref="XhsTopicService.SearchAsync"/>（edith 的 <c>/web_api/sns/v1/search/topic</c>，
/// 2026-10-22 起一直可用）—— 两个端点都拿不到才报失败。
/// </summary>
public sealed class XhsTopicApi : ITopicSuggestService, IActivityListService
{
    public static readonly TopicCapability StaticCapability = new(
        SupportsSearch: true,
        SupportsRecommend: true,
        SupportsActivities: true);

    private const string SuggestPath = "/api/galaxy/v2/creator/recommend/suggest/topics";
    private const string SearchPath = "/api/galaxy/v2/creator/servicegw/v2/search/topics";
    private const string ActivityListPath = "/api/galaxy/v2/creator/activity_center/list";

    private readonly PlatformOptions _options;

    public XhsTopicApi(PlatformOptions options) => _options = options;

    public string PlatformKey => PlatformNames.Xiaohongshu;

    public TopicCapability Capability => StaticCapability;

    public bool Supported => true;

    // ═══ 话题 ═══

    public async Task<TopicFetchResult> SearchAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Keyword)) return TopicFetchResult.Empty;

        try
        {
            using var client = new XhsApiClient(credentials.Cookie, _options);
            var body = new Dictionary<string, object?>
            {
                ["keyword"] = query.Keyword,
                ["topic_round_start_time"] = NowMillis(),
                ["content"] = query.Description,
            };
            var resp = await client.PostCreatorRawAsync(SearchPath, body, ct);
            EnsureOk(resp, "小红书话题搜索");

            var items = ParseTopicArray(resp, "data", "topics", "topic_info_dto");
            if (items.Count > 0) return new TopicFetchResult(items);
            return TopicFetchResult.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[XhsTopic] 搜索失败 keyword={Keyword}，退回旧搜索端点", query.Keyword);
            return await FallbackSearchAsync(credentials, query, ex, ct);
        }
    }

    /// <summary>
    /// 降级搜索：生产在用的 edith 端点（<see cref="XhsTopicService.SearchAsync"/>）。
    /// 只有新端点抛异常时才会走到这里（空结果不降级 —— 那是平台真没这个词）。
    /// </summary>
    private async Task<TopicFetchResult> FallbackSearchAsync(
        PlatformCredentials credentials, TopicQuery query, Exception primary, CancellationToken ct)
    {
        try
        {
            var list = await new XhsTopicService(_options)
                .SearchAsync(credentials.Cookie, query.Keyword, query.Title, query.Description,
                    pageSize: Math.Clamp(query.Count, 1, 50), ct: ct);
            if (list.Count > 0)
            {
                return new TopicFetchResult([.. list.Select(x => new TopicSuggestion
                {
                    Name = x.Name,
                    PlatformId = x.Id,
                })]);
            }
            return TopicFetchResult.Fail(Describe(primary, "小红书话题搜索"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[XhsTopic] 降级搜索也失败");
            return TopicFetchResult.Fail(Describe(primary, "小红书话题搜索"));
        }
    }

    public async Task<TopicFetchResult> RecommendAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
    {
        try
        {
            using var client = new XhsApiClient(credentials.Cookie, _options);
            var body = new Dictionary<string, object?>
            {
                ["title"] = query.Title,
                ["desc"] = query.Description,
                ["file_ids"] = Array.Empty<string>(),
                ["topic_round_start_time"] = NowMillis(),
            };
            var resp = await client.PostCreatorRawAsync(SuggestPath, body, ct);
            EnsureOk(resp, "小红书话题推荐");

            return new TopicFetchResult(ParseTopicArray(resp, "data", "topic_dto_list", null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[XhsTopic] 推荐失败");
            return TopicFetchResult.Fail(Describe(ex, "小红书话题推荐"));
        }
    }

    // ═══ 官方活动 ═══

    public async Task<ActivityFetchResult> ListAsync(
        PlatformCredentials credentials, ActivityQuery query, CancellationToken ct = default)
    {
        try
        {
            using var client = new XhsApiClient(credentials.Cookie, _options);
            var resp = await client.GetCreatorRawAsync(ActivityListPath, new Dictionary<string, string>
            {
                ["sort"] = "1",
                ["type"] = "1",
                ["source"] = "3",
                ["topic_activity"] = "0",
            }, ct);
            EnsureOk(resp, "小红书活动列表");

            if (!resp.TryGetProperty("data", out var data)
                || !data.TryGetProperty("activity_list", out var arr)
                || arr.ValueKind != JsonValueKind.Array) return ActivityFetchResult.Empty;

            var list = new List<PlatformActivity>();
            foreach (var a in arr.EnumerateArray())
            {
                var id = Long(a, "activity_id");
                if (id <= 0) continue;

                var topics = new List<TopicSuggestion>();
                if (a.TryGetProperty("topic_infos", out var tarr)
                    && tarr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tarr.EnumerateArray())
                    {
                        var tid = Str(t, "id");
                        var name = Str(t, "name");
                        if (string.IsNullOrWhiteSpace(tid) || string.IsNullOrWhiteSpace(name)) continue;
                        topics.Add(new TopicSuggestion { Name = name, PlatformId = tid });
                    }
                }

                list.Add(new PlatformActivity
                {
                    Id = id.ToString(),
                    Name = Str(a, "activity_name") ?? "",
                    CoverUrl = Str(a, "picture_link"),
                    Reward = Str(a, "activity_reward"),
                    StartTime = FromUnixMs(Long(a, "start_time")),
                    EndTime = FromUnixMs(Long(a, "end_time")),
                    Topics = topics,
                    ExtraJson = JsonSerializer.Serialize(new
                    {
                        activity_status = Int(a, "activity_status"),
                        instance_id = Long(a, "instance_id"),
                        page_id = Str(a, "page_id"),
                        pc_post_link = Str(a, "pc_post_link"),
                    }),
                });
            }
            return new ActivityFetchResult(list);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[XhsTopic] 活动列表失败");
            return ActivityFetchResult.Fail(Describe(ex, "小红书活动列表"));
        }
    }

    /// <summary>小红书活动没有分类接口（只有 sort/type/topic_activity 固定开关）→ 空表。</summary>
    public Task<IReadOnlyList<ActivityCategory>> GetCategoriesAsync(
        PlatformCredentials credentials, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ActivityCategory>>([]);

    // ═══ 解析 ═══

    /// <summary>
    /// 解析话题数组。搜索与推荐的差别只在键名：
    /// 搜索 <c>data.topics[].topic_info_dto</c>，推荐 <c>data.topic_dto_list[]</c>。
    /// </summary>
    public static IReadOnlyList<TopicSuggestion> ParseTopicArray(
        JsonElement resp, string dataKey, string arrayKey, string? wrapperKey)
    {
        if (!resp.TryGetProperty(dataKey, out var data)
            || !data.TryGetProperty(arrayKey, out var arr)
            || arr.ValueKind != JsonValueKind.Array) return [];

        var list = new List<TopicSuggestion>();
        foreach (var raw in arr.EnumerateArray())
        {
            var t = raw;
            if (wrapperKey is not null)
            {
                if (!raw.TryGetProperty(wrapperKey, out t) || t.ValueKind != JsonValueKind.Object)
                    continue;
            }
            else if (t.ValueKind != JsonValueKind.Object) continue;

            var id = Str(t, "id");
            var name = Str(t, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;

            var discuss = Str(t, "discuss_num_desc") ?? Str(t, "discuss_num");
            list.Add(new TopicSuggestion
            {
                Name = name,
                PlatformId = id,
                TopicType = t.TryGetProperty("topic_type_v2", out var tv) ? tv.ToString() : null,
                DiscussText = discuss,
                ViewCount = Long(t, "view_num"),
                IsHot = t.TryGetProperty("hot_topic", out var hot) && hot.ValueKind == JsonValueKind.True,
            });
        }
        return list;
    }

    private static void EnsureOk(JsonElement resp, string what)
    {
        if (resp.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException($"{what}失败：{Str(resp, "msg") ?? "平台返回 success=false"}");
        if (resp.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var c) && c != 0)
            throw new InvalidOperationException($"{what}失败：code={c}，{Str(resp, "msg") ?? "?"}");
    }

    private static long NowMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static DateTimeOffset? FromUnixMs(long ms) =>
        ms <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var l) ? l : 0;

    private static int Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var i) ? i : 0;

    private static string Describe(Exception ex, string what) =>
        ex is TaskCanceledException or OperationCanceledException
            ? $"{what}超时（网络或签名服务无响应）"
            : $"{what}失败：{ex.Message}";
}
