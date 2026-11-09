namespace CreatFlow.NewApp.Models;

// ═══════════════════════════════════════════════════════════════════════════
//  发布载荷 —— PublishPlatform.ExtraDataJson 里存的东西（**每个平台一份有类型的 record**）。
//
//  为什么不再用 Dictionary<string, object?>（老版做法）：
//  System.Text.Json 反序列化后所有值都是 JsonElement，`raw is true` 永远不匹配，
//  老版为此专门写了 ParseBool 绕这个坑（PublishPipeline.cs:1089 的注释就是它）。
//  有类型的 record 让「字段名写错」在编译期就暴露，而不是发布时静默变成默认值。
//
//  自包含原则（设计文档 §3.3）：全局声明/可见性在提交时**扇出**到每条平台记录，
//  这样重试单条平台记录时不需要回查草稿 —— 重试是队列页的高频操作，回查会让
//  「草稿已提交即移除」的设计直接失效。
//
//  ⚠️ 字段命名与 SDK 请求一致（PascalCase，序列化走 camelCase），改名即旧队列记录失效。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 各平台载荷的共同部分 —— 都是「提交时算好、执行时直接用」的东西。
/// </summary>
public abstract record PublishPayloadBase
{
    /// <summary>
    /// 标题 —— 全局概念，2026-11-06 从抖音/小红书两份重复字段上提到基类。
    /// 落点：抖音 <c>item_title</c> / 小红书 <c>common.title</c> / 快手并入 <c>caption</c> /
    /// 视频号 <c>shortTitle</c>（另有 20 字截断的 <see cref="WechatVideoPayload.ShortTitle"/>）/
    /// B站 <c>add_v3.title</c> / 头条 <c>PublishVideo.Title</c>。
    /// </summary>
    public string? Title { get; init; }

    /// <summary>该平台最终的正文（**已按平台过滤话题**：无 ID 的话题保留纯文本，见缺陷 L2 修复）</summary>
    public string? Caption { get; init; }

    /// <summary>定时发布时间（null = 立即）</summary>
    public DateTime? ScheduledAt { get; init; }

    /// <summary>内容声明语义 —— 各平台在 <see cref="Services.PublishPipeline"/> 里按能力矩阵映射</summary>
    public DeclarationKind Declaration { get; init; }

    /// <summary>声明的二级补充信息（只有转载 / 自行拍摄有）</summary>
    public ContentDeclaration? DeclarationDetail { get; init; }

    /// <summary>统一可见性 —— 各平台映射到自己的枚举，不支持的回退「公开」</summary>
    public PublishVisibility Visibility { get; init; }
}

/// <summary>抖音载荷 —— <c>create_v2 → item</c>（字段清单见进度表 §1.3 抖音表）。</summary>
public sealed record DouyinPayload : PublishPayloadBase
{
    /// <summary>合集 mix_id</summary>
    public string? MixId { get; init; }

    /// <summary>合集内排序 —— <c>mix.mix_order</c>（2026-11-05 抓包：选合集时页面会发；
    /// 0 = 按发布时间顺序，老样例 83 证明该字段有效）</summary>
    public int? CollectionOrder { get; init; }

    /// <summary>位置 POI（抖音要求 ID 与名称同时非空，否则整个位置不发 —— 缺陷 L10）</summary>
    public PoiRef? Location { get; init; }

    /// <summary>是否允许下载 —— 只有真正有该开关的平台才持有（视频号请求体没有这个字段）</summary>
    public bool AllowDownload { get; init; } = true;

    /// <summary>热点词（与声明正交，走 item.common）</summary>
    public string? HotSentence { get; init; }

    /// <summary>话题的 API 形态（text_extra JSON 串 —— **带真实偏移量**，见 PublishPayloadBuilder.BuildDouyinTopics）</summary>
    public string? TextExtra { get; init; }
    /// <summary>话题的 challenges JSON 串</summary>
    public string? Challenges { get; init; }

