namespace CreatFlow.Platforms.Kuaishou.Requests;

// ═══ 响应基类 ═══

/// <summary>快手 API 通用响应信封。</summary>
internal sealed class KuaishouEnvelope<T>
{
    [JsonPropertyName("result")]
    public int Result { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

// ═══ 用户信息 ═══

internal sealed class KuaishouUserResponse
{
    [JsonPropertyName("coreUserInfo")]
    public KuaishouUserInfo? CoreUserInfo { get; set; }

    /// <summary>捕获 JSON 中尚未建模的额外字段（方便以后扩展）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

internal sealed class KuaishouUserInfo
{
    [JsonPropertyName("userId")]
    public long UserId { get; set; }
    [JsonPropertyName("userName")]
    public string? UserName { get; set; }
    [JsonPropertyName("headUrl")]
    public string? HeadUrl { get; set; }
    [JsonPropertyName("fansNum")]
    public int FansNum { get; set; }

    /// <summary>捕获 JSON 中尚未建模的额外字段。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

// ═══ 视频列表 ═══

internal sealed class KuaishouVideoListResponse
{
    [JsonPropertyName("list")]
    public List<KuaishouVideoItem>? List { get; set; }
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }
    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>视频作品条目（用于公开 API 返回）。</summary>
public sealed class KuaishouVideoItem
{
    [JsonPropertyName("publishId")]
    public long PublishId { get; set; }
    [JsonPropertyName("workId")]
    public string? WorkId { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("publishCoverUrl")]
    public string? PublishCoverUrl { get; set; }
    [JsonPropertyName("unPublishCoverKey")]
    public string? UnPublishCoverKey { get; set; }
    [JsonPropertyName("userName")]
    public string? UserName { get; set; }
    [JsonPropertyName("userHead")]
    public string? UserHead { get; set; }
    [JsonPropertyName("playCount")]
    public int PlayCount { get; set; }
    [JsonPropertyName("likeCount")]
    public int LikeCount { get; set; }
    [JsonPropertyName("commentCount")]
    public int CommentCount { get; set; }
    [JsonPropertyName("uploadTime")]
    public long UploadTime { get; set; }
    [JsonPropertyName("durationSecond")]
    public int DurationSecond { get; set; }
    [JsonPropertyName("publishStatus")]
    public int PublishStatus { get; set; }
    [JsonPropertyName("photoStatus")]
    public int PhotoStatus { get; set; }
    [JsonPropertyName("photoTop")]
    public bool PhotoTop { get; set; }
    [JsonPropertyName("collectionTitle")]
    public string? CollectionTitle { get; set; }
    [JsonPropertyName("collectionId")]
    public long CollectionId { get; set; }
    [JsonPropertyName("operations")]
    public List<string>? Operations { get; set; }
    [JsonPropertyName("publishType")]
    public int PublishType { get; set; }
    [JsonPropertyName("coPublish")]
    public bool CoPublish { get; set; }

    /// <summary>
    /// 捕获 JSON 中尚未建模的额外字段。回查探针（<c>--probe-kuaishou-work</c>）靠它读
    /// <c>hotspotActivity</c> / <c>hotspot</c> / <c>tags</c> 等 —— 官方活动是否挂上就看
    /// <c>hotspotActivity</c>（2026-11-09）。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
}

// ═══ 上传 ═══

internal sealed class KuaishouPreInfoResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }
    [JsonPropertyName("endPoints")]
    public List<string>? EndPoints { get; set; }
    [JsonPropertyName("fileId")]
    public long FileId { get; set; }
}

internal sealed class KuaishouFinishResponse
{
    [JsonPropertyName("fileId")]
    public long FileId { get; set; }
    [JsonPropertyName("mediaId")]
    public string? MediaId { get; set; }
    [JsonPropertyName("coverKey")]
    public string? CoverKey { get; set; }
    [JsonPropertyName("coverMediaId")]
    public string? CoverMediaId { get; set; }
    [JsonPropertyName("videoDuration")]
    public int VideoDuration { get; set; }
    [JsonPropertyName("width")]
    public int Width { get; set; }
    [JsonPropertyName("height")]
    public int Height { get; set; }
    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }
    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }
    [JsonPropertyName("photoIdStr")]
    public string? PhotoIdStr { get; set; }
}

