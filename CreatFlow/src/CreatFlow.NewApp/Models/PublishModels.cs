namespace CreatFlow.NewApp.Models;

// ═══════════════════════════════════════════════════════════════════════════
//  发布页客户端模型（设计文档 §3.1）
//
//  与 Models/PublishApiModels.cs 的分工：
//    · 本文件 = 客户端「概念」模型 —— 一个概念只存一份，各平台在提交时各自映射；
//    · PublishApiModels = 与服务端交换的 wire DTO，字段形状由服务端契约决定。
//
//  为什么平台专有参数放在 PlatformParams 而不是散在 Draft 上：老版把
//  「位置」实现了 4 份、「合集」4 份、「声明」8 个字段 —— 字段数 ≈ Σ(各平台各自
//  实现的概念)，这才是五十来个字段的真正来源（设计文档 §4.0）。合并的着力点是
//  一个概念只存一份，不是把控件藏起来。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 内容声明语义 —— 四平台加起来只有这 6 个真实语义（设计文档 §4.3 能力矩阵）。
/// 平台缺哪个是**设计约束**：快手无营销、小红书无个人观点、抖音无自行拍摄。
/// </summary>
public enum DeclarationKind
{
    /// <summary>不声明</summary>
    None = 0,
    /// <summary>内容由 AI 生成</summary>
    AiGenerated,
    /// <summary>虚构演绎，仅供娱乐</summary>
    Fiction,
    /// <summary>内容为个人观点</summary>
    PersonalOpinion,
    /// <summary>含营销推广信息</summary>
    Marketing,
    /// <summary>内容为转载</summary>
    Repost,
    /// <summary>内容为自行拍摄</summary>
    SelfShot,
}

/// <summary>
/// 统一可见性 —— 取六平台并集 6 态（设计文档 §4.2 / 头条抓包「粉丝可见」）。
/// 不支持的平台**退回「公开」并在 UI 写明**，不静默忽略。
/// </summary>
public enum PublishVisibility
{
    /// <summary>公开（视频号只有这一态）</summary>
    Public = 0,
    /// <summary>仅自己（抖音/快手/小红书/B站/头条）</summary>
    Private,
    /// <summary>仅互关好友可见（仅小红书）</summary>
    MutualFriends,
    /// <summary>只给谁看 + 白名单（仅小红书）</summary>
    OnlyUsers,
    /// <summary>不给谁看 + 白名单（仅小红书）</summary>
    ExcludeUsers,
    /// <summary>仅粉丝可见（仅头条 HideType=2；**不是**抖音的「好友可见」，也不等于小红书的「互关」）</summary>
    FollowersOnly,
}

/// <summary>
/// 位置引用 —— 一个平台自己的 POI 命中结果。
/// **不做实体对齐**：四个平台是四个独立 POI 库，ID 之间无对应关系（设计文档 §4.4 铁律一）。
/// </summary>
/// <param name="PoiId">平台侧 POI ID（视频号可为空 → 届时发全零对象）</param>
/// <param name="Name">主名称</param>
/// <param name="SubName">副名 / 地址（各平台含义不同，原样透传）</param>
/// <param name="PoiType">平台给的 POI 类型（小红书发布时需要；老版硬编码 12 丢弃真实值，见缺陷 L5）</param>
/// <param name="Longitude">经度（仅快手用经纬度绕过 POI ID）</param>
/// <param name="Latitude">纬度</param>
/// <param name="CityName">城市名（快手 body 必填项，见设计文档 T4）</param>
public sealed record PoiRef(
    string? PoiId,
    string Name,
    string? SubName = null,
    int? PoiType = null,
    string? Longitude = null,
    string? Latitude = null,
    string? CityName = null)
{
    /// <summary>该平台实际能发出去吗 —— 抖音/小红书要求 ID 与名称同时非空（老版任一为空即静默丢弃整个位置，见缺陷 L10）</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(PoiId) && !string.IsNullOrWhiteSpace(Name);
}

/// <summary>拍摄地区（视频号 shootInfo 用整数编码；快手转载地区是另一份**文本**，不走这个类型）</summary>
/// <param name="CountryCode">国家码（视频号 int，如 1017）</param>
/// <param name="ProvinceCode">省码（视频号 int，如 10160）</param>
/// <param name="CityCode">市码（视频号 int，如 100032）</param>
public sealed record RegionRef(
    int? CountryCode = null,
    int? ProvinceCode = null,
    int? CityCode = null);

/// <summary>
/// 内容声明（全局，跨平台映射）。
///
/// ⚠️ 收拢了老版散在四张平台卡里的 8 个字段。二级补充信息**不能合并**（四平台形态
/// 完全不同，见 §4.3.1），但共用交互：**选中的语义决定哪些二级行出现**。
/// </summary>
public sealed class ContentDeclaration
{
    public DeclarationKind Kind { get; set; } = DeclarationKind.None;

    // ── 转载（Repost）二级 ──
    /// <summary>转载来源文本（小红书 repostInfo.source / 视频号 repostSource 选填）</summary>
    public string? RepostSource { get; set; }
    /// <summary>抖音：来源 = 抖音作品 / 站外</summary>
    public bool DouyinRepostFromInside { get; set; }
    /// <summary>抖音：选中的自己已发布作品 item_id（别家没有的独占机制）</summary>
    public string? DouyinRepostItemId { get; set; }
    /// <summary>快手：1=快手 2=其他</summary>
    public int? KuaishouRepostPlatform { get; set; }
    /// <summary>快手：发生地区（省市区文本，非编码）</summary>
    public string? KuaishouRepostLocation { get; set; }
    /// <summary>快手：发生时间（**毫秒**时间戳）</summary>
    public long? KuaishouRepostTimeMs { get; set; }

    // ── 自行拍摄（SelfShot）二级 ──
    /// <summary>视频号/小红书：拍摄时间</summary>
    public DateTime? ShootTime { get; set; }
    /// <summary>视频号：拍摄地区（整数编码，走 shootInfo）</summary>
    public RegionRef? ShootRegion { get; set; }
    /// <summary>小红书：拍摄地点（**独立于「位置」的第二个地点概念**）</summary>
    public PoiRef? ShootPoi { get; set; }