    // ── 封面（1.0 封面矩阵 / 2.5.1）──
    /// <summary>封面帧（毫秒）—— <c>cover.poster_delay</c></summary>
    public long? CoverFrameMs { get; init; }
    /// <summary>自定义封面裁剪宽 —— <c>cover.custom_cover_image_width</c></summary>
    public int? CoverCropWidth { get; init; }
    /// <summary>自定义封面裁剪高 —— <c>cover.custom_cover_image_height</c></summary>
    public int? CoverCropHeight { get; init; }
    /// <summary>封面工具信息（原始 JSON 串）—— <c>cover.cover_tools_info</c></summary>
    public string? CoverToolsInfo { get; init; }
    /// <summary>封面工具扩展信息（原始 JSON 串）—— <c>cover.cover_tools_extend_info</c></summary>
    public string? CoverToolsExtendInfo { get; init; }

    // ── 长尾（1.3 抖音表）──
    /// <summary>@ 用户（原始 JSON 数组串）—— <c>common.mentions</c></summary>
    public string? Mentions { get; init; }
    /// <summary>互动贴纸（原始 JSON 数组串）—— <c>common.interaction_stickers</c></summary>
    public string? InteractionStickers { get; init; }
    /// <summary>背景音乐 ID —— <c>common.music_id</c>（<c>music_source</c> 由执行端按有无 ID 定）</summary>
    public string? MusicId { get; init; }
    /// <summary>章节 —— <c>chapter.chapter</c>（wire 是 JSON 串，执行时序列化）</summary>
    public List<PlatformChapter>? Chapters { get; init; }
    /// <summary>官方活动 ID —— <c>common.activity</c>（wire 是 **JSON 字符串数组**，如 <c>"[7702...]"</c>）</summary>
    public List<string>? ActivityIds { get; init; }
    /// <summary>同步目标 —— <c>sync.should_sync / sync_to_toutiao</c></summary>
    public List<string>? SyncTargets { get; init; }
}

/// <summary>快手载荷 —— <c>submit</c>（字段清单见进度表 §1.3 快手表）。</summary>
public sealed record KuaishouPayload : PublishPayloadBase
{
    /// <summary>合集 ID</summary>
    public long? CollectionId { get; init; }
    /// <summary>合集内排序 —— <c>photoIdList</c> 顺序（发布后补加/排序用）</summary>
    public int? CollectionOrder { get; init; }

    /// <summary>是否允许下载（<c>downloadType</c>：1 = 允许 / 2 = 禁止）</summary>
    public bool AllowDownload { get; init; } = true;

    /// <summary>位置（快手的经纬度与 poiId 是三个平级字段，只有它能用经纬度绕过 POI ID）</summary>
    public PoiRef? Location { get; init; }

    /// <summary>同城可见 —— <c>disableNearbyShow</c> 取反</summary>
    public bool ShowInNearby { get; init; } = true;
    /// <summary>允许同框 —— <c>allowSameFrame</c></summary>
    public bool AllowSameFrame { get; init; } = true;
    /// <summary>通知粉丝 —— <c>notifyResult</c>（1 = 通知 / 0 = 不通知）</summary>
    public bool NotifyFollowers { get; init; } = true;

    /// <summary>推荐标签 ID（平台侧标签库命中项）</summary>
    public List<long>? RecTagIds { get; init; }

    // ── 封面（1.0 封面矩阵 / 2.5.2）──
    /// <summary>封面帧（毫秒）—— <c>coverTimeStamp</c></summary>
    public long? CoverFrameMs { get; init; }
    /// <summary>封面文字 —— <c>coverTitle</c></summary>
    public string? CoverTitle { get; init; }
    /// <summary>封面文字样式 —— <c>coverTitleStyle</c></summary>
    public string? CoverTitleStyle { get; init; }
    /// <summary>PK 封面 key —— <c>pkCoverKey</c></summary>
    public string? PkCoverKey { get; init; }
    /// <summary>PK 封面帧（毫秒；-1 = 未设置）—— <c>pkCoverTimeStamp</c></summary>
    public long? PkCoverTimeStamp { get; init; }
    /// <summary>PK 封面类型 —— <c>pkCoverType</c></summary>
    public int? PkCoverType { get; init; }
    /// <summary>PK 封面尺寸 —— <c>pkCoverSize</c></summary>
    public string? PkCoverSize { get; init; }
    /// <summary>个人主页封面 key —— <c>profileCoverKey</c></summary>
    public string? ProfileCoverKey { get; init; }