// ═══ 封面 ═══

internal sealed class KuaishouCoverResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }
    [JsonPropertyName("list")]
    public List<string>? List { get; set; }
}

// ═══ 合集 ═══

internal sealed class KuaishouCollectionListResponse
{
    [JsonPropertyName("list")]
    public List<KuaishouCollectionItem>? List { get; set; }
    [JsonPropertyName("total")]
    public int Total { get; set; }
}

internal sealed class KuaishouCollectionItem
{
    [JsonPropertyName("collectionId")]
    public long CollectionId { get; set; }
    [JsonPropertyName("tubeId")]
    public string? TubeId { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    [JsonPropertyName("coverUrl")]
    public string? CoverUrl { get; set; }
    [JsonPropertyName("createTime")]
    public long CreateTime { get; set; }
    [JsonPropertyName("viewCount")]
    public long ViewCount { get; set; } // 真实数据可达 44 亿（> int.MaxValue），必须 long 否则反序列化抛 JsonException
    [JsonPropertyName("likeCount")]
    public int LikeCount { get; set; }
    [JsonPropertyName("commentCount")]
    public int CommentCount { get; set; }
    [JsonPropertyName("collectCount")]
    public int CollectCount { get; set; }
    [JsonPropertyName("size")]
    public int Size { get; set; }
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }
    [JsonPropertyName("status")]
    public int Status { get; set; }
    [JsonPropertyName("showOnProfile")]
    public int ShowOnProfile { get; set; }
    [JsonPropertyName("publishStatus")]
    public int PublishStatus { get; set; }
}

internal sealed class KuaishouCollectionInfoResponse
{
    [JsonPropertyName("collectionId")]
    public long CollectionId { get; set; }
    [JsonPropertyName("tubeId")]
    public string? TubeId { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    [JsonPropertyName("type")]
    public string? Type { get; set; }
    [JsonPropertyName("showType")]
    public int ShowType { get; set; }
    [JsonPropertyName("photoIdList")]
    public List<long>? PhotoIdList { get; set; }
    [JsonPropertyName("notAdopted")]
    public int NotAdopted { get; set; }
    [JsonPropertyName("createTime")]
    public long CreateTime { get; set; }
    [JsonPropertyName("publishStatus")]
    public int PublishStatus { get; set; }
    [JsonPropertyName("collectCount")]
    public int CollectCount { get; set; }
    [JsonPropertyName("viewCount")]
    public long ViewCount { get; set; } // 真实数据可达 44 亿（> int.MaxValue），必须 long 否则反序列化抛 JsonException
}

internal sealed class KuaishouCollectionCreateResponse
{
    [JsonPropertyName("titleIsSensitive")]
    public bool TitleIsSensitive { get; set; }
    [JsonPropertyName("desIsSensitive")]
    public bool DesIsSensitive { get; set; }
    [JsonPropertyName("collectionId")]
    public long CollectionId { get; set; }
}

/// <summary>未收录作品列表</summary>
internal sealed class KuaishouUnrecordedListResponse
{
    [JsonPropertyName("list")]
    public List<KuaishouUnrecordedItem>? List { get; set; }
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }
}

/// <summary>未收录作品条目（用于公开 API 返回）。</summary>
public sealed class KuaishouUnrecordedItem
{
    [JsonPropertyName("photoId")]
    public long PhotoId { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("coverUrl")]
    public string? CoverUrl { get; set; }
    [JsonPropertyName("publishTime")]
    public long PublishTime { get; set; }
    [JsonPropertyName("duration")]
    public int Duration { get; set; }
    [JsonPropertyName("status")]
    public int Status { get; set; }
    [JsonPropertyName("viewCount")]
    public long ViewCount { get; set; } // 真实数据可达 44 亿（> int.MaxValue），必须 long 否则反序列化抛 JsonException
    [JsonPropertyName("likeCount")]
    public int LikeCount { get; set; }
    [JsonPropertyName("playUrl")]
    public string? PlayUrl { get; set; }
    [JsonPropertyName("available")]
    public bool Available { get; set; }
    [JsonPropertyName("filterReason")]
    public string? FilterReason { get; set; }
    [JsonPropertyName("photoType")]
    public int PhotoType { get; set; }
}

