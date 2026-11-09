namespace CreatFlow.Platforms.Toutiao.Requests;

/// <summary>视频合集创建/编辑响应（POST /xigua/api/pSeries/PublishPseries/）。</summary>
internal sealed class ToutiaoPublishPseriesResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public ToutiaoPublishPseriesData? Data { get; set; }
}

internal sealed class ToutiaoPublishPseriesData
{
    [JsonPropertyName("AlbumId")] public string? AlbumId { get; set; }
    [JsonPropertyName("BaseResp")] public ToutiaoBaseResp? BaseResp { get; set; }
}

internal sealed class ToutiaoBaseResp
{
    [JsonPropertyName("StatusCode")] public int StatusCode { get; set; }
    [JsonPropertyName("StatusMessage")] public string? StatusMessage { get; set; }
}

/// <summary>视频合集列表响应（GET /xigua/api/pSeries/GetAlbumInfoByMediaId/）。</summary>
internal sealed class ToutiaoPseriesListResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public ToutiaoPseriesListData? Data { get; set; }
}

internal sealed class ToutiaoPseriesListData
{
    [JsonPropertyName("Items")] public List<ToutiaoPseriesItem>? Items { get; set; }
    [JsonPropertyName("Total")] public int Total { get; set; }
    [JsonPropertyName("Limit")] public int Limit { get; set; }
    [JsonPropertyName("Offset")] public int Offset { get; set; }
}

internal sealed class ToutiaoPseriesItem
{
    [JsonPropertyName("Id")] public string Id { get; set; } = "";
    [JsonPropertyName("Title")] public string Title { get; set; } = "";
    [JsonPropertyName("ImageUri")] public string? ImageUri { get; set; }
    [JsonPropertyName("ImageUrl")] public string? ImageUrl { get; set; }
    [JsonPropertyName("AlbumItemNum")] public int AlbumItemNum { get; set; }
    [JsonPropertyName("CreateTime")] public long CreateTime { get; set; }
    [JsonPropertyName("ArticleStatusInstance")] public ToutiaoArticleStatus? ArticleStatusInstance { get; set; }
}

internal sealed class ToutiaoArticleStatus
{
    [JsonPropertyName("ItemStatusCode")] public int ItemStatusCode { get; set; }
}

/// <summary>视频合集详情响应（GET /xigua/api/pSeries/GetPseriesInfo/）。</summary>
internal sealed class ToutiaoPseriesDetailResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public ToutiaoPseriesDetailData? Data { get; set; }
}

internal sealed class ToutiaoPseriesDetailData
{
    [JsonPropertyName("Album")] public ToutiaoPseriesAlbum? Album { get; set; }
}

public sealed class ToutiaoPseriesAlbum
{
    [JsonPropertyName("AlbumId")] public string AlbumId { get; set; } = "";
    [JsonPropertyName("SeqsCount")] public int SeqsCount { get; set; }
    [JsonPropertyName("Title")] public string Title { get; set; } = "";
    [JsonPropertyName("CoverUri")] public string? CoverUri { get; set; }
    [JsonPropertyName("CoverWidth")] public int CoverWidth { get; set; }
    [JsonPropertyName("CoverHeight")] public int CoverHeight { get; set; }
    [JsonPropertyName("VisibilityLevel")] public int VisibilityLevel { get; set; }
    [JsonPropertyName("AuditStatus")] public int AuditStatus { get; set; }
    /// <summary>平台侧专辑类型（2026-11-05 抓包视频合集 = 18）；语义类型统一走 CollectionInfo.ContentType</summary>
    [JsonPropertyName("AlbumType")] public int AlbumType { get; set; }
    /// <summary>平台侧来源（抓包视频合集 = 149）</summary>
    [JsonPropertyName("GroupSource")] public int GroupSource { get; set; }
}

/// <summary>视频合集删除响应（POST /xigua/api/pSeries/DeletePseries/）。</summary>
internal sealed class ToutiaoDeletePseriesResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public ToutiaoDeletePseriesData? Data { get; set; }
}

/// <summary>删除响应的 data 是 { BaseResp: { StatusCode, StatusMessage } }（不是平铺）。</summary>
internal sealed class ToutiaoDeletePseriesData
{
    [JsonPropertyName("BaseResp")] public ToutiaoBaseResp? BaseResp { get; set; }
}

/// <summary>
/// 当前登录用户（GET /mp/agw/general/user/get_user_info）。
/// TOS 图片上传的 <c>X-Storage-U</c> 要用这里的 <c>data.data.user_id</c>，
/// 不是页面 <c>pgc_info.media.id</c>（2026-11-05 实测：用 media.id → TOS 401/4001）。
/// </summary>
internal sealed class ToutiaoUserInfoResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public ToutiaoUserInfoEnvelope? Data { get; set; }
}

internal sealed class ToutiaoUserInfoEnvelope
{
    [JsonPropertyName("data")] public ToutiaoUserInfo? Data { get; set; }
    [JsonPropertyName("errno")] public int Errno { get; set; }
    [JsonPropertyName("errmsg")] public string? ErrMsg { get; set; }
}

internal sealed class ToutiaoUserInfo
{
    [JsonPropertyName("user_id")] public string? UserId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

/// <summary>
/// VAS 图片上传参数（GET https://vas-lf-x.ixigua.com/video/openapi/v1/?action=GetImageUploadParams）。
/// <c>tos_sign</c> 是 TOS 直传的 Authorization；<c>object_ids[0]</c> 是服务端预分配的 StoreUri。
/// </summary>
internal sealed class ToutiaoImageUploadParamsResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public ToutiaoImageUploadParams? Data { get; set; }
}

internal sealed class ToutiaoImageUploadParams
{
    [JsonPropertyName("tos_host")] public string? TosHost { get; set; }
    [JsonPropertyName("tos_sign")] public string? TosSign { get; set; }
    [JsonPropertyName("object_ids")] public List<string>? ObjectIds { get; set; }
    [JsonPropertyName("bucket")] public string? Bucket { get; set; }
}

/// <summary>图片上传凭证（GET /xigua/api/upload/getAuthKey/?params={"type":"image_temp",...}）。</summary>
internal sealed class ToutiaoImageAuthResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public ToutiaoImageAuthData? Data { get; set; }
}

internal sealed class ToutiaoImageAuthData
{
    [JsonPropertyName("accessKey")] public string? AccessKey { get; set; }
    [JsonPropertyName("uploadToken")] public string? UploadToken { get; set; }
}