    // ── 长尾（1.3 快手表）──
    /// <summary>画质增强 —— <c>riseQuality</c></summary>
    public bool? RiseQuality { get; init; }
    /// <summary>AI 字幕 —— <c>useAiCaption</c></summary>
    public bool? UseAiCaption { get; init; }
    /// <summary>AI 字幕封面 —— <c>useAiCaptionCover</c></summary>
    public bool? UseAiCaptionCover { get; init; }
    /// <summary>AI 封面 —— <c>useAiCover</c></summary>
    public bool? UseAiCover { get; init; }
    /// <summary>智能时间 —— <c>isUseIdealTime</c></summary>
    public bool? UseIdealTime { get; init; }
    /// <summary>禁止二创 —— <c>disallowRecreation</c></summary>
    public bool? DisallowRecreation { get; init; }
    /// <summary>专属领域 —— <c>domain</c></summary>
    public string? Domain { get; init; }
    /// <summary>二级领域 —— <c>secondDomain</c></summary>
    public string? SecondDomain { get; init; }
    /// <summary>短剧 ID —— <c>movieId</c></summary>
    public string? MovieId { get; init; }
    /// <summary>项目 ID —— <c>projectId</c></summary>
    public string? ProjectId { get; init; }

    /// <summary>章节（**原始 JSON 数组串**）—— <c>chapters</c>；元素结构未抓到（2026-11-05 恒为 []），先透传</summary>
    public string? ChaptersJson { get; init; }
    /// <summary>共创用户（**原始 JSON 数组串**）—— <c>coPublishUser</c>；元素结构未抓到，先透传</summary>
    public string? CoPublishUsersJson { get; init; }
    /// <summary>共创角色 —— <c>coPublishRole</c>（抓包默认 0）</summary>
    public int? CoPublishRole { get; init; }
    /// <summary>官方活动 ID —— <c>activityIds</c>（数组；数字 ID 序列化成 JSON 数字）</summary>
    public List<string>? ActivityIds { get; init; }
}

/// <summary>小红书载荷 —— <c>note/update</c>（字段清单见进度表 §1.3 小红书表）。</summary>
public sealed record XhsPayload : PublishPayloadBase
{
    /// <summary>合集 ID —— <c>business_binds.noteCollectionBind.id</c></summary>
    public string? CollectionId { get; init; }
    /// <summary>合集内排序 —— <c>add/sort</c>（发布后）</summary>
    public int? CollectionOrder { get; init; }

    /// <summary>位置 POI</summary>
    public PoiRef? Location { get; init; }

    /// <summary>是否允许下载 —— 映射 <c>noteCopyBind.copyable</c>（2026-11-06 起下放到本载荷）</summary>
    public bool AllowDownload { get; init; } = true;

    /// <summary>原创声明（与声明语义 origin **正交可共存**）</summary>
    public bool IsOriginal { get; init; }

    /// <summary>「只给谁看 / 不给谁看」的用户 ID 白名单 —— **必须与可见性一起映射**，见缺陷 L11</summary>
    public List<string>? PrivacyUserIds { get; init; }

    /// <summary>话题（含平台侧 id+name）</summary>
    public List<XhsTopicRef>? Topics { get; init; }

    // ── 封面（1.0 封面矩阵 / 2.5.3）──
    /// <summary>封面帧（毫秒）—— <c>video_info.cover.frame.ts</c>（wire 是秒，执行时换算）</summary>
    public long? CoverFrameMs { get; init; }

