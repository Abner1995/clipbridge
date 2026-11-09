namespace CreatFlow.Platforms.Topics;

/// <summary>
/// 平台凭证（话题 / 活动接口用）—— 比各平台自己的服务多带一份 <c>extra_data_json</c>：
/// 抖音的 csrfToken、头条的 csrfToken 都住在那份 JSON 里，而话题/活动接口的签名器
/// 在 Platforms 层拿不到 App 层的 CredentialFields。
///
/// Cookie 是必需的；extra 缺失时按空处理（大多数话题接口只吃 Cookie + 请求参数）。
/// </summary>
public sealed class PlatformCredentials
{
    private Dictionary<string, JsonElement>? _extra;

    public PlatformCredentials(string cookie, string? extraDataJson = null)
    {
        Cookie = cookie ?? "";
        ExtraDataJson = extraDataJson;
    }

    public string Cookie { get; }

    public string? ExtraDataJson { get; }

    /// <summary>从 extra_data_json 取一个字符串值（值不是字符串时取它的 JSON 文本；缺失/坏档 → null）。</summary>
    public string? GetExtraString(string name)
    {
        if (string.IsNullOrWhiteSpace(ExtraDataJson)) return null;
        try
        {
            _extra ??= JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ExtraDataJson);
            if (_extra is null || !_extra.TryGetValue(name, out var el)) return null;
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => el.ToString(),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>头条写接口用的 secsdk csrf token（<c>extra_data.csrfToken</c>）。</summary>
    public string? CsrfToken => GetExtraString("csrfToken");
}

/// <summary>
/// 一个平台话题候选。
///
/// <see cref="PlatformId"/> 为空 = 该平台没有可下发的结构化 ID（例如抖音联想样本里
/// <c>cid</c> 就是空串、视频号根本没有 ID）—— 发布时按平台降级为纯文本，不阻塞。
/// </summary>
public sealed record TopicSuggestion
{
    /// <summary>话题名（不带 #；各平台接口原样）</summary>
    public string Name { get; init; } = "";

    /// <summary>平台侧 ID：抖音 cid / 快手 tag id / 小红书 topic id / B站 topic_id / 头条 hashtag id</summary>
    public string? PlatformId { get; init; }

    /// <summary>平台侧类型（小红书 <c>topic_type_v2</c> 原值等；没有就是 null）</summary>
    public string? TopicType { get; init; }

    /// <summary>B站活动任务 ID（&gt;0 = 该话题同时参加官方活动）</summary>
    public long MissionId { get; init; }

    /// <summary>播放/浏览量（抖音 view_count / 快手 viewCount / B站 arc_play_vv / 小红书 view_num）</summary>
    public long ViewCount { get; init; }

    /// <summary>讨论量文案（小红书 <c>discuss_num_desc</c> 原样，如「1.7亿浏览」；其它平台 null）</summary>
    public string? DiscussText { get; init; }

    /// <summary>平台标记的热门话题（小红书 <c>hot_topic</c>）</summary>
    public bool IsHot { get; init; }

    /// <summary>B站 <c>activity_text</c>（空串 = 没有活动文案）</summary>
    public string? ActivityText { get; init; }

    /// <summary>话题简介（B站 topic_description；抖音频道的 group_id 放 <see cref="GroupId"/>）</summary>
    public string? Description { get; init; }

    /// <summary>抖音 <c>group_id</c>（话题详情/聚合用，发布体不需要）</summary>
    public string? GroupId { get; init; }

    /// <summary>平台条目原样 JSON（排障用；UI 不展示）</summary>
    public string? ExtraJson { get; init; }

    /// <summary>能不能按结构化 ID 下发（false = 只能落纯文本）</summary>
    public bool HasPlatformId => !string.IsNullOrWhiteSpace(PlatformId);

    /// <summary>是不是「带官方活动」的话题（B站 mission_id / activity_text）</summary>
    public bool IsActivity => MissionId > 0 || !string.IsNullOrWhiteSpace(ActivityText);
}

/// <summary>话题搜索 / 推荐的输入。</summary>
public sealed class TopicQuery
{
    /// <summary>搜索关键词（不带 #）。推荐接口不需要。</summary>
    public string Keyword { get; init; } = "";

    /// <summary>当前标题（小红书/B站推荐接口的上下文，可空）</summary>
    public string Title { get; init; } = "";

    /// <summary>当前正文（同上，可空）</summary>
    public string Description { get; init; } = "";

    /// <summary>页码（1 起；平台不支持分页时忽略）</summary>
    public int Page { get; init; } = 1;

    /// <summary>一页条数</summary>
    public int Count { get; init; } = 20;

    // ── 平台专属上下文（拿不到就由各实现给出明确文案） ──

    /// <summary>B站 upload_id（preupload 后才有；话题/标签推荐依赖它）</summary>
    public string? UploadId { get; init; }

    /// <summary>B站 track_id（同上）</summary>
    public string? TrackId { get; init; }

    /// <summary>B站一级分区 tid（tag/recommend 的 subtype_id，默认 21）</summary>
    public int BilibiliTid { get; init; } = 21;