// ═══ 标签 ═══

internal sealed class KuaishouTagSearchResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }
    [JsonPropertyName("pcursor")]
    public string? Pcursor { get; set; }
    [JsonPropertyName("tags")]
    public List<KuaishouTagItem>? Tags { get; set; }
}

internal sealed class KuaishouTagItem
{
    [JsonPropertyName("tag")]
    public KuaishouTagInfo? Tag { get; set; }
    [JsonPropertyName("viewCount")]
    public long ViewCount { get; set; } // 真实数据可达 44 亿（> int.MaxValue），必须 long 否则反序列化抛 JsonException
}

internal sealed class KuaishouTagInfo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("data")]
    public string? Data { get; set; }
    [JsonPropertyName("status")]
    public int Status { get; set; }
}

internal sealed class KuaishouRecTagResponse
{
    [JsonPropertyName("tags")]
    public List<KuaishouRecTagItem>? Tags { get; set; }
}

internal sealed class KuaishouRecTagItem
{
    [JsonPropertyName("tag")]
    public KuaishouTagInfo? Tag { get; set; }
    [JsonPropertyName("viewCount")]
    public long ViewCount { get; set; } // 真实数据可达 44 亿（> int.MaxValue），必须 long 否则反序列化抛 JsonException
}

// ═══ 位置 ═══

/// <summary>附近 POI 响应</summary>
internal sealed class KuaishouNearbyResponse
{
    [JsonPropertyName("cityName")]
    public string? CityName { get; set; }
    [JsonPropertyName("pcursor")]
    public string? Pcursor { get; set; }
    [JsonPropertyName("locations")]
    public List<KuaishouPoiItem>? Locations { get; set; }
}

/// <summary>POI 搜索响应（无 envelope）</summary>
internal sealed class KuaishouPoiSearchResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }
    [JsonPropertyName("pcursor")]
    public string? Pcursor { get; set; }
    [JsonPropertyName("locations")]
    public List<KuaishouPoiItem>? Locations { get; set; }
}

internal sealed class KuaishouPoiItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
    [JsonPropertyName("idString")]
    public string? IdString { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("address")]
    public string? Address { get; set; }
    [JsonPropertyName("city")]
    public string? City { get; set; }
    [JsonPropertyName("category")]
    public int Category { get; set; }
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }
    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }
}

// ═══ 作者搜索 (search_author.py) ═══

/// <summary>作者搜索 data 部分 — /rest/cp/works/v2/common/pc/author/search 返回 envelope。</summary>
internal sealed class KuaishouAuthorSearchResponse
{
    [JsonPropertyName("list")]
    public List<KuaishouAuthorItem>? List { get; set; }
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }
}

internal sealed class KuaishouAuthorItem
{
    [JsonPropertyName("userId")]
    public long UserId { get; set; }
    [JsonPropertyName("userName")]
    public string? UserName { get; set; }
    [JsonPropertyName("photoNum")]
    public int PhotoNum { get; set; }
    [JsonPropertyName("likeNum")]
    public int LikeNum { get; set; }
    [JsonPropertyName("userHead")]
    public List<KuaishouUserHeadItem>? UserHead { get; set; }
}

internal sealed class KuaishouUserHeadItem
{
    [JsonPropertyName("cdn")]
    public string? Cdn { get; set; }
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}


// ═══ 关联热点（B3 抓包 2026-11-11）═══

/// <summary><c>hotspot/list</c> 的 <c>data</c>：候选列表 + 下一页游标（-2147483648 = 无更多）。</summary>
internal sealed class KuaishouHotspotListResponse
{
    [JsonPropertyName("list")]
    public List<KuaishouHotspotItemDto>? List { get; set; }