    // ── 长尾（1.3 小红书表）──
    /// <summary>视频摘要 —— <c>video_info.summary</c></summary>
    public string? Summary { get; init; }
    /// <summary>允许评论 —— <c>interactionPermissionBind.commentPermission</c></summary>
    public bool? AllowComment { get; init; }
    /// <summary>章节 —— <c>video_info.chapters</c></summary>
    public List<PlatformChapter>? Chapters { get; init; }
    /// <summary>带货商品 ID —— <c>goods_info</c> 的简单形态；复杂结构走 <see cref="GoodsInfo"/></summary>
    public List<string>? ProductIds { get; init; }

    /// <summary>@ 用户（原始 JSON 数组串）—— <c>common.ats</c></summary>
    public string? Ats { get; init; }
    /// <summary>带货商品信息（原始 JSON 对象串）—— <c>common.goods_info</c></summary>
    public string? GoodsInfo { get; init; }
    /// <summary>业务绑定（原始 JSON 数组串）—— <c>common.biz_relations</c></summary>
    public string? BizRelations { get; init; }
    /// <summary>速览合集 ID —— <c>business_binds.noteSketchCollectionBind.id</c></summary>
    public string? SketchCollectionId { get; init; }
    /// <summary>订单绑定（原始 JSON 对象串）—— <c>business_binds.noteOrderBind</c></summary>
    public string? OrderBind { get; init; }
    /// <summary>章节文字同步 —— <c>video_info.chapter_sync_text</c></summary>
    public bool? ChapterSyncText { get; init; }
    /// <summary>PK 封面业务关联（原始 JSON 数组串）—— <c>video_info.pk_cover_biz_relations</c></summary>
    public string? PkCoverBizRelations { get; init; }
}

/// <summary>小红书话题引用（平台侧 ID + 名称）</summary>
public sealed record XhsTopicRef(string Id, string Name, string Type = "topic");

/// <summary>
/// 视频号载荷。
///
/// ⚠️ 这里的 <see cref="TagType"/> / <see cref="TagKey"/> 是**运行时解析结果**：
/// 视频号的声明源是账号的动态标签列表（<c>finder_get_object_tag_list</c>），
/// tagKey 还是会话级 token —— 每会话签发，必须与 tagType 配对透传（设计文档 §4.3）。
/// </summary>
public sealed record WechatVideoPayload : PublishPayloadBase
{
    /// <summary>短标题（视频号标题映射到 ShortTitle，**上限 20 字符**）</summary>
    public string? ShortTitle { get; init; }

    /// <summary>合集 ID</summary>
    public string? CollectionId { get; init; }
    public string? CollectionName { get; init; }

    /// <summary>位置</summary>
    public PoiRef? Location { get; init; }

    /// <summary>内容标注 tagType（0=无需标注 / 1=AI / 2=营销 / 3=虚构 / 5=自行拍摄 / 7=转载 / 8=个人观点）</summary>
    public int? TagType { get; init; }
    /// <summary>标注 key（会话级 token，与 tagType 配对）</summary>
    public string? TagKey { get; init; }

    /// <summary>
    /// 话题名列表 —— 视频号**纯文本**，不经 API 匹配，直接靠正文里的 #（设计文档 §4.4 铁律三）。
    /// 2.6.5 起 = 草稿统一话题列表 ∪ 平台卡话题（后者以前只进 TopicRefs，视频号收不到）。
    /// </summary>
    public List<string>? Topics { get; init; }

    /// <summary>合集内排序 —— <c>mod_collection_feed</c>（发布后调）</summary>
    public int? CollectionOrder { get; init; }

    /// <summary>扩展链接 —— <c>objectDesc.extReading.link</c></summary>
    public string? ExternalLink { get; init; }

    /// <summary>链接卡片标题 —— <c>objectDesc.extReading.title</c></summary>
    public string? ExternalLinkTitle { get; init; }

    /// <summary>链接类型 —— <c>objectDesc.extReading.type</c>（1 公众号 / 2 游戏 / 3 红包）</summary>
    public int ExternalLinkType { get; init; } = 1;