    /// <summary>该语义在 UI 上有没有二级要填（只有转载 / 自行拍摄有）</summary>
    public static bool HasSecondary(DeclarationKind kind) =>
        kind is DeclarationKind.Repost or DeclarationKind.SelfShot;

    public ContentDeclaration Clone() => new()
    {
        Kind = Kind,
        RepostSource = RepostSource,
        DouyinRepostFromInside = DouyinRepostFromInside,
        DouyinRepostItemId = DouyinRepostItemId,
        KuaishouRepostPlatform = KuaishouRepostPlatform,
        KuaishouRepostLocation = KuaishouRepostLocation,
        KuaishouRepostTimeMs = KuaishouRepostTimeMs,
        ShootTime = ShootTime,
        ShootRegion = ShootRegion,
        ShootPoi = ShootPoi,
    };
}

/// <summary>
/// 平台专有参数 —— 也是**账号级预设的形状**（设计文档 §3.1 / §4.5）。
///
/// 预设与覆盖共用同一个类型：预设存一份、每个 target 存一份，
/// 「恢复账号默认」= 把预设整份抄进 target 的参数；有差异即「已覆盖预设」。
/// </summary>
public sealed class PlatformParams
{
    // ── 与前两项同构的联邦概念（全局区那个「位置 / 合集」卡片写的就是它们） ──
    /// <summary>该平台解析出的位置（null = 不带位置）</summary>
    public PoiRef? Location { get; set; }
    /// <summary>该账号上这个逻辑合集的平台侧 ID（null = 不带合集）</summary>
    public string? CollectionId { get; set; }
    public string? CollectionName { get; set; }

    // ── 覆盖项（null = 跟随全局） ──
    public PublishVisibility? Visibility { get; set; }
    public bool? AllowDownload { get; set; }

    // ── 平台长尾（各有默认值，藏在「更多设置」） ──
    /// <summary>快手 · 通知粉丝（<c>notifyResult</c>：1 = 通知 / 0 = 不通知）</summary>
    public bool NotifyFollowers { get; set; } = true;
    /// <summary>快手 · 同城可见</summary>
    public bool ShowInNearby { get; set; } = true;
    /// <summary>快手 · 允许同框</summary>
    public bool AllowSameFrame { get; set; } = true;
    /// <summary>
    /// 账号级原创覆盖（null = 跟随全局 <see cref="PublishDraft.IsOriginal"/>）。
    /// 老预设里是 <c>bool</c>（默认 false），升级后原样读成 true/false 继续生效；
    /// 发布页的开关已上提到全局，这里是兼容旧档的覆盖位。
    /// </summary>
    public bool? IsOriginal { get; set; }
    /// <summary>抖音 · 热点词</summary>
    public string? HotspotSentence { get; set; }

    /// <summary>该账号解析出的话题（联邦解析结果，旧档形状）。
    /// <c>PlatformId</c> 为 null = 该平台没命中 —— **正文里保留纯文本，不删**（缺陷 L2）。
    /// <para>⚠️ 2.6.2 起结构化话题的单一来源是 <see cref="TopicRefs"/>；本列表保留旧档兼容
    /// （旧版本软件仍读它），读档时会把里面的平台 ID 一次性迁进 <see cref="TopicRefs"/>。</para></summary>
    public List<ResolvedTopic> Topics { get; set; } = [];

    /// <summary>
    /// 结构化话题（2.6）—— 用户在该平台选中的话题，带平台侧 ID / 类型 / 活动任务号。
    /// 空表 = 没选结构化话题（旧档由 <see cref="Topics"/> 迁移而来，纯文本话题不进来）。
    /// **这是「每目标一份」的解析缓存，不是全局话题清单** —— 全局清单是草稿上的名字列表
    /// （<see cref="PublishDraft.Topics"/>），两者一个存「选了什么名」、一个存「该平台怎么发」。
    /// </summary>
    public List<TopicRef> TopicRefs { get; set; } = [];

    /// <summary>
    /// 选中的官方活动快照（2.6）—— 与 <see cref="ActivityIds"/> 的分工：
    /// 这里是 UI / params.txt 选中的结构化对象（带名称、截止时间、关联话题），
    /// <see cref="ActivityIds"/> 是 1.4 的原始 ID 透传口（旧 payload 字段）。
    /// **新代码一律写这里**；builder 在没有 <see cref="Activities"/> 时才回退读旧 ID。
    /// </summary>
    public List<ActivityRef> Activities { get; set; } = [];

    // ── 跨平台公共字段（2026-11-06 全量建模，1.4） ──
    //  一个概念只存一份；不是每个平台都支持（能力矩阵见 PublishMapping/预检）：
    //  不支持的平台在 UI/预检里**明说**，不静默。列表字段一律非空（空 = 不设置），
    //  与 Topics 同款 —— 省掉 null 判断，旧 JSON 缺字段时初始化器兜底成空表。

