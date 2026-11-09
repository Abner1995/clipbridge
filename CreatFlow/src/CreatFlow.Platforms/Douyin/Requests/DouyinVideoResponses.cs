namespace CreatFlow.Platforms.Douyin.Requests;

// ═══ Video Publish Responses ═══
// 对标 Python fangai/douyin/api.py — 视频 CRUD
// DouyinVideoResponses.cs

/// <summary>
/// 发布视频（create_work）API 响应。
/// 对标: POST /web/api/media/aweme/create_v2/
/// </summary>
public sealed class DouyinCreateWorkResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonPropertyName("item_id")]
    public string? ItemId { get; set; }

    /// <summary>
    /// 失败原因（平台的人话说明）。**必须报出来**：2026-09-20 实测只带 status_code=35 时，
    /// 用户拿到的是一句「status_code=35」，既分不清是内容不合规还是账号被风控，
    /// 也没法判断该改内容还是该养号。
    /// </summary>
    [JsonPropertyName("status_msg")]
    public string? StatusMsg { get; set; }

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// 更新视频 API 响应。
/// 对标: POST /web/api/media/aweme/update_v2/
/// </summary>
public sealed class DouyinUpdateWorkResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonPropertyName("item_id")]
    public string? ItemId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// 删除视频 API 响应。
/// 对标: POST /web/api/media/aweme/delete/
/// </summary>
public sealed class DouyinDeleteWorkResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

// ═══ Video List Response ═══
// 对标: GET /janus/douyin/creator/pc/work_list