    /// <summary>媒体卡片样式 —— <c>objectDesc.media[].cardShowStyle</c>（抓包恒 2）</summary>
    public int? CardShowStyle { get; init; }

    /// <summary>
    /// 封面帧（毫秒）—— 视频号没有「封面帧」接口，这个值只用于**自动抽帧**
    /// （2.5.7 统一封面：没选封面图时按它抽一帧当封面；留空 = 0.5 秒）。
    /// </summary>
    public long? CoverFrameMs { get; init; }
}

/// <summary>
/// B站载荷 —— <c>add/v3</c>（字段清单见进度表 §1.3 B站表）。
///
/// ⚠️ 1.7 只组装、不执行（发布执行在阶段 4）：
///   · 定时发布 = `add/v3.dtime`（Unix 秒）。2026-11-04 抓包时定时开关是关的（请求体没有该键），
///     但平台模型里 `<c>archive.dtime</c>` 一直存在；2026-11-07 用户实测页面有定时开关
///     （5 分钟 ~ 15 天）。执行时由基类 `ScheduledAt` 转 Unix 秒下发，立即发布不发该字段；
///   · `is_only_self` 由基类 <see cref="PublishPayloadBase.Visibility"/> 在执行时经
///     <see cref="Services.VisibilityMapping.Bilibili"/> 派生（**不在载荷里另存一份**，
///     避免语义值与 wire 值各说各话）；
///   · 封面 `cover/cover43` 是上传返回的 URL —— 运行时产物，不进载荷（执行时上传）。
/// </summary>
public sealed record BilibiliPayload : PublishPayloadBase
{
    /// <summary>
    /// 封面帧（毫秒）—— B站没有「封面帧」接口，这个值只用于**自动抽帧**
    /// （2.5.7 统一封面：没选封面图时按它抽一帧，再交给 16:9 / 4:3 裁剪；留空 = 0.5 秒）。
    /// </summary>
    public long? CoverFrameMs { get; init; }

    /// <summary>标签 —— <c>add_v3.tag</c>（逗号分隔，1~10 个；**标签 ≠ 话题**）</summary>
    public List<string>? Tags { get; init; }

    /// <summary>
    /// 结构化话题 —— <c>add_v3.topic_id</c>（2.6.5 落点）。
    /// B站的话题与标签是两套：<see cref="Tags"/> 走 <c>tag</c>，话题走 <c>topic_id</c>；
    /// 活动挂在话题的 <c>mission_id</c> 上，所以引用里同时带任务号。
    /// ⚠️ 已抓到的 add_v3 样本没有话题字段（提交时页面没选话题），
    /// wire 名与「只发第一个还是数组」由阶段 4 真账号探针定案。
    /// </summary>
    public List<BilibiliTopicRef>? Topics { get; init; }

    /// <summary>
    /// 参与的官方活动任务号 —— <c>mission_id</c>。来源 = 话题引用自带的任务号 +
    /// 已选活动的 ID（B站活动在 <c>TopicServiceCatalog</c> 里就是从话题 <c>mission_id</c> 聚合的）。
    /// </summary>
    public List<long>? MissionIds { get; init; }

    /// <summary>一级分区 —— <c>tid</c>（默认 21 家装房产）</summary>
    public int Tid { get; init; } = 21;
    /// <summary>二级分区 —— <c>human_type2</c>（默认 1015 家装房产）</summary>
    public int HumanType2 { get; init; } = 1015;

    /// <summary>版权 —— <c>copyright</c>（转载 2 / 自制 1 / 抓包默认 3，含义待实账号探针确认）</summary>
    public int Copyright { get; init; } = 3;

    /// <summary>创作声明 —— <c>creation_statement.id</c>（0 无需标注 / 1 AI / 2 虚构 / 3 营销）</summary>
    public int? CreationStatementId { get; init; }