    /// <summary>合集内排序 —— 抖音 <c>mix_order</c> / 快手 <c>photoIdList</c> 顺序 /
    /// 小红书 add-sort / 视频号 <c>mod_collection_feed</c> / 头条 <c>Rank</c></summary>
    public int? CollectionOrder { get; set; }
    /// <summary>允许评论 —— 小红书 <c>interactionPermissionBind.commentPermission</c> /
    /// B站 <c>up_close_reply</c>（取反）；其余平台未抓到写接口</summary>
    public bool? AllowComment { get; set; }
    /// <summary>允许弹幕 —— 仅 B站 <c>up_close_danmu</c>（取反）</summary>
    public bool? AllowDanmaku { get; set; }
    /// <summary>扩展链接 —— 视频号 <c>objectDesc.extReading</c> / 头条 <c>ExternalLink</c></summary>
    public string? ExternalLink { get; set; }
    /// <summary>背景音乐 ID —— 目前只有抖音 <c>music_source/music_id</c> 抓到</summary>
    public string? MusicId { get; set; }
    /// <summary>章节 —— 抖音 <c>chapter</c> / 快手 <c>chapters</c> / 小红书 <c>video_info.chapters</c></summary>
    public List<PlatformChapter> Chapters { get; set; } = [];
    /// <summary>标签 —— B站 <c>tag</c>（逗号串，1~10 个）/ 头条 <c>Label</c>。
    /// ⚠️ B站的**标签 ≠ 话题**：标签是 tag，话题是 topic_id（带 mission_id 时即活动），两者都要</summary>
    public List<string> Tags { get; set; } = [];
    /// <summary>封面标题 —— 快手 <c>coverTitle</c> / 头条 <c>CoverTitles</c></summary>
    public string? CoverTitle { get; set; }
    /// <summary>商品/带货 ID —— 小红书 <c>goods_info</c> / 头条 <c>Commodity</c></summary>
    public List<string> ProductIds { get; set; } = [];
    /// <summary>共同创作者 —— 快手 <c>coPublishUser/coPublishRole</c>；其余平台未抓到</summary>
    public List<CoPublishUser> CoPublishUsers { get; set; } = [];
    /// <summary>官方活动 ID —— 抖音 <c>activity</c>（下发时是 JSON 字符串数组）/
    /// 快手 <c>activityIds</c>；小红书/B站的活动走结构化 <see cref="Activities"/>（2.6）。
    /// 本列表是 1.4 的原始透传口，只为旧档/旧调用方保留 —— **新代码写入 <see cref="Activities"/>**。</summary>
    public List<string> ActivityIds { get; set; } = [];
    /// <summary>赞赏 —— 仅头条 <c>Praise</c></summary>
    public bool? Praise { get; set; }
    /// <summary>视频摘要 —— 仅小红书 <c>video_info.summary</c></summary>
    public string? Summary { get; set; }
    /// <summary>短标题 —— 视频号 <c>objectDesc.shortTitle</c>（与标题分开；&lt;6 字不下发）</summary>
    public string? ShortTitle { get; set; }
    /// <summary>同步目标平台（平台键）—— 抖音 <c>sync</c>（同步头条/西瓜）/ 头条 <c>SyncPlatform</c>；
    /// 两个平台的方向语义不同，由各自的 builder 解释</summary>
    public List<string> SyncTargets { get; set; } = [];

    // ── 平台专有参数（2026-11-06 分组，1.5） ──
    //  只放该平台**独有**、没有跨平台对应概念的长尾；能合并的一律走上面的公共字段。
    //  null = 该平台未设置（旧档缺整组也读成 null），builder/UI 按默认值处理。
    public DouyinExtras? Douyin { get; set; }
    public KuaishouExtras? Kuaishou { get; set; }
    public XhsExtras? Xhs { get; set; }
    public WechatVideoExtras? WechatVideo { get; set; }
    public BilibiliExtras? Bilibili { get; set; }
    public ToutiaoExtras? Toutiao { get; set; }

    // ── 运行时缓存（不是用户输入） ──
    /// <summary>视频号 tagKey —— **会话级 token**，必须与 tagType 配对透传（§4.3）。
    /// tagType 由声明语义现算（<see cref="DeclarationMapping.WechatTagType"/>），不落预设。</summary>
    public string? WechatTagKey { get; set; }

    public PlatformParams Clone() => new()
    {
        Location = Location,
        CollectionId = CollectionId,
        CollectionName = CollectionName,
        Visibility = Visibility,
        AllowDownload = AllowDownload,
        NotifyFollowers = NotifyFollowers,
        ShowInNearby = ShowInNearby,
        AllowSameFrame = AllowSameFrame,
        IsOriginal = IsOriginal,
        HotspotSentence = HotspotSentence,
        Topics = [.. Topics],
        // 结构化话题 / 活动（2.6）—— 元素是不可变 record，列表本身复制一份即可隔离
        TopicRefs = [.. TopicRefs],
        Activities = [.. Activities],
        // 公共字段（1.4）—— 列表必须深拷贝：预设与目标各持一份，改一边不能串另一边
        CollectionOrder = CollectionOrder,
        AllowComment = AllowComment,
        AllowDanmaku = AllowDanmaku,
        ExternalLink = ExternalLink,
        MusicId = MusicId,
        Chapters = [.. Chapters],
        Tags = [.. Tags],
        CoverTitle = CoverTitle,
        ProductIds = [.. ProductIds],
        CoPublishUsers = [.. CoPublishUsers],
        ActivityIds = [.. ActivityIds],
        Praise = Praise,
        Summary = Summary,
        ShortTitle = ShortTitle,
        SyncTargets = [.. SyncTargets],
        // 平台专有分组（1.5）—— null 保持 null，有则深拷贝
        Douyin = Douyin?.Clone(),
        Kuaishou = Kuaishou?.Clone(),
        Xhs = Xhs?.Clone(),
        WechatVideo = WechatVideo?.Clone(),
        Bilibili = Bilibili?.Clone(),
        Toutiao = Toutiao?.Clone(),
        WechatTagKey = WechatTagKey,
    };

