using Serilog;

namespace CreatFlow.Platforms.Topics;

/// <summary>
/// 视频号话题 / 官方活动。
///
/// <para><b>话题：没有接口。</b>2026-11-05 抓包（<c>docs/wechatvideo/20261105/</c>）与历史接口文档里
/// 都没有话题列表接口：<c>finder_get_object_tag_list</c> 是「视频标注」（内容声明），不是话题；
/// 发布体 <c>topics[]</c> 与 <c>objectDesc.topic.finderTopicInfo</c> 只能由用户手动输入，
/// 正文里 <c>#话题 </c> **必须空格结尾**才会被解析（页面行为）。
/// 本实现按用户要求「返回空并标记『无接口』」—— UI 据此显示说明并只留手动输入，
/// 绝不编造推荐。</para>
///
/// <para><b>活动：有搜索接口</b>（2026-11-11 B2 抓包 <c>wire-2026-11-11.md</c> §1）。
/// <c>POST /post/post_search_event</c>（form: <c>{query, lastBuff, requestId}</c>）→
/// <c>data.eventList[]</c>，每条 <c>{eventTopicId, eventName, eventCreatorNickname}</c> ——
/// 这三项就是提交体 <c>objectDesc.event</c> 的原样快照，平台不再按 ID 二次查询。
/// 与抖音/快手按分类浏览不同，视频号是**关键词搜索**：这里用草稿标题当关键词
/// （候选面板没有独立搜索框），标题为空时直接返回明确原因、不编造列表。</para>
/// </summary>
public sealed class WechatVideoTopicApi : ITopicSuggestService, IActivityListService
{
    public static readonly TopicCapability StaticCapability = new(
        SupportsSearch: false,
        SupportsRecommend: false,
        SupportsActivities: true,
        NoInterfaceReason: NoInterface);

    /// <summary>UI 统一文案（视频号卡片的话题块写这句）</summary>
    public const string NoInterface =
        "视频号没有话题列表接口：请在正文里直接输入 #话题（结尾加空格），发布后平台会解析";

    /// <summary>官方活动搜索 —— 发布页「活动」行的候选来源（B2 §1.1）</summary>
    private const string EventSearchPath = "/post/post_search_event";

    private readonly PlatformOptions _options;

    public WechatVideoTopicApi(PlatformOptions options) => _options = options;

    public string PlatformKey => PlatformNames.WechatVideo;

    public TopicCapability Capability => StaticCapability;

    public bool Supported => true;

    // ═══ 话题（无接口）═══

    public Task<TopicFetchResult> SearchAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
        => Task.FromResult(TopicFetchResult.Fail(NoInterface));

    public Task<TopicFetchResult> RecommendAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
        => Task.FromResult(TopicFetchResult.Fail(NoInterface));

    // ═══ 官方活动（post/post_search_event）═══

    public async Task<ActivityFetchResult> ListAsync(
        PlatformCredentials credentials, ActivityQuery query, CancellationToken ct = default)
    {
        // 视频号活动是关键词搜索（不是分类列表）：候选面板没有关键词输入框，
        // 用草稿标题当关键词 —— 空标题不编造列表，直接给可执行的提示。
        var keyword = (query.Title ?? "").Trim();
        if (keyword.Length == 0)
        {
            return ActivityFetchResult.Fail(
                "视频号活动按关键词搜索 —— 先在「标题」里写进要搜的词（如「日常」「宠物」），再拉取");
        }

        var deviceId = credentials.GetExtraString("deviceId");
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return ActivityFetchResult.Fail(
                "视频号凭证缺 deviceId —— 到「平台账号」页打开该账号窗口、点「完成登录」刷新一次凭证");
        }

        try
        {
            using var client = new WechatVideoApiClient(_options, credentials.Cookie, deviceId);
            var resp = await client.PostAsync(EventSearchPath, new Dictionary<string, object?>
            {
                ["query"] = keyword,
                ["lastBuff"] = "",
                ["requestId"] = Guid.NewGuid().ToString(),
            }, ct: ct);

            if (resp.ErrCode != 0)
            {
                return ActivityFetchResult.Fail(
                    $"视频号活动搜索失败（errCode={resp.ErrCode}）：{resp.ErrMsg ?? "平台未给出原因"}");
            }

            return new ActivityFetchResult(ParseEventList(resp.Data));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[WechatVideoTopic] 活动搜索失败 keyword={Keyword}", keyword);
            return ActivityFetchResult.Fail($"视频号活动搜索失败：{ex.Message}");
        }
    }

    public Task<IReadOnlyList<ActivityCategory>> GetCategoriesAsync(
        PlatformCredentials credentials, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ActivityCategory>>([]);

    /// <summary>
    /// <c>data.eventList[]</c> → <see cref="PlatformActivity"/>（纯映射，公开 static 供自检钉字段）。
    /// <c>eventName</c> 为空的活动直接丢弃 —— 提交体三件套缺一项就组不出合法 <c>objectDesc.event</c>。
    /// </summary>
    public static List<PlatformActivity> ParseEventList(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("eventList", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<PlatformActivity>();
        foreach (var el in list.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = Str(el, "eventTopicId");
            var name = Str(el, "eventName");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;

            var creator = Str(el, "eventCreatorNickname");
            items.Add(new PlatformActivity
            {
                Id = id,
                Name = name,
                CreatorNickname = string.IsNullOrWhiteSpace(creator) ? null : creator,
                // 平台条目原样留档（排障用；UI 不展示）
                ExtraJson = el.GetRawText(),
            });
        }
        return items;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim()
            : null;
}