    /// <summary>动态文案 —— <c>dynamic</c>（抓包默认空串）</summary>
    public string? Dynamic { get; init; }

    /// <summary>禁止转载 —— <c>no_reprint</c>（wire 0/1）</summary>
    public bool NoReprint { get; init; }

    /// <summary>水印 —— <c>watermark.state</c>（wire 1/0；页面默认开）</summary>
    public bool Watermark { get; init; } = true;

    /// <summary>开启字幕 —— <c>subtitle.open</c>（wire 0/1）</summary>
    public bool SubtitleOpen { get; init; }
    /// <summary>字幕语言 —— <c>subtitle.lan</c>（抓包默认空串）</summary>
    public string? SubtitleLan { get; init; }

    /// <summary>
    /// 允许评论 —— 执行时取反成 <c>up_close_reply</c>（**B站是「关闭」语义，与统一语义相反**）。
    /// null = 沿用页面默认（允许）。
    /// </summary>
    public bool? AllowComment { get; init; }
    /// <summary>允许弹幕 —— 执行时取反成 <c>up_close_danmu</c>。null = 沿用页面默认（允许）。</summary>
    public bool? AllowDanmaku { get; init; }
    /// <summary>精选评论 —— <c>up_selection_reply</c></summary>
    public bool UpSelectionReply { get; init; }

    /// <summary>预约 —— <c>act_reserve_create</c>（wire 0/1）</summary>
    public bool ActReserve { get; init; }
    /// <summary>预约标题 —— <c>act_reserve_create_title</c></summary>
    public string? ActReserveTitle { get; init; }

    /// <summary>AI 封面 —— <c>ai_cover</c></summary>
    public int? AiCover { get; init; }
    /// <summary>AB 封面 —— <c>is_ab_cover</c></summary>
    public int? IsAbCover { get; init; }
    /// <summary>AB 封面信息（原始 JSON 串）—— <c>ab_cover_info</c></summary>
    public string? AbCoverInfo { get; init; }

    /// <summary>互动视频 —— <c>interactive</c>（wire 0/1）</summary>
    public bool Interactive { get; init; }
    /// <summary>空间隐藏 —— <c>space_hidden</c>（wire 整数，抓包默认 2）</summary>
    public int? SpaceHidden { get; init; }
    /// <summary>杜比音效 —— <c>dolby</c>（wire 0/1）</summary>
    public bool Dolby { get; init; }
    /// <summary>无损音乐 —— <c>lossless_music</c>（wire 0/1）</summary>
    public bool LosslessMusic { get; init; }

    /// <summary>原创声明 —— 与 <see cref="Copyright"/> 同源（自制 / 转载 / 未声明三态）</summary>
    public bool IsOriginal { get; init; }
}

/// <summary>B站话题引用（<c>topic_id</c> + 名称 + 活动任务号）。</summary>
public sealed record BilibiliTopicRef(string Id, string Name, long MissionId = 0);

/// <summary>
/// 头条载荷 —— <c>PublishVideo</c>（字段清单见进度表 §1.3 头条表）。
///
/// ⚠️ 1.7 只组装、不执行（发布执行在阶段 5）：
///   · `VideoType` 在**提交时**按视频宽高算好（3 横版 / 6 竖版）—— 执行时拿不到视频元数据；
///     竖版没有简介（`Abstract`）与定时（`TimerTime`），这里直接置空；
///   · `HideInfo.HideType` 由基类 <see cref="PublishPayloadBase.Visibility"/> 在执行时经
///     <see cref="Services.VisibilityMapping.Toutiao"/> 派生；
///   · 作品声明提交字段名未抓到 → 载荷仍带语义，**v1 不下发**（预检在阶段 5 提示）。
/// </summary>
public sealed record ToutiaoPayload : PublishPayloadBase
{
    /// <summary>
    /// 封面帧（毫秒）—— 头条封面**必填**（TOS → <c>VideoInfo.ThumbUri</c>），
    /// 没选封面图时按它自动抽一帧当封面（2.5.7；留空 = 0.5 秒）。
    /// </summary>
    public long? CoverFrameMs { get; init; }