    /// <summary>
    /// 旧档一次性迁移（2.6.2）：把旧 <see cref="Topics"/> 里**已解析出平台 ID** 的条目搬进
    /// <see cref="TopicRefs"/>。读档时调用（发布预设 + 未提交草稿两条路径），幂等。
    ///
    /// 为什么不删 <see cref="Topics"/>：旧版本软件读新档时只认它；新 builder 优先读
    /// <see cref="TopicRefs"/>、没有才回退旧列表 —— 两边都不丢。
    /// 只在 <see cref="TopicRefs"/> 为空时迁移：新档一旦有结构化话题就以它为准，
    /// 不能被旧缓存（可能是另一次搜索的残留）盖掉。
    /// </summary>
    public void MigrateLegacyTopics()
    {
        TopicRefs ??= [];
        if (TopicRefs.Count > 0) return;
        if (Topics is not { Count: > 0 }) return;

        TopicRefs = [.. Topics
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => new TopicRef
            {
                Id = t.PlatformId,
                Name = t.Name,
                Source = TopicRefSources.Legacy,
            })];
    }

    /// <summary>按名字 upsert 一个结构化话题（同名只留一份 —— 话题芯片按名字去重）。</summary>
    public void UpsertTopic(TopicRef topic)
    {
        if (string.IsNullOrWhiteSpace(topic.Name)) return;
        var idx = TopicRefs.FindIndex(t => t.Name == topic.Name);
        if (idx >= 0) TopicRefs[idx] = topic;
        else TopicRefs.Add(topic);
    }

    /// <summary>
    /// 选中一个官方活动：替换同 ID 的旧快照，并把它关联的话题一并带进 <see cref="TopicRefs"/>
    /// （「选活动自动带话题」的唯一实现 —— UI 与 params.txt 导入共用）。
    /// </summary>
    public void UpsertActivity(ActivityRef activity)
    {
        if (string.IsNullOrWhiteSpace(activity.Id)) return;
        var idx = Activities.FindIndex(a => a.Id == activity.Id);
        if (idx >= 0) Activities[idx] = activity;
        else Activities.Add(activity);

        foreach (var t in activity.Topics)
            UpsertTopic(t with { Source = TopicRefSources.Activity });
    }

    /// <summary>
    /// 旧档一次性迁移（2.6.4）：把 1.4 的原始 <see cref="ActivityIds"/> 升成
    /// <see cref="ActivityRef"/> 快照（名称先按 ID 显示）。读档时调用，幂等。
    ///
    /// 为什么需要：2.6.4 起发布页把「官方活动 ID」输入框换成了活动卡片列表，
    /// 旧档只在 <see cref="ActivityIds"/> 里存了 ID —— 不迁移的话界面显示「没选活动」，
    /// 实际发布时 builder 又照样带上（最难看懂的一类不一致）。
    /// 只在 <see cref="Activities"/> 为空时迁移：新档一旦有活动就以它为准。
    /// </summary>
    public void MigrateLegacyActivities()
    {
        Activities ??= [];
        if (Activities.Count > 0) return;
        if (ActivityIds is not { Count: > 0 }) return;

        Activities = [.. ActivityIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new ActivityRef { Id = id.Trim(), Name = id.Trim() })];
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  六平台专有参数分组（1.5）
//
//  每条 = 1.3 表里该平台「专有」栏的落点。**只放没有跨平台对应概念的长尾**：
//    · 能合并的（标题/简介/话题/封面/可见性/原创/合集/位置/章节/评论/标签…）在上面的公共字段；
//    · 上传/转码返回的 ID、客户端常量、签名参数一律不进来（§1.4）。
//  `string?` 类型的「原始 JSON 串」是留给复杂结构的透传口：本次没抓到完整结构，
//  原样保存用户/页面给的值，builder 到 2.x 再决定形状 —— 绝不凭猜测建强类型。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>抖音专有参数 —— <c>create_v2 → item</c> 的长尾字段。</summary>
public sealed class DouyinExtras
{
    /// <summary>自定义封面裁剪宽（px）—— <c>cover.custom_cover_image_width</c></summary>
    public int? CoverCropWidth { get; set; }
    /// <summary>自定义封面裁剪高（px）—— <c>cover.custom_cover_image_height</c></summary>
    public int? CoverCropHeight { get; set; }
    /// <summary>封面工具信息（原始 JSON 串）—— <c>cover.cover_tools_info</c>（`cover_type`/`cut_coordinate` 等）</summary>
    public string? CoverToolsInfo { get; set; }
    /// <summary>封面工具扩展信息（原始 JSON 串）—— <c>cover.cover_tools_extend_info</c>（推荐封面列表/裁剪框）</summary>
    public string? CoverToolsExtendInfo { get; set; }
    /// <summary>@ 用户（原始 JSON 数组串）—— <c>common.mentions</c>；抓包里是字符串 <c>"[]"</c></summary>
    public string? Mentions { get; set; }
    /// <summary>互动贴纸（原始 JSON 数组串）—— <c>common.interaction_stickers</c>；抓包里是字符串 <c>"[]"</c></summary>
    public string? InteractionStickers { get; set; }

    public DouyinExtras Clone() => new()
    {
        CoverCropWidth = CoverCropWidth,
        CoverCropHeight = CoverCropHeight,
        CoverToolsInfo = CoverToolsInfo,
        CoverToolsExtendInfo = CoverToolsExtendInfo,
        Mentions = Mentions,
        InteractionStickers = InteractionStickers,
    };
}

/// <summary>快手专有参数 —— <c>submit</c> 的长尾字段。</summary>
public sealed class KuaishouExtras
{
    /// <summary>封面文字样式 —— <c>coverTitleStyle</c>（封面文字本身上面的公共 <see cref="PlatformParams.CoverTitle"/>）</summary>
    public string? CoverTitleStyle { get; set; }
    /// <summary>PK 封面 key —— <c>pkCoverKey</c></summary>
    public string? PkCoverKey { get; set; }
    /// <summary>PK 封面帧（毫秒；-1 = 未设置）—— <c>pkCoverTimeStamp</c></summary>
    public long? PkCoverTimeStamp { get; set; }
    /// <summary>PK 封面类型 —— <c>pkCoverType</c></summary>
    public int? PkCoverType { get; set; }
    /// <summary>PK 封面尺寸 —— <c>pkCoverSize</c></summary>
    public string? PkCoverSize { get; set; }
    /// <summary>个人主页封面 key —— <c>profileCoverKey</c></summary>
    public string? ProfileCoverKey { get; set; }
    /// <summary>画质增强 —— <c>riseQuality</c></summary>
    public bool? RiseQuality { get; set; }
    /// <summary>AI 字幕 —— <c>useAiCaption</c></summary>
    public bool? UseAiCaption { get; set; }
    /// <summary>AI 字幕封面 —— <c>useAiCaptionCover</c></summary>
    public bool? UseAiCaptionCover { get; set; }
    /// <summary>AI 封面 —— <c>useAiCover</c></summary>
    public bool? UseAiCover { get; set; }
    /// <summary>智能时间 —— <c>isUseIdealTime</c></summary>
    public bool? UseIdealTime { get; set; }
    /// <summary>禁止二创 —— <c>disallowRecreation</c></summary>
    public bool? DisallowRecreation { get; set; }
    /// <summary>专属领域 —— <c>domain</c></summary>
    public string? Domain { get; set; }
    /// <summary>二级领域 —— <c>secondDomain</c></summary>
    public string? SecondDomain { get; set; }
    /// <summary>短剧 ID —— <c>movieId</c></summary>
    public string? MovieId { get; set; }
    /// <summary>项目 ID —— <c>projectId</c></summary>
    public string? ProjectId { get; set; }
    /// <summary>
    /// 章节（**原始 JSON 数组串**）—— <c>chapters</c>。
    /// ⚠️ 2026-11-05 抓包里 `chapters` 恒为 `[]`，元素结构没有抓到 —— 先按原始 JSON 透传，
    /// 不猜字段名（猜错会让整个 submit 被拒）；结构确认后收敛成 <see cref="PlatformChapter"/>。
    /// </summary>
    public string? ChaptersJson { get; set; }
    /// <summary>
    /// 共创用户（**原始 JSON 数组串**）—— <c>coPublishUser</c>。
    /// 元素结构未抓到（抓包为空数组），先透传；纯 UI 侧的概念见 <see cref="PlatformParams.CoPublishUsers"/>。
    /// </summary>
    public string? CoPublishUsersJson { get; set; }
    /// <summary>共创角色 —— <c>coPublishRole</c>（抓包默认 0）</summary>
    public int? CoPublishRole { get; set; }