/// <summary>
/// 视频列表 API 响应。
/// </summary>
public sealed class DouyinListWorksResponse
{
    [JsonPropertyName("aweme_list")]
    public List<JsonElement>? AwemeList { get; set; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    [JsonPropertyName("max_cursor")]
    public long MaxCursor { get; set; }

    [JsonPropertyName("min_cursor")]
    public long MinCursor { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

// ═══ CreateWork Request Builders ═══

/// <summary>
/// 抖音发布请求的 item 结构（对标 Python create_work 的 item dict）。
///
/// 端点: POST /web/api/media/aweme/create_v2/
/// Body: {"item": item}
///
/// 最小必填: Common.Text, Common.VideoId
/// </summary>
public sealed class DouyinCreateItem
{
    [JsonPropertyName("common")]
    public DouyinCommon Common { get; set; } = new();

    /// <summary>封面图信息（始终序列化 — 对标 Python cover: {}）</summary>
    [JsonPropertyName("cover")]
    public DouyinCoverInfo Cover { get; set; } = new();

    /// <summary>合集信息（始终序列化 — 对标 Python mix: {}）</summary>
    [JsonPropertyName("mix")]
    public DouyinMixAttachment Mix { get; set; } = new();

    /// <summary>精选成员 — 对标浏览器 selected_member: {"is_selected_member_video": false}</summary>
    [JsonPropertyName("selected_member")]
    public DouyinSelectedMemberInfo SelectedMember { get; set; } = new();

    /// <summary>章节信息 — 对标浏览器 chapter: {"chapter": "{...}"}</summary>
    [JsonPropertyName("chapter")]
    public DouyinChapterInfo Chapter { get; set; } = new();

    /// <summary>同步设置（should_sync: true）</summary>
    [JsonPropertyName("sync")]
    public DouyinSyncInfo Sync { get; set; } = new();

    [JsonPropertyName("anchor")]
    public Dictionary<string, object?> Anchor { get; set; } = new();

    [JsonPropertyName("open_platform")]
    public Dictionary<string, object?> OpenPlatform { get; set; } = new();

    [JsonPropertyName("assistant")]
    public DouyinAssistantInfo Assistant { get; set; } = new();

    /// <summary>
    /// 自主声明 — 对标 Python item["declare"]。
    /// 格式: {"user_declare_info": "{\"choose_value\":\"repost\",\"source\":\"站外\"}"}
    /// </summary>
    [JsonPropertyName("declare")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DouyinDeclareInfo? Declare { get; set; }
}

/// <summary>
/// Common 通用字段 — 对标 Python item["common"]。
/// </summary>
public sealed class DouyinCommon
{
    /// <summary>视频标题 + 描述（会同步到 caption、item_title 等）</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary>描述</summary>
    [JsonPropertyName("caption")]
    public string Caption { get; set; } = "";

    /// <summary>标题</summary>
    [JsonPropertyName("item_title")]
    public string ItemTitle { get; set; } = "";

    /// <summary>视频 ID（上传后从 CommitUploadInner 获取的 Vid）</summary>
    [JsonPropertyName("video_id")]
    public string VideoId { get; set; } = "";

    /// <summary>媒体类型：4 = 视频</summary>
    [JsonPropertyName("media_type")]
    public int MediaType { get; set; } = 4;

    /// <summary>
    /// 谁可以看。
    /// 0 = 公开, 10 = 好友可见, 50 = 仅自己可见
    /// </summary>
    [JsonPropertyName("visibility_type")]
    public int VisibilityType { get; set; } = 0;

    /// <summary>
    /// 是否允许下载：0 = 禁止, 1 = 允许
    /// </summary>
    [JsonPropertyName("download")]
    public int Download { get; set; } = 1;

    /// <summary>
    /// 定时发布时间：<b>Unix 秒</b>，0 = 立即发布（2026-11-05 抓包 `timing=1793850900`）。
    /// ⚠️ 不是「0/1 开关」，也没有单独的 timing_time 字段 —— 旧注释写反过。
    /// </summary>
    [JsonPropertyName("timing")]
    public int Timing { get; set; } = 0;

    /// <summary>唯一 creation_id（客户端生成，用于幂等）</summary>
    [JsonPropertyName("creation_id")]
    public string CreationId { get; set; } = "";

    /// <summary>音乐 ID（null = 不选音乐）— 浏览器显式发 null，对齐 2026-08-16 抓包</summary>
    [JsonPropertyName("music_id")]
    public string? MusicId { get; set; }

    /// <summary>音乐来源：0 = 无</summary>
    [JsonPropertyName("music_source")]
    public int MusicSource { get; set; } = 0;

    /// <summary>
    /// 热点来源 — 2026-11-05 抓包实测：未选热点时是 <c>"recommend"</c>
    /// （旧值 <c>"recommend/recommend/recommend"</c> 已过时）；选中热点词时用
    /// <c>"search/search/search"</c>（旧版知识，待实账号复核）。
    /// </summary>
    [JsonPropertyName("hashtag_source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HashtagSource { get; set; }

    /// <summary>选中的热点词（hashtag_source=search/search/search 时必填）— 浏览器始终发 ""，对齐 2026-08-16 抓包</summary>
    [JsonPropertyName("hot_sentence")]
    public string HotSentence { get; set; } = "";

    // ── 以下字段保持空默认值即可 ──

    [JsonPropertyName("activity")]
    public string Activity { get; set; } = "[]";

    [JsonPropertyName("text_extra")]
    public string TextExtra { get; set; } = "[]";

    [JsonPropertyName("challenges")]
    public string Challenges { get; set; } = "[]";

    [JsonPropertyName("mentions")]
    public string Mentions { get; set; } = "[]";

    [JsonPropertyName("interaction_stickers")]
    public string InteractionStickers { get; set; } = "[]";

    /// <summary>
    /// 自主声明 — JSON 对象。
    /// 如 {"fictional_content":false, "dangerous_behavior":false,
    ///      "medical_advice":false, "commercial_promotion":true}
    /// </summary>
    [JsonPropertyName("content_declaration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? ContentDeclaration { get; set; }

}

/// <summary>
/// 封面信息 — 对齐 2026-11-05 浏览器抓包：poster（TOS key）+ poster_delay
/// + 自定义封面的裁剪尺寸。
/// </summary>
public sealed class DouyinCoverInfo
{
    // 注意：页面从不下发 cover.uri —— 2026-11-05 create_v2 与 2026-06 create_media_v2
    // 的封面对象里只有 poster（老代码多发一个 uri 是照错误笔记写的，2.5.1 删掉）。

    /// <summary>封面 poster（TOS key）— 浏览器字段名</summary>
    [JsonPropertyName("poster")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Poster { get; set; }

    /// <summary>
    /// 封面帧延迟 —— <b>秒</b>（旧样例 `poster_delay=4` 对应的 cover_timestamp 是 4000ms）。
    /// 页面始终发 0；我们按「用户选的封面帧毫秒 ÷ 1000」下发。
    /// </summary>
    [JsonPropertyName("poster_delay")]
    public int PosterDelay { get; set; } = 0;

    /// <summary>自定义封面裁剪宽（px）—— <c>custom_cover_image_width</c>（2026-11-05 抓包 1051）</summary>
    [JsonPropertyName("custom_cover_image_width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CustomCoverImageWidth { get; set; }

    /// <summary>自定义封面裁剪高（px）—— <c>custom_cover_image_height</c>（2026-11-05 抓包 788）</summary>
    [JsonPropertyName("custom_cover_image_height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CustomCoverImageHeight { get; set; }

    /// <summary>封面工具信息（原始 JSON 串）—— <c>cover_tools_info</c>（裁剪框/来源等）</summary>
    [JsonPropertyName("cover_tools_info")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoverToolsInfo { get; set; }

    /// <summary>封面工具扩展信息（原始 JSON 串）—— <c>cover_tools_extend_info</c></summary>
    [JsonPropertyName("cover_tools_extend_info")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoverToolsExtendInfo { get; set; }
}

/// <summary>
/// 合集挂载信息 — 对标 Python item["mix"]。
/// </summary>
public sealed class DouyinMixAttachment
{
    /// <summary>合集 mix_id</summary>
    [JsonPropertyName("mix_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MixId { get; set; }

    /// <summary>合集内排序序号</summary>
    [JsonPropertyName("mix_order")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MixOrder { get; set; }
}

/// <summary>
/// 同步设置 — 对齐 2026-08-16 浏览器抓包: should_sync=false + sync_to_toutiao=0
/// （此前 should_sync=true 会触发西瓜/头条同步校验，可能被新校验拒绝）。
/// </summary>
public sealed class DouyinSyncInfo
{
    [JsonPropertyName("should_sync")]
    public bool ShouldSync { get; set; } = false;

    [JsonPropertyName("sync_to_toutiao")]
    public int SyncToToutiao { get; set; } = 0;
}

/// <summary>
/// 发布助手信息。
/// </summary>
public sealed class DouyinAssistantInfo
{
    [JsonPropertyName("is_preview")]
    public int IsPreview { get; set; } = 0;

    [JsonPropertyName("is_post_assistant")]
    public int IsPostAssistant { get; set; } = 1;
}

/// <summary>
/// 精选成员信息 — 对标浏览器 selected_member。
/// </summary>
public sealed class DouyinSelectedMemberInfo
{
    [JsonPropertyName("is_selected_member_video")]
    public bool IsSelectedMemberVideo { get; set; } = false;
}

/// <summary>
/// 章节信息 — 对标浏览器 chapter。
/// chapter 字段本身是一个 JSON 字符串（非嵌套对象）。
/// </summary>
public sealed class DouyinChapterInfo
{
    [JsonPropertyName("chapter")]
    public string Chapter { get; set; } =
        "{\"chapter_abstract\":\"\",\"chapter_details\":[],\"chapter_type\":0," +
        "\"chapter_tools_info\":{\"chapter_recommend_detail\":[]," +
        "\"chapter_recommend_abstract\":\"\",\"chapter_source\":2," +
        "\"chapter_recommend_type\":-2,\"create_date\":0," +
        "\"is_pc\":\"1\",\"is_pre_generated\":\"0\",\"is_syn\":\"1\"}}";
}

/// <summary>
/// 自主声明信息 — 对标 Python item["declare"]。
/// user_declare_info 是一个 JSON 字符串（不是嵌套对象）。
/// </summary>
public sealed class DouyinDeclareInfo
{
    /// <summary>
    /// 声明信息 JSON 字符串。
    /// 例: "{\"choose_value\":\"repost\",\"source\":\"站外\"}"
    /// </summary>
    [JsonPropertyName("user_declare_info")]
    public string UserDeclareInfo { get; set; } = "";
}

// ═══ Video Search Result DTO ═══

/// <summary>
/// 视频作品信息（从列表 API 解析）。
/// </summary>
public sealed record DouyinWorkInfo(
    string AwemeId,
    string Desc,
    string Caption,
    string? CoverUrl,
    long CreateTime,
    int Status,
    int VideoDuration,
    string VideoRatio,
    Dictionary<string, int>? Statistics
);

/// <summary>
/// 视频列表查询结果。
/// </summary>
public sealed record DouyinListWorksResult(
    List<DouyinWorkInfo> Works,
    bool HasMore,
    long MaxCursor,
    long MinCursor,
    int Total
);

/// <summary>
/// text_extra 单个条目 — 对标 createMedia.py 的 text_extra 数组元素。
/// 序列化为 [{"start":12,"end":16,"type":1,"hashtag_name":"水族馆",...}]
/// </summary>
public sealed class HashtagExtraEntry
{
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("hashtag_name")]
    public string HashtagName { get; set; } = "";

    [JsonPropertyName("hashtag_id")]
    public long HashtagId { get; set; }

    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("caption_start")]
    public int CaptionStart { get; set; }

    [JsonPropertyName("caption_end")]
    public int CaptionEnd { get; set; }
}
