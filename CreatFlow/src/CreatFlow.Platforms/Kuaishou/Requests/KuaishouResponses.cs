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