    public KuaishouExtras Clone() => new()
    {
        CoverTitleStyle = CoverTitleStyle,
        PkCoverKey = PkCoverKey,
        PkCoverTimeStamp = PkCoverTimeStamp,
        PkCoverType = PkCoverType,
        PkCoverSize = PkCoverSize,
        ProfileCoverKey = ProfileCoverKey,
        RiseQuality = RiseQuality,
        UseAiCaption = UseAiCaption,
        UseAiCaptionCover = UseAiCaptionCover,
        UseAiCover = UseAiCover,
        UseIdealTime = UseIdealTime,
        DisallowRecreation = DisallowRecreation,
        Domain = Domain,
        SecondDomain = SecondDomain,
        MovieId = MovieId,
        ProjectId = ProjectId,
        ChaptersJson = ChaptersJson,
        CoPublishUsersJson = CoPublishUsersJson,
        CoPublishRole = CoPublishRole,
    };
}

/// <summary>小红书专有参数 —— <c>note/update</c> 的长尾字段。</summary>
public sealed class XhsExtras
{
    /// <summary>@ 用户（原始 JSON 数组串）—— <c>common.ats</c></summary>
    public string? Ats { get; set; }
    /// <summary>带货商品信息（原始 JSON 对象串）—— <c>common.goods_info</c>；
    /// 简单商品 ID 走公共 <see cref="PlatformParams.ProductIds"/></summary>
    public string? GoodsInfo { get; set; }
    /// <summary>业务绑定（原始 JSON 数组串）—— <c>common.biz_relations</c></summary>
    public string? BizRelations { get; set; }
    /// <summary>速览合集 ID —— <c>business_binds.noteSketchCollectionBind.id</c>（与普通合集分开）</summary>
    public string? SketchCollectionId { get; set; }
    /// <summary>订单绑定（原始 JSON 对象串）—— <c>business_binds.noteOrderBind</c></summary>
    public string? OrderBind { get; set; }
    /// <summary>章节文字同步 —— <c>video_info.chapter_sync_text</c></summary>
    public bool? ChapterSyncText { get; set; }
    /// <summary>PK 封面业务关联（原始 JSON 数组串）—— <c>video_info.pk_cover_biz_relations</c></summary>
    public string? PkCoverBizRelations { get; set; }

    public XhsExtras Clone() => new()
    {
        Ats = Ats,
        GoodsInfo = GoodsInfo,
        BizRelations = BizRelations,
        SketchCollectionId = SketchCollectionId,
        OrderBind = OrderBind,
        ChapterSyncText = ChapterSyncText,
        PkCoverBizRelations = PkCoverBizRelations,
    };
}

/// <summary>视频号专有参数 —— <c>post_create</c> 的长尾字段。</summary>
public sealed class WechatVideoExtras
{
    /// <summary>媒体卡片样式 —— <c>objectDesc.media[].cardShowStyle</c>
    /// （页面枚举：0 默认 / 1 平铺 / 2 居中；抓包横版恒 2）</summary>
    public int? CardShowStyle { get; set; }

    /// <summary>链接卡片标题 —— <c>objectDesc.extReading.title</c>（选填；页面输入链接后自动解析）</summary>
    public string? ExtReadingTitle { get; set; }

    /// <summary>链接类型 —— <c>objectDesc.extReading.type</c>（1 公众号 / 2 游戏 / 3 红包）</summary>
    public int? ExtReadingType { get; set; }

    public WechatVideoExtras Clone() => new()
    {
        CardShowStyle = CardShowStyle,
        ExtReadingTitle = ExtReadingTitle,
        ExtReadingType = ExtReadingType,
    };
}

/// <summary>B站专有参数 —— <c>add/v3</c> 的长尾字段。
/// 允许评论/弹幕走公共 <see cref="PlatformParams.AllowComment"/>/<see cref="PlatformParams.AllowDanmaku"/>；
/// 原创/转载由 <c>copyright</c> 按全局原创 + 声明映射，不单独存。</summary>
public sealed class BilibiliExtras
{
    /// <summary>一级分区 —— <c>tid</c>（默认 21 家装房产）</summary>
    public int? Tid { get; set; }
    /// <summary>二级分区 —— <c>human_type2</c>（默认 1015 家装房产）</summary>
    public int? HumanType2 { get; set; }
    /// <summary>禁止转载 —— <c>no_reprint</c>（wire 0/1）</summary>
    public bool? NoReprint { get; set; }
    /// <summary>动态文案 —— <c>dynamic</c></summary>
    public string? Dynamic { get; set; }
    /// <summary>水印 —— <c>watermark.state</c>（wire 1/0）</summary>
    public bool? Watermark { get; set; }
    /// <summary>开启字幕 —— <c>subtitle.open</c>（wire 0/1）</summary>
    public bool? SubtitleOpen { get; set; }
    /// <summary>字幕语言 —— <c>subtitle.lan</c></summary>
    public string? SubtitleLan { get; set; }
    /// <summary>精选评论 —— <c>up_selection_reply</c>（允许评论/弹幕是公共字段）</summary>
    public bool? UpSelectionReply { get; set; }
    /// <summary>预约 —— <c>act_reserve_create</c>（wire 0/1）</summary>
    public bool? ActReserve { get; set; }
    /// <summary>预约标题 —— <c>act_reserve_create_title</c></summary>
    public string? ActReserveTitle { get; set; }
    /// <summary>AI 封面 —— <c>ai_cover</c></summary>
    public int? AiCover { get; set; }
    /// <summary>AB 封面 —— <c>is_ab_cover</c></summary>
    public int? IsAbCover { get; set; }
    /// <summary>AB 封面信息（原始 JSON 串）—— <c>ab_cover_info</c></summary>
    public string? AbCoverInfo { get; set; }
    /// <summary>互动视频 —— <c>interactive</c>（wire 0/1）</summary>
    public bool? Interactive { get; set; }
    /// <summary>空间隐藏 —— <c>space_hidden</c>（wire 整数，样本 2）</summary>
    public int? SpaceHidden { get; set; }
    /// <summary>杜比音效 —— <c>dolby</c>（wire 0/1）</summary>
    public bool? Dolby { get; set; }
    /// <summary>无损音乐 —— <c>lossless_music</c>（wire 0/1）</summary>
    public bool? LosslessMusic { get; set; }

