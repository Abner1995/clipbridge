namespace CreatFlow.Platforms.Topics;

/// <summary>
/// 视频号话题 / 活动 —— **没有接口**。
///
/// 2026-11-05 抓包（<c>docs/wechatvideo/20261105/</c>）与历史接口文档里都没有话题列表接口：
/// <c>finder_get_object_tag_list</c> 是「视频标注」（内容声明），不是话题；
/// 发布体 <c>topics[]</c> 与 <c>objectDesc.topic.finderTopicInfo</c> 只能由用户手动输入，
/// 正文里 <c>#话题 </c> **必须空格结尾**才会被解析（页面行为）。
///
/// 本实现按用户要求「返回空并标记『无接口』」—— UI 据此显示说明并只留手动输入，
/// 绝不编造推荐。后续补抓到接口再把 <see cref="StaticCapability"/> 打开。
/// </summary>
public sealed class WechatVideoTopicApi : ITopicSuggestService, IActivityListService
{
    public static readonly TopicCapability StaticCapability = new(
        SupportsSearch: false,
        SupportsRecommend: false,
        SupportsActivities: false,
        NoInterfaceReason: NoInterface);

    /// <summary>UI 统一文案（视频号卡片的话题块写这句）</summary>
    public const string NoInterface =
        "视频号没有话题列表接口：请在正文里直接输入 #话题（结尾加空格），发布后平台会解析";

    private readonly PlatformOptions _options;

    public WechatVideoTopicApi(PlatformOptions options) => _options = options;

    public string PlatformKey => PlatformNames.WechatVideo;

    public TopicCapability Capability => StaticCapability;

    public bool Supported => false;

    public Task<TopicFetchResult> SearchAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
        => Task.FromResult(TopicFetchResult.Fail(NoInterface));

    public Task<TopicFetchResult> RecommendAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default)
        => Task.FromResult(TopicFetchResult.Fail(NoInterface));

    public Task<ActivityFetchResult> ListAsync(
        PlatformCredentials credentials, ActivityQuery query, CancellationToken ct = default)
        => Task.FromResult(ActivityFetchResult.Fail("视频号没有官方活动接口"));

    public Task<IReadOnlyList<ActivityCategory>> GetCategoriesAsync(
        PlatformCredentials credentials, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ActivityCategory>>([]);
}