    [JsonPropertyName("nextCursor")]
    public long NextCursor { get; set; }
}

internal sealed class KuaishouHotspotItemDto
{
    [JsonPropertyName("hotspotId")]
    public long HotspotId { get; set; }
    [JsonPropertyName("hotspotContent")]
    public string? HotspotContent { get; set; }
    [JsonPropertyName("hotValue")]
    public string? HotValue { get; set; }
    [JsonPropertyName("feedCount")]
    public long FeedCount { get; set; }
    [JsonPropertyName("rising")]
    public bool Rising { get; set; }
}

// ═══ @好友（B3 抓包 2026-11-11）═══

/// <summary>
/// <c>at/search</c> 的 <c>data</c>：候选用户 + 是否还有下一页 + 会话号。
/// 抓包：<c>{"hasMore":true,"sessionId":"…","list":[{"userName":"房","userId":2210679639,
/// "fansCount":4272,"headUrl":"https://…"}]}</c>。
/// </summary>
internal sealed class KuaishouAtSearchResponse
{
    [JsonPropertyName("list")]
    public List<KuaishouAtUserDto>? List { get; set; }
    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }
}

internal sealed class KuaishouAtUserDto
{
    [JsonPropertyName("userName")]
    public string? UserName { get; set; }
    [JsonPropertyName("userId")]
    public long UserId { get; set; }
    [JsonPropertyName("fansCount")]
    public long FansCount { get; set; }
    [JsonPropertyName("headUrl")]
    public string? HeadUrl { get; set; }
}

/// <summary>
/// <c>at/choose</c> 的响应外壳 —— 抓包是 <c>{"result":1,"data":null}</c>：
/// <c>data</c> 恒为 null，所以不能走 <see cref="KuaishouApiClient.PostAsync{T}"/>（它会把 null data 当错误抛）。
/// </summary>
internal sealed class KuaishouAtChooseResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

// ═══ 作者服务 · 关联小程序（2026-11-15 真页面抓包）═══

/// <summary>
/// <c>relation/list</c> 的响应外壳 —— <c>{result, data:{list, cursor}}</c>。
/// cursor 在响应里是**字符串**（<c>"0"</c>），请求里也是字符串（首屏 <c>""</c>）。
/// </summary>
internal sealed class KuaishouRelationListResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }
    [JsonPropertyName("data")]
    public KuaishouRelationListData? Data { get; set; }
}

internal sealed class KuaishouRelationListData
{
    [JsonPropertyName("list")]
    public List<KuaishouRelationItemDto>? List { get; set; }
    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }
}

internal sealed class KuaishouRelationItemDto
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("url")]
    public string? Url { get; set; }
    [JsonPropertyName("desc")]
    public string? Desc { get; set; }
    [JsonPropertyName("associateTaskId")]
    public KuaishouAssociateTaskDto? AssociateTaskId { get; set; }
    [JsonPropertyName("validMessage")]
    public string? ValidMessage { get; set; }
}

/// <summary>
/// <c>associateTaskId</c> —— 关联小程序的 wire 元素（2026-11-15 提交抓包逐字）：
/// <c>{"taskId":0,"bindId":"plc…","entranceType":9,"bindType":"25","taskType":"PLC"}</c>。
/// 页面选中后把**整个对象**放进 submit 的 <c>associateTasks</c> 数组。
/// <c>taskId</c> 观测为 0（不是 ID>0 才能用）；<c>bindId</c> 才是真正标识。
/// </summary>
public sealed class KuaishouAssociateTaskDto
{
    [JsonPropertyName("taskId")]
    public long TaskId { get; set; }
    [JsonPropertyName("bindId")]
    public string BindId { get; set; } = "";
    [JsonPropertyName("entranceType")]
    public int EntranceType { get; set; }
    [JsonPropertyName("bindType")]
    public string BindType { get; set; } = "";
    [JsonPropertyName("taskType")]
    public string TaskType { get; set; } = "";
}

// ═══ 共同创作（2026-11-15 发布页 bundle 定案）═══