    /// <summary>3 = 横版 / 6 = 竖版（提交时按视频宽高算好）</summary>
    public int VideoType { get; init; } = 3;

    /// <summary>原创 —— <c>ClaimOrigin</c></summary>
    public bool IsOriginal { get; init; }

    /// <summary>赞赏 —— <c>Praise</c></summary>
    public bool Praise { get; init; }

    /// <summary>视频合集 —— <c>PrependPSeriesId</c>（仅横版；竖版执行端不发送）</summary>
    public string? CollectionId { get; init; }

    /// <summary>
    /// 话题名（纯文本兜底）—— 来源 = 草稿统一话题列表 ∪ 平台卡结构化话题的名字；
    /// 正文里的 <c>#话题</c> 由 builder 追加（<c>Abstract</c> 仅横版）。
    /// </summary>
    public List<string>? Topics { get; init; }

    /// <summary>
    /// 结构化话题 —— <c>HashtagList</c>（2.6.5 落点）。
    /// 元素形状取自视频详情回读：<c>{"HashtagId":"7425…","HashtagName":"作品同步头条赢激励"}</c>
    /// （**ID 是字符串**，见 <c>docs/toutiao/20261105/响应结构.md</c> §4.1）。
    /// ⚠️ PublishVideo 抓包样本里没有该字段（提交时没选话题），阶段 5 探针确认；
    /// 当前兜底是正文里的 <c>#话题</c> 纯文本。
    /// </summary>
    public List<ToutiaoHashtagRef>? Hashtags { get; init; }

    /// <summary>扩展链接 —— <c>ExternalLink</c></summary>
    public string? ExternalLink { get; init; }

    /// <summary>同步目标 —— <c>SyncPlatform</c>（对象；与抖音 sync 方向不同，由执行端解释）</summary>
    public List<string>? SyncTargets { get; init; }

    /// <summary>标签 —— <c>Label</c>（数组）</summary>
    public List<string>? Tags { get; init; }

    /// <summary>封面标题 —— <c>CoverTitles</c>（数组；单个标题执行端包一层）</summary>
    public string? CoverTitle { get; init; }

    /// <summary>模板名 —— <c>TemplateName</c></summary>
    public string? TemplateName { get; init; }
    /// <summary>滤镜名 —— <c>FilterName</c></summary>
    public string? FilterName { get; init; }

    /// <summary>商品（原始 JSON 数组串）—— <c>Commodity</c></summary>
    public string? Commodity { get; init; }
    /// <summary>懂车帝视频结构（原始 JSON 对象串）—— <c>CompassVideo</c></summary>
    public string? CompassVideo { get; init; }
    /// <summary>长尾属性（原始 JSON 对象串）—— <c>AttrsValueMap</c></summary>
    public string? AttrsValueMap { get; init; }
    /// <summary>埋点扩展（原始 JSON 对象串）—— <c>etData</c></summary>
    public string? EtData { get; init; }
}

/// <summary>头条话题引用 —— 对应 <c>HashtagList</c> 元素（<c>HashtagId</c> 是字符串）。</summary>
public sealed record ToutiaoHashtagRef(string Id, string Name);

/// <summary>
/// 载荷读写 —— 按平台键选类型。<c>ExtraDataJson</c> 里存的就是对应 record 的 JSON。
/// </summary>
public static class PublishPayload
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T payload) where T : PublishPayloadBase =>
        JsonSerializer.Serialize(payload, Opts);

    /// <summary>
    /// 反序列化。失败返回 null —— 调用方按「该记录没有可用载荷」处理并回报失败，
    /// **不要**退回空对象：空载荷会静默丢掉用户设置的合集/位置/声明，发出去才发现。
    /// </summary>
    public static T? Deserialize<T>(string? json) where T : PublishPayloadBase
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Opts);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 平台载荷解析失败（{Type}）", typeof(T).Name);
            return null;
        }
    }
}