    public BilibiliExtras Clone() => new()
    {
        Tid = Tid,
        HumanType2 = HumanType2,
        NoReprint = NoReprint,
        Dynamic = Dynamic,
        Watermark = Watermark,
        SubtitleOpen = SubtitleOpen,
        SubtitleLan = SubtitleLan,
        UpSelectionReply = UpSelectionReply,
        ActReserve = ActReserve,
        ActReserveTitle = ActReserveTitle,
        AiCover = AiCover,
        IsAbCover = IsAbCover,
        AbCoverInfo = AbCoverInfo,
        Interactive = Interactive,
        SpaceHidden = SpaceHidden,
        Dolby = Dolby,
        LosslessMusic = LosslessMusic,
    };
}

/// <summary>头条专有参数 —— <c>PublishVideo</c> 的长尾字段。
/// 赞赏/同步/扩展链接/标签/封面标题都走公共字段（1.4）；VideoType 按宽高自动，不建模。</summary>
public sealed class ToutiaoExtras
{
    /// <summary>模板名 —— <c>TemplateName</c></summary>
    public string? TemplateName { get; set; }
    /// <summary>滤镜名 —— <c>FilterName</c></summary>
    public string? FilterName { get; set; }
    /// <summary>商品（原始 JSON 数组串）—— <c>Commodity</c></summary>
    public string? Commodity { get; set; }
    /// <summary>懂车帝视频结构（原始 JSON 对象串）—— <c>CompassVideo</c></summary>
    public string? CompassVideo { get; set; }
    /// <summary>长尾属性（原始 JSON 对象串）—— <c>AttrsValueMap</c></summary>
    public string? AttrsValueMap { get; set; }
    /// <summary>埋点扩展（原始 JSON 对象串）—— <c>etData</c></summary>
    public string? EtData { get; set; }

    public ToutiaoExtras Clone() => new()
    {
        TemplateName = TemplateName,
        FilterName = FilterName,
        Commodity = Commodity,
        CompassVideo = CompassVideo,
        AttrsValueMap = AttrsValueMap,
        EtData = EtData,
    };
}

/// <summary>
/// 话题来源（<see cref="TopicRef.Source"/>）—— 只用于 UI 展示与排障，**不参与平台下发**。
/// 值保持英文短词：将来若把预设/草稿搬到服务端，英文值比中文稳定。
/// </summary>
public static class TopicRefSources
{
    /// <summary>编辑器里手动输入（没有平台 ID，正文纯文本）</summary>
    public const string Manual = "manual";
    /// <summary>关键词搜索选中</summary>
    public const string Search = "search";
    /// <summary>平台推荐列表选中</summary>
    public const string Recommend = "recommend";
    /// <summary>选中官方活动时自动带入</summary>
    public const string Activity = "activity";
    /// <summary>params.txt 的「话题ID」指定</summary>
    public const string Params = "params";
    /// <summary>旧档 <see cref="PlatformParams.Topics"/> 一次性迁移而来</summary>
    public const string Legacy = "legacy";
}

/// <summary>
/// 结构化话题（2.6）—— 一个已选话题在**某个平台**的落地形状。
///
/// 与 <see cref="ResolvedTopic"/> 的区别：那个是旧的四平台联邦解析结果（名字 + ID），
/// 这个是六平台话题 API 化后的完整快照（还带类型 / B站活动任务号 / 原始 JSON / 来源）。
/// 发布时**优先下发 <see cref="Id"/>**；为空才按纯文本处理（各平台降级见 2.6.5）。
/// </summary>
public sealed record TopicRef
{
    /// <summary>平台侧话题 ID（抖音 cid / 快手 tag id / 小红书 topic id / B站 topic_id / 头条 hashtag id）。
    /// 空 = 该平台没有可下发的结构化 ID，只能落纯文本。</summary>
    public string? Id { get; init; }

    /// <summary>话题名（不带 #，平台接口原样）</summary>
    public string Name { get; init; } = "";

    /// <summary>平台侧类型（小红书 <c>topic_type_v2</c> 原值等；没有就是 null）</summary>
    public string? Type { get; init; }

    /// <summary>B站活动任务 ID（&gt;0 = 该话题同时参加官方活动；其余平台 0）</summary>
    public long MissionId { get; init; }

    /// <summary>平台条目原样 JSON（排障用；UI 不展示）</summary>
    public string? ExtraJson { get; init; }

    /// <summary>来源（见 <see cref="TopicRefSources"/>；null = 旧档未标注）</summary>
    public string? Source { get; init; }

    /// <summary>
    /// 讨论量 / 播放量展示文案（2.6.3）—— 选自平台接口时把「1.7亿浏览」「480 万」这类
    /// 原样文案存下来给话题芯片看。**纯展示字段，不进任何发布体**；手动输入/旧档为 null。
    /// </summary>
    public string? Metric { get; init; }

    /// <summary>能不能按结构化 ID 下发（false = 只能落纯文本）</summary>
    public bool HasPlatformId => !string.IsNullOrWhiteSpace(Id);

    /// <summary>是不是「带官方活动」的话题（B站 mission_id）</summary>
    public bool IsActivityTopic => MissionId > 0;
}

/// <summary>
/// 选中的官方活动（2.6）—— **快照**，不是实时引用：名称/封面/截止时间一起存，
/// 活动下架后旧草稿仍能显示「你选过这个活动」。
///
/// 发布时各平台落点不同（2.6.5）：抖音 <c>activity</c> JSON 字符串数组 / 快手
/// <c>activityIds</c> / 小红书只下发关联话题 / B站下发话题的 <c>mission_id</c>。
/// <see cref="Topics"/> 是**活动关联话题的快照** —— 选活动时自动并进目标的话题列表，
/// 避免「选了活动却忘带话题」导致实际不参与。
/// </summary>
public sealed record ActivityRef
{
    /// <summary>平台侧活动 ID（抖音 activity_id / 快手 activityId / 小红书 activity_id / B站 mission_id）</summary>
    public string Id { get; init; } = "";

