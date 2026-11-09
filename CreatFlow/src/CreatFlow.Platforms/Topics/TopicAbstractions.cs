namespace CreatFlow.Platforms.Topics;

/// <summary>
/// 话题（推荐 / 搜索）—— 六平台统一抽象。
///
/// 为什么要抽象：2026-11 用户要求「话题不能写死默认值，六平台都要从平台 API 取」。
/// 六个平台的返回结构、签名方式、上下文依赖（B站要 upload_id、头条要 Vid、
/// 快手要 editSessionId）各不相同，但 UI 只关心三件事：
///   1. 能不能搜 / 能不能推荐（<see cref="Capability"/>）；
///   2. 拉一批候选（失败给明确原因，不抛）；
///   3. 每条候选有没有可下发的结构化 ID（<see cref="TopicSuggestion.HasPlatformId"/>）。
///
/// 实现纪律：**永不抛**（用户取消除外）—— 话题是辅助输入，接口失败不能拦住发布；
/// 失败原因写进 <see cref="TopicFetchResult.Error"/>，UI 显示「拉取失败，可重试」。
/// </summary>
public interface ITopicSuggestService
{
    /// <summary>平台键（<see cref="PlatformNames"/>）</summary>
    string PlatformKey { get; }

    /// <summary>本平台支持哪些话题模式</summary>
    TopicCapability Capability { get; }

    /// <summary>按关键词搜索话题（不支持搜索的平台返回明确原因，不抛）</summary>
    Task<TopicFetchResult> SearchAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default);

    /// <summary>
    /// 平台推荐话题（按标题/正文/已上传视频推荐）。
    /// 需要上传上下文的平台（B站 upload_id / 头条 Vid / 快手 editSessionId）在缺失时
    /// 返回「上传后可获取推荐」这类明确原因。
    /// </summary>
    Task<TopicFetchResult> RecommendAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default);
}

/// <summary>
/// 标签推荐（B站 <c>tag/recommend</c>）—— **标签 ≠ 话题**：
/// B站标签是 <c>add_v3.tag</c>（逗号分隔），话题是 <c>topic_id</c>（带 mission_id 时即活动），
/// 两者都要，所以单独一个接口，不塞进 <see cref="ITopicSuggestService"/>。
/// </summary>
public interface ITagSuggestService
{
    string PlatformKey { get; }

    Task<TopicFetchResult> SuggestTagsAsync(
        PlatformCredentials credentials, TopicQuery query, CancellationToken ct = default);
}

/// <summary>
/// 官方活动列表 —— 关联活动增加曝光。
///
/// 支持面（2026-11-06 抓包）：抖音 / 快手 / 小红书 / B站；头条视频无活动接口、
/// 视频号未抓到 → <see cref="Supported"/> = false 且 UI 不显示活动块（不误导用户）。
///
/// 同 <see cref="ITopicSuggestService"/>：永不抛（用户取消除外），失败不阻塞发布。
/// </summary>
public interface IActivityListService
{
    string PlatformKey { get; }

    /// <summary>本平台有没有官方活动接口</summary>
    bool Supported { get; }

    /// <summary>活动分类（抖音 query_tags / 快手 tab）；没有分类的平台返回空表</summary>
    Task<IReadOnlyList<ActivityCategory>> GetCategoriesAsync(
        PlatformCredentials credentials, CancellationToken ct = default);

    /// <summary>拉活动列表（按当前时间本地过滤；已结束的由 <see cref="PlatformActivity.IsEnded"/> 判）</summary>
    Task<ActivityFetchResult> ListAsync(
        PlatformCredentials credentials, ActivityQuery query, CancellationToken ct = default);
}

/// <summary>
/// 六平台话题 / 活动服务目录 —— App 层只认平台键，不认实现类。
///
/// 服务都是无状态的（凭证按调用传入），每次 new 一个即可；缓存策略由上层（2.6.7）负责。
/// </summary>
public static class TopicServiceCatalog
{
    /// <summary>取该平台的话题服务（视频号也有 —— 返回空并带「无接口」原因，UI 据此显示说明）。</summary>
    public static ITopicSuggestService CreateTopicService(string platformKey, PlatformOptions options)
        => platformKey switch
        {
            PlatformNames.Douyin => new DouyinTopicApi(options),
            PlatformNames.Kuaishou => new KuaishouTopicApi(options),
            PlatformNames.Xiaohongshu => new XhsTopicApi(options),
            PlatformNames.Bilibili => new BilibiliTopicApi(options),
            PlatformNames.Toutiao => new ToutiaoTopicApi(options),
            PlatformNames.WechatVideo => new WechatVideoTopicApi(options),
            _ => throw new ArgumentOutOfRangeException(nameof(platformKey), platformKey,
                "未知平台，没有话题服务"),
        };

    /// <summary>取该平台的活动服务（头条/视频号返回 Supported=false 的实现）。</summary>
    public static IActivityListService CreateActivityService(string platformKey, PlatformOptions options)
        => platformKey switch
        {
            PlatformNames.Douyin => new DouyinTopicApi(options),
            PlatformNames.Kuaishou => new KuaishouTopicApi(options),
            PlatformNames.Xiaohongshu => new XhsTopicApi(options),
            PlatformNames.Bilibili => new BilibiliTopicApi(options),
            PlatformNames.Toutiao => new ToutiaoTopicApi(options),
            PlatformNames.WechatVideo => new WechatVideoTopicApi(options),
            _ => throw new ArgumentOutOfRangeException(nameof(platformKey), platformKey,
                "未知平台，没有活动服务"),
        };

    /// <summary>取该平台的标签推荐服务；不支持（null）= 没有这个接口。</summary>
    public static ITagSuggestService? CreateTagService(string platformKey, PlatformOptions options)
        => platformKey switch
        {
            PlatformNames.Bilibili => new BilibiliTopicApi(options),
            _ => null,
        };

    /// <summary>不构造服务也能问能力（UI 装配阶段用）。</summary>
    public static TopicCapability CapabilityOf(string platformKey) => platformKey switch
    {
        PlatformNames.Douyin => DouyinTopicApi.StaticCapability,
        PlatformNames.Kuaishou => KuaishouTopicApi.StaticCapability,
        PlatformNames.Xiaohongshu => XhsTopicApi.StaticCapability,
        PlatformNames.Bilibili => BilibiliTopicApi.StaticCapability,
        PlatformNames.Toutiao => ToutiaoTopicApi.StaticCapability,
        PlatformNames.WechatVideo => WechatVideoTopicApi.StaticCapability,
        _ => new TopicCapability(),
    };

    /// <summary>
    /// 平台是否有**任何**官方活动能力（UI 显示「官方活动」块的条件）。
    /// 头条/视频号 false —— 卡片上不出现活动块，避免误以为漏了。
    /// </summary>
    public static bool SupportsActivities(string platformKey) =>
        CapabilityOf(platformKey).SupportsActivities;
}