    /// <summary>B站二级分区 human_type2（topic/type/v2 的 type_pid，默认 1015）</summary>
    public int BilibiliTypePid { get; init; } = 1015;

    /// <summary>B站标题预检用的文件名（tag/recommend 的 filename，可空）</summary>
    public string? FileName { get; init; }

    /// <summary>B站封面 URL（tag/recommend 的 cover_url，可空）</summary>
    public string? CoverUrl { get; init; }

    /// <summary>头条 Vid（上传完成后才有；hashtag/rec 依赖它）</summary>
    public string? VideoId { get; init; }

    /// <summary>快手 editSessionId（上传完成后才有；recTag 依赖它）</summary>
    public string? EditSessionId { get; init; }
}

/// <summary>话题拉取结果。<see cref="Error"/> 非空 = 接口失败/不可用（Items 仍可能为空）。</summary>
public sealed record TopicFetchResult(IReadOnlyList<TopicSuggestion> Items, string? Error = null)
{
    public static TopicFetchResult Empty { get; } = new([]);

    public static TopicFetchResult Fail(string error) => new([], error);

    public bool Ok => Error is null;
}

/// <summary>
/// 平台话题能力 —— UI 据此决定「搜索框 / 获取平台推荐 / 手动输入」怎么摆，
/// 不再用 if-platform-key 散在 VM 里。
/// </summary>
public sealed record TopicCapability(
    bool SupportsSearch = false,
    bool SupportsRecommend = false,
    bool SupportsActivities = false,
    bool RecommendNeedsUpload = false,
    string? NoInterfaceReason = null)
{
    public bool HasAnyTopicApi => SupportsSearch || SupportsRecommend;
}

/// <summary>
/// 一个官方活动（关联活动增加曝光）。
///
/// 存的是**快照**（名称/封面/截止时间一起存）—— 活动下架后旧草稿仍能显示；
/// 发布时只下发 Id（小红书下发关联话题），不依赖活动接口实时可用。
/// </summary>
public sealed record PlatformActivity
{
    /// <summary>平台侧活动 ID（抖音 activity_id / 快手 activityId / 小红书 activity_id / B站 mission_id）</summary>
    public string Id { get; init; } = "";

    /// <summary>活动名</summary>
    public string Name { get; init; } = "";

    public string? CoverUrl { get; init; }

    /// <summary>奖励文案（快手 topGain / 小红书 activity_reward / 抖音按 reward_type 归一为「有奖活动」）</summary>
    public string? Reward { get; init; }

    public DateTimeOffset? StartTime { get; init; }

    public DateTimeOffset? EndTime { get; init; }

    /// <summary>热度（抖音 hot_score / B站话题播放量取最大值）</summary>
    public long HotScore { get; init; }

    /// <summary>参与人数（快手 number）</summary>
    public long ParticipantCount { get; init; }

    /// <summary>平台自定义标签（快手 customTag）</summary>
    public string? CustomTag { get; init; }

    /// <summary>活动关联话题 —— **选中活动自动带这些话题**（抖音 challenge_ids / 小红书 topic_infos / B站同 mission 的 topics）</summary>
    public IReadOnlyList<TopicSuggestion> Topics { get; init; } = [];

    /// <summary>平台条目原样 JSON（排障用；UI 不展示）</summary>
    public string? ExtraJson { get; init; }

    /// <summary>按当前时间判断是否已结束（过期活动 UI 灰显、不默认选中）</summary>
    public bool IsEnded => EndTime is { } end && end <= DateTimeOffset.Now;
}

/// <summary>活动列表的筛选输入。</summary>
public sealed class ActivityQuery
{
    /// <summary>标题（B站按标题推荐话题，活动从话题聚合；其余平台忽略）</summary>
    public string Title { get; init; } = "";

    /// <summary>B站 upload_id（活动聚合走话题接口；缺省用 0 —— 探针实测可按标题推荐）</summary>
    public string? UploadId { get; init; }

    /// <summary>分类（抖音 query_tag / 快手 tab value；null = 综合）</summary>
    public long? CategoryId { get; init; }

    /// <summary>奖励类型（快手 rewardType；null = 全部）</summary>
    public int? RewardType { get; init; }

    /// <summary>排序（快手 sortType：0 默认 / 1 最新 / 2 最热）</summary>
    public int? SortType { get; init; }

    /// <summary>活动类型（快手 tagType：0 全部 / 1 热点创作 / 2 打卡挑战）</summary>
    public int? TagType { get; init; }

    public int Page { get; init; } = 1;

    public int Count { get; init; } = 50;
}

/// <summary>活动拉取结果。<see cref="Error"/> 非空 = 接口失败/不可用（不阻塞发布）。</summary>
public sealed record ActivityFetchResult(IReadOnlyList<PlatformActivity> Items, string? Error = null)
{
    public static ActivityFetchResult Empty { get; } = new([]);

    public static ActivityFetchResult Fail(string error) => new([], error);

    public bool Ok => Error is null;
}

/// <summary>活动分类（抖音 query_tags / 快手 tab / 快手 filter）。</summary>
public sealed record ActivityCategory(string Id, string Name);