    /// <summary>活动名</summary>
    public string Name { get; init; } = "";

    /// <summary>活动封面 URL</summary>
    public string? Cover { get; init; }

    /// <summary>截止时间（null = 平台没给/未知 —— 不判已结束）</summary>
    public DateTimeOffset? EndTime { get; init; }

    /// <summary>奖励文案（快手 topGain / 抖音「有奖活动」/ 小红书活动名等）</summary>
    public string? Reward { get; init; }

    /// <summary>活动关联话题（选中活动时自动带进话题块）</summary>
    public IReadOnlyList<TopicRef> Topics { get; init; } = [];

    /// <summary>按当前时间判断是否已结束（UI 灰显、不默认选中）</summary>
    public bool IsEnded => EndTime is { } end && end <= DateTimeOffset.Now;
}

/// <summary>
/// 一条已解析的话题（联邦解析结果的一格）。
/// <paramref name="PlatformId"/> 为 null 表示该平台未命中 —— 此时**正文原样保留纯文本**。
/// <para>2.6 起新代码用 <see cref="TopicRef"/>；本类型只为旧档与旧调用方保留。</para>
/// </summary>
public sealed record ResolvedTopic(string Name, string? PlatformId, string? PlatformName = null);

/// <summary>
/// 视频章节（公共概念，1.4）—— 起始毫秒 + 标题。
/// 各平台 wire 形状不同（抖音是 JSON 字符串、快手/小红书是数组），但「第几毫秒 + 叫什么」
/// 这层语义一致；具体序列化在各平台 builder 里做（2.x）。结构待实账号探针确认。
/// </summary>
public sealed record PlatformChapter(long StartMs, string Title);

/// <summary>
/// 共同创作者（公共概念，1.4）—— 快手 <c>coPublishUser/coPublishRole</c> 的落地形状。
/// 其余平台本次未抓到写接口；角色编码由平台侧定义，原样透传。
/// </summary>
public sealed record CoPublishUser(string UserId, int? Role = null, string? Name = null);

/// <summary>
/// 发布目标（平台 × 账号）—— 身份用 Key，其它组件一律以它为主键（同 CollectionCard 惯例）。
/// </summary>
public sealed record PublishTarget(string PlatformKey, Guid SessionId, string AccountLabel)
{
    public string Key => $"{PlatformKey}:{SessionId}";
    /// <summary>主账号标记（零 emoji：品牌紫「主」小标，见 §4.2）</summary>
    public bool IsPrimary { get; init; }
    /// <summary>账号在当前是否可发布（登录态失效 → 队列页横幅 + 暂停）</summary>
    public bool IsLoggedIn { get; init; } = true;

    /// <summary>平台中文名（派生自 PlatformKey，不入库不序列化）</summary>
    public string PlatformDisplayName => PlatformNames.GetDisplayName(PlatformKey);
}

/// <summary>视频元数据 —— ffmpeg 探测，用户不可见（设计文档 §4.1「全自动」层）</summary>
public sealed record VideoMeta(
    string Path,
    long SizeBytes,
    double DurationS,
    int Width,
    int Height,
    string? ThumbPath = null)
{
    /// <summary>
    /// 是否有音轨（ffprobe <c>-select_streams a:0</c> 探测）。
    /// <c>null</c> = 未知（探测失败 / 加这个字段之前写的老缓存）—— 调用方按「不下结论」处理。
    /// 头条审核要求视频必须有音频（2026-11-09 实测：无音轨作品判「缺少音频信息」），
    /// 预检靠它提示。
    /// </summary>
    public bool? HasAudio { get; init; }

    /// <summary>展示用时长 mm:ss</summary>
    public string DurationText => TimeSpan.FromSeconds(DurationS) is var t
        ? $"{(int)t.TotalMinutes:00}:{t.Seconds:00}"
        : "00:00";

    /// <summary>展示用体积</summary>
    public string SizeText => SizeBytes >= 1024L * 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024 / 1024:0.##} GB"
        : $"{SizeBytes / 1024.0 / 1024:0.#} MB";

    /// <summary>卡片副行：`03:24 · 48.2 MB`</summary>
    public string MetaText => $"{DurationText} · {SizeText}";
}

/// <summary>预检问题级别</summary>
public enum PreflightSeverity
{
    /// <summary>仅提示，不阻断</summary>
    Info,
    /// <summary>会导致该平台失败，但不阻断提交（用户可执意）</summary>
    Warning,
    /// <summary>必然失败，提交前必须处理</summary>
    Error,
}

/// <summary>预检问题 —— 平台为 null 表示条目级（所有目标都受影响）</summary>
public sealed record PreflightIssue(
    PreflightSeverity Severity,
    string Message,
    string? PlatformKey = null)
{
    /// <summary>展示底色（Error 红 / Warning 橙 / Info 灰）—— 三档必须能一眼分开：
    /// 把 Warning 画成 Error 会让用户以为整条都发不出去，把 Error 画成 Warning 会让他直接提交然后失败。</summary>
    public string BgHex => Severity switch
    {
        PreflightSeverity.Error => "#FDECEA",
        PreflightSeverity.Warning => "#FFF3E8",
        _ => "#F2F3F5",
    };

    public string TextHex => Severity switch
    {
        PreflightSeverity.Error => "#C62828",
        PreflightSeverity.Warning => "#B45F06",
        _ => "#4E5969",
    };

    /// <summary>前缀标记（零 emoji：用文字而非符号，避免字体缺字变豆腐块）</summary>
    public string Prefix => Severity switch
    {
        PreflightSeverity.Error => "必须处理",
        PreflightSeverity.Warning => "提示",
        _ => "说明",
    };
}

/// <summary>
/// 一条待发布条目（工作台的一行）。
///
/// 全局字段每条一次；目标与覆盖按 target 分别持有（设计文档 §3.1）。
/// </summary>
public sealed class PublishDraft : ObservableObject
{
    public PublishDraft(string videoPath, string? sourceFolder = null)
    {
        VideoPath = videoPath;
        SourceFolder = sourceFolder;
        Title = Path.GetFileNameWithoutExtension(videoPath);
    }