/// <summary>
/// submit <c>coPublishUser</c> 数组的元素 —— 发布页 bundle 逐字：
/// <c>{userId, userName, userHead, coPublishRole}</c>。
/// 角色枚举（bundle <c>Ti</c>）：<c>-1 默认 / 0 作者 / 1 共创 / 2 出镜</c>。
/// </summary>
public sealed class KuaishouCoPublishUserDto
{
    [JsonPropertyName("userId")]
    public long UserId { get; set; }
    [JsonPropertyName("userName")]
    public string UserName { get; set; } = "";
    [JsonPropertyName("userHead")]
    public string UserHead { get; set; } = "";
    [JsonPropertyName("coPublishRole")]
    public int CoPublishRole { get; set; } = -1;
}

// ═══ 变现入口 ═══

internal sealed class KuaishouEntranceResponse
{
    [JsonPropertyName("items")]
    public List<KuaishouEntranceItem>? Items { get; set; }
}

internal sealed class KuaishouEntranceItem
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("type")]
    public string? Type { get; set; }
    [JsonPropertyName("bizType")]
    public string? BizType { get; set; }
}

// ═══ 发布请求体 ═══

/// <summary>
/// 快手视频发布请求体（submit API）。
/// 对应 Python api.py submit() 方法的 ~55 个字段。
/// </summary>
public sealed class KuaishouSubmitRequest
{
    [JsonPropertyName("fileId")]
    public long FileId { get; set; }

    [JsonPropertyName("coverKey")]
    public string CoverKey { get; set; } = "";

    [JsonPropertyName("coverTimeStamp")]
    public int CoverTimeStamp { get; set; }

    [JsonPropertyName("caption")]
    public string Caption { get; set; } = "";

    [JsonPropertyName("photoStatus")]
    public int PhotoStatus { get; set; } = 1;

    [JsonPropertyName("coverType")]
    public int CoverType { get; set; } = 1;

    [JsonPropertyName("coverTitle")]
    public string CoverTitle { get; set; } = "";