    public string VideoPath { get; }
    public string FileName => Path.GetFileName(VideoPath);
    /// <summary>文件夹导入时非空 —— 「一个文件夹 = 一个发布单元」的锚点（设计文档 D1）</summary>
    public string? SourceFolder { get; }

    private VideoMeta? _meta;
    /// <summary>后台探测，未就绪为 null（UI 显示占位而非假数据）</summary>
    public VideoMeta? Meta
    {
        get => _meta;
        set
        {
            if (SetProperty(ref _meta, value)) OnPropertyChanged(nameof(MetaText));
        }
    }

    /// <summary>卡片副行：`03:24 · 48.2 MB`</summary>
    public string MetaText => _meta is null ? "读取视频信息…" : _meta.MetaText;

    /// <summary>探测出首帧缩略图了吗（没有就画占位三角）</summary>
    public bool HasThumb => _meta?.ThumbPath is { Length: > 0 } p && File.Exists(p);

    // ── 全局字段（每条一次） ──
    private string _title = "";
    public string Title { get => _title; set => SetProperty(ref _title, value); }

    private string _description = "";
    public string Description { get => _description; set => SetProperty(ref _description, value); }

    /// <summary>话题名列表（不含 #）—— 只存**名字**，各平台 ID 由联邦解析器在提交时解析（§4.4）</summary>
    public ObservableCollection<string> Topics { get; } = [];

    private string? _coverPath;
    /// <summary>null = 自动抽首帧（老版的 1×1 绿 JPEG 占位与 `? null : null` 死代码一并删除，见 §4.2）</summary>
    public string? CoverPath { get => _coverPath; set => SetProperty(ref _coverPath, value); }

    private long? _coverFrameMs;
    /// <summary>
    /// 封面帧时间（毫秒；null = 平台默认/首帧，0 = 首帧）。
    /// 落到抖音 <c>cover.poster_delay</c>、快手 <c>coverTimeStamp</c>、小红书 <c>cover.frame.ts</c>
    /// （小红书 wire 是秒，换算在 builder）。封面帧是**内容级**选择，所以放草稿而不是账号预设。
    /// </summary>
    public long? CoverFrameMs { get => _coverFrameMs; set => SetProperty(ref _coverFrameMs, value); }

    private bool _isScheduled;
    public bool IsScheduled { get => _isScheduled; set => SetProperty(ref _isScheduled, value); }

    private DateTime? _scheduledAt;
    public DateTime? ScheduledAt { get => _scheduledAt; set => SetProperty(ref _scheduledAt, value); }

    private PublishVisibility _visibility = PublishVisibility.Public;
    /// <summary>统一五态；各平台在提交时各自映射，不支持的退回「公开」（§4.2）</summary>
    public PublishVisibility Visibility { get => _visibility; set => SetProperty(ref _visibility, value); }

    private bool _allowDownload = true;
    public bool AllowDownload { get => _allowDownload; set => SetProperty(ref _allowDownload, value); }

    private bool _isOriginal;
    /// <summary>
    /// 原创声明（全局）—— 只有小红书/B站/头条有投稿字段；其余平台不映射。
    /// 原创是**内容属性**不是账号属性，所以与可见性/下载并列放在全局区；
    /// 账号预设仍可通过 <see cref="PlatformParams.IsOriginal"/> 覆盖（兼容旧档）。
    /// </summary>
    public bool IsOriginal { get => _isOriginal; set => SetProperty(ref _isOriginal, value); }

    /// <summary>小红书白名单用户 ID —— 「只给谁看 / 不给谁看」时才出现，**紧贴可见性**（§4.2 页级）</summary>
    public ObservableCollection<string> VisibilityUserIds { get; } = [];

    private ContentDeclaration _declaration = new();
    public ContentDeclaration Declaration
    {
        get => _declaration;
        set { if (SetProperty(ref _declaration, value)) OnPropertyChanged(nameof(DeclarationKind)); }
    }

    public DeclarationKind DeclarationKind => _declaration.Kind;

    // ── 目标与覆盖 ──
    public ObservableCollection<PublishTarget> Targets { get; } = [];

    /// <summary>被**取消勾选**的目标 key（平台卡上的勾）。不在集合里 = 启用。
    /// 存「禁用集」而非「启用集」：目标是后加的，新加目标默认就该启用。</summary>
    public HashSet<string> DisabledTargets { get; } = [];

    /// <summary>该目标是否启用（禁用 = 本条不发给它）</summary>
    public bool IsTargetEnabled(PublishTarget target) => !DisabledTargets.Contains(target.Key);

    /// <summary>实际要发布的目标（预检与提交都只看这些）</summary>
    public IEnumerable<PublishTarget> EnabledTargets => Targets.Where(IsTargetEnabled);

    /// <summary>key = <see cref="PublishTarget.Key"/>；缺失 = 该目标全跟随预设</summary>
    public Dictionary<string, PlatformParams> Overrides { get; } = [];

    // ── 预检 ──
    public ObservableCollection<PreflightIssue> Issues { get; } = [];

    private bool _isSelected = true;
    /// <summary>工作台勾选（未勾选不进入提交）</summary>
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    private bool _isExpanded;
    /// <summary>卡片展开（默认折叠，只显示摘要）</summary>
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    private bool _isProbing;
    /// <summary>元数据探测中（卡片显示「读取视频信息…」）</summary>
    public bool IsProbing { get => _isProbing; set => SetProperty(ref _isProbing, value); }

    /// <summary>该条目是否被勾选且无 Error 级问题（底栏「可提交」计数用）</summary>
    public bool IsSubmittable => IsSelected && !Issues.Any(i => i.Severity == PreflightSeverity.Error);

    /// <summary>取该目标的参数；不存在则按预设播种一份（首次访问即固化，便于「恢复默认」比对）</summary>
    public PlatformParams ParamsFor(PublishTarget target, PlatformParams? preset = null)
    {
        if (!Overrides.TryGetValue(target.Key, out var p) || p is null)
        {
            p = preset?.Clone() ?? new PlatformParams();
            Overrides[target.Key] = p;
        }
        return p;
    }
}