    /// <summary>
    /// 封面文字样式 —— <c>coverTitleStyle</c>。**取值未验证**（只在作品编辑提交里观测到，
    /// 恒为空串；2026-11-15 发布页 submit 不带该键）。null = 不下发（保持页面行为）。
    /// </summary>
    [JsonPropertyName("coverTitleStyle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoverTitleStyle { get; set; }

    [JsonPropertyName("photoType")]
    public int PhotoType { get; set; }

    [JsonPropertyName("collectionId")]
    public object? CollectionId { get; set; }  // long or ""

    [JsonPropertyName("publishTime")]
    public long PublishTime { get; set; }        // ms, 0 = immediate

    [JsonPropertyName("longitude")]
    public string Longitude { get; set; } = "";

    [JsonPropertyName("latitude")]
    public string Latitude { get; set; } = "";

    /// <summary>POI 数字 ID —— 抓包是 number（无位置时 0），不是字符串</summary>
    [JsonPropertyName("poiId")]
    public object PoiId { get; set; } = 0;

    [JsonPropertyName("notifyResult")]
    public int NotifyResult { get; set; }

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = "";

    [JsonPropertyName("secondDomain")]
    public string SecondDomain { get; set; } = "";

    [JsonPropertyName("coverCropped")]
    public bool CoverCropped { get; set; }

    [JsonPropertyName("pkCoverKey")]
    public string PkCoverKey { get; set; } = "";

    [JsonPropertyName("profileCoverKey")]
    public string ProfileCoverKey { get; set; } = "";

    [JsonPropertyName("downloadType")]
    public int DownloadType { get; set; } = 1;

    [JsonPropertyName("disableNearbyShow")]
    public bool DisableNearbyShow { get; set; }

    [JsonPropertyName("allowSameFrame")]
    public bool AllowSameFrame { get; set; } = true;

    [JsonPropertyName("movieId")]
    public string MovieId { get; set; } = "";

    [JsonPropertyName("openPrePreview")]
    public bool OpenPrePreview { get; set; }

    [JsonPropertyName("declareInfo")]
    public object? DeclareInfo { get; set; }

    [JsonPropertyName("activityIds")]
    public List<object> ActivityIds { get; set; } = [];

    [JsonPropertyName("riseQuality")]
    public bool RiseQuality { get; set; }

    /// <summary>章节 —— 元素结构未抓到（抓包恒 []），允许原始 JSON 透传</summary>
    [JsonPropertyName("chapters")]
    public object Chapters { get; set; } = Array.Empty<object>();

    /// <summary>
    /// 关联热点 ID（B3 抓包 2026-11-11）—— <c>hotspotId</c>，数字。
    /// 未选热点时**两个键都不出现**（页面清除时把它们加进 <c>needDeleteKey</c>）。
    /// </summary>
    [JsonPropertyName("hotspotId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? HotspotId { get; set; }

    /// <summary>关联热点类型 —— <c>hotspotConnectType</c>，恒为 1（热点）。</summary>
    [JsonPropertyName("hotspotConnectType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HotspotConnectType { get; set; }

    [JsonPropertyName("useAiCaptionCover")]
    public bool UseAiCaptionCover { get; set; }

    [JsonPropertyName("useAiCaption")]
    public bool UseAiCaption { get; set; }

    [JsonPropertyName("isUseIdealTime")]
    public bool IsUseIdealTime { get; set; }

    [JsonPropertyName("useAiCover")]
    public bool UseAiCover { get; set; }

    [JsonPropertyName("kceInfo")]
    public string KceInfo { get; set; } = "";

    [JsonPropertyName("coverSize")]
    public string CoverSize { get; set; } = "";

    [JsonPropertyName("pkCoverTimeStamp")]
    public int PkCoverTimeStamp { get; set; } = -1;

    [JsonPropertyName("pkCoverType")]
    public int PkCoverType { get; set; } = 1;

    [JsonPropertyName("pkCoverSize")]
    public string PkCoverSize { get; set; } = "";

    [JsonPropertyName("innerChannel")]
    public int InnerChannel { get; set; }

    [JsonPropertyName("mediaId")]
    public string MediaId { get; set; } = "";

    [JsonPropertyName("videoInfoMeta")]
    public string VideoInfoMeta { get; set; } = "";

    [JsonPropertyName("triggerH265")]
    public bool TriggerH265 { get; set; }

    [JsonPropertyName("recTagIdList")]
    public List<long> RecTagIdList { get; set; } = [];

    [JsonPropertyName("onvideoDuration")]
    public int OnvideoDuration { get; set; }

    [JsonPropertyName("disallowRecreation")]
    public bool DisallowRecreation { get; set; }

    [JsonPropertyName("previewUrlErrorMessage")]
    public string PreviewUrlErrorMessage { get; set; } = "";

    /// <summary>共创用户 —— 元素结构未抓到（抓包恒 []），允许原始 JSON 透传</summary>
    [JsonPropertyName("coPublishUser")]
    public object CoPublishUser { get; set; } = Array.Empty<object>();

    [JsonPropertyName("coPublishRole")]
    public int CoPublishRole { get; set; }

    [JsonPropertyName("extraInfo")]
    public string ExtraInfo { get; set; } = "";

    [JsonPropertyName("videoDuration")]
    public int VideoDuration { get; set; }

    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("photoIdStr")]
    public string PhotoIdStr { get; set; } = "";

    /// <summary>
    /// 作者服务 · 关联小程序（2026-11-15 受控发布抓包）—— 整个 <c>associateTaskId</c> 对象数组。
    /// 未关联时页面 submit **不带这个键**（2026-11-05 抓包无此键），所以 null 即省略。
    /// </summary>
    [JsonPropertyName("associateTasks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<KuaishouAssociateTaskDto>? AssociateTasks { get; set; }

    [JsonPropertyName("kuaishou.web.cp.api_ph")]
    public string ApiPh { get; set; } = "";
}

/// <summary>
/// 快手视频发布响应（submit API 返回无 data 包裹，仅 result + message）。
/// 对标 Python api.py submit() 的返回格式。
/// </summary>
internal sealed class KuaishouSubmitResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error_msg")]
    public string? ErrorMsg { get; set; }
}
