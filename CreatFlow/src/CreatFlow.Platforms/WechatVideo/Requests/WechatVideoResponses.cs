namespace CreatFlow.Platforms.WechatVideo.Requests;

using System.Text.Json.Serialization;

// ═══ 说明 ═══
// 本文件所有类都是 STJ 反序列化边界（微信 API 响应）。
// ⚠️ 必须显式写 [JsonPropertyName]（对照 docs/wechatvideo 抓包/JS 的真实字段名）：
//    打包时 Obfuscar 会把属性名改成乱码，依赖 PropertyNameCaseInsensitive
//    按 CLR 属性名匹配的绑定会全部失效（errCode 恒 0、data 恒 Undefined，
//    随后 JsonElement.GetRawText() 抛 "Operation is not valid due to the
//    current state of the object"）。特性字符串是常量，混淆不改。

// ═══ CDN Upload APIs (finder.video.qq.com) ═══

/// <summary>applyUpload 响应 — PUT /applyuploaddfs</summary>
internal sealed class ApplyUploadResponse
{
    [JsonPropertyName("uploadID")]
    public string? UploadID { get; set; }
    public ApplyUploadData? Data { get; set; }
}

internal sealed class ApplyUploadData
{
    /// <summary>upload.js:21086 → n.data.UploadID</summary>
    [JsonPropertyName("UploadID")]
    public string? UploadID { get; set; }
}

/// <summary>uploadPart 响应 — PUT /uploadpartdfs</summary>
internal sealed class UploadPartResponse
{
    [JsonPropertyName("etag")]
    public string? ETag { get; set; }
    [JsonPropertyName("transFlag")]
    public string? TransFlag { get; set; }
    public UploadPartData? Data { get; set; }
}

internal sealed class UploadPartData
{
    /// <summary>upload.js:21281 → i.data.ETag</summary>
    [JsonPropertyName("ETag")]
    public string? ETag { get; set; }
    /// <summary>upload.js:21280 → i.data.TransFlag</summary>
    [JsonPropertyName("TransFlag")]
    public string? TransFlag { get; set; }
}

/// <summary>completeUpload 响应 — POST /completepartuploaddfs</summary>
internal sealed class CompleteUploadResponse
{
    [JsonPropertyName("downloadURL")]
    public string? DownloadURL { get; set; }
    public CompleteUploadData? Data { get; set; }
}

internal sealed class CompleteUploadData
{
    /// <summary>upload.js:21602 → n.data.DownloadURL</summary>
    [JsonPropertyName("DownloadURL")]
    public string? DownloadURL { get; set; }
}

// ═══ Auth API (channels.weixin.qq.com) ═══

/// <summary>helper_upload_params 响应（字段名对照 docs/wechatvideo/接口/helper_upload_params_result.json）</summary>
internal sealed class UploadParamsResponse
{
    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }
    [JsonPropertyName("errMsg")]
    public string? ErrMsg { get; set; }
    [JsonPropertyName("data")]
    public UploadParamsData? Data { get; set; }
}

internal sealed class UploadParamsData
{
    [JsonPropertyName("uin")]
    public string? Uin { get; set; }
    [JsonPropertyName("appType")]
    public int AppType { get; set; }
    [JsonPropertyName("authKey")]
    public string? AuthKey { get; set; }
    [JsonPropertyName("scene")]
    public int Scene { get; set; }
    [JsonPropertyName("videoFileType")]
    public int VideoFileType { get; set; }
    [JsonPropertyName("pictureFileType")]
    public int PictureFileType { get; set; }
    [JsonPropertyName("thumbFileType")]
    public int ThumbFileType { get; set; }
    [JsonPropertyName("musicType")]
    public int MusicType { get; set; }
    [JsonPropertyName("pagFileType")]
    public int PagFileType { get; set; }
    [JsonPropertyName("zipType")]
    public int ZipType { get; set; }
    [JsonPropertyName("liveAppType")]
    public int LiveAppType { get; set; }
}

// ═══ Content Management APIs ═══

/// <summary>通用 API 响应（post_list / post_create / post_delete 共用）</summary>
internal sealed class WechatApiResponse
{
    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }
    [JsonPropertyName("errMsg")]
    public string? ErrMsg { get; set; }
    [JsonPropertyName("data")]
    public System.Text.Json.JsonElement Data { get; set; }
}

/// <summary>post_list data 结构（listadd.js:4271-4272 → data.list / data.lastBuff / data.continueFlag）</summary>
internal sealed class PostListData
{
    [JsonPropertyName("list")]
    public List<PostListItem>? List { get; set; }
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
    [JsonPropertyName("continueFlag")]
    public bool ContinueFlag { get; set; }
    [JsonPropertyName("lastBuff")]
    public string? LastBuff { get; set; }
}

public sealed class PostListItem
{
    [JsonPropertyName("objectId")]
    public string? ObjectId { get; set; }
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    [JsonPropertyName("createTime")]
    public long CreateTime { get; set; }
    [JsonPropertyName("thumbUrl")]
    public string? ThumbUrl { get; set; }
    [JsonPropertyName("videoUrl")]
    public string? VideoUrl { get; set; }
    [JsonPropertyName("mediaType")]
    public int MediaType { get; set; }
    [JsonPropertyName("status")]
    public int Status { get; set; }
    [JsonPropertyName("exportId")]
    public string? ExportId { get; set; }
    /// <summary>发布时传入的 videoClipTaskId（clipKey），用于匹配回查 objectId</summary>
    [JsonPropertyName("urlCdnTaskId")]
    public string? UrlCdnTaskId { get; set; }
}

/// <summary>post_create data 结构（实际返回 data.baseResp，objectId 需从 post_list 回查）</summary>
internal sealed class PostCreateData
{
    [JsonPropertyName("objectId")]
    public string? ObjectId { get; set; }
}

// ═══ Collection APIs ═══

/// <summary>get_collection_list data 结构（listadd.js:4377-4378 → data.collectionList / data.collectionListCount）</summary>
internal sealed class CollectionListData
{
    [JsonPropertyName("collectionList")]
    public List<CollectionListItem>? Collections { get; set; }
    [JsonPropertyName("collectionListCount")]
    public int TotalCount { get; set; }
}

internal sealed class CollectionListItem
{
    [JsonPropertyName("collectionId")]
    public string? CollectionId { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    [JsonPropertyName("coverUrl")]
    public string? CoverUrl { get; set; }
    [JsonPropertyName("videoCount")]
    public int VideoCount { get; set; }
    [JsonPropertyName("status")]
    public int Status { get; set; }
    [JsonPropertyName("stickyOp")]
    public int StickyOp { get; set; }     // 1=已置顶
    [JsonPropertyName("createTime")]
    public long CreateTime { get; set; }
    [JsonPropertyName("updateTime")]
    public long UpdateTime { get; set; }
}

/// <summary>create_collection data 结构（listadd.js:8937 → data.collectionId）</summary>
internal sealed class CreateCollectionData
{
    [JsonPropertyName("collectionId")]
    public string? CollectionId { get; set; }
}

// ═══ Location Search ═══

internal sealed class LocationSearchData
{
    [JsonPropertyName("list")]
    public List<LocationItem>? List { get; set; }
}

public sealed class LocationItem
{
    [JsonPropertyName("name")]
    public string? PoiName { get; set; }
    [JsonPropertyName("address")]
    public string? Address { get; set; }
    [JsonPropertyName("city")]
    public string? City { get; set; }
    // API 返回 "uid" → PoiClassifyId
    [JsonPropertyName("uid")]
    public string? PoiClassifyId { get; set; }
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }
    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }
}

// ═══ Tag / Object Tag List ═══

internal sealed class ObjectTagListData
{
    [JsonPropertyName("tags")]
    public List<ObjectTagItem>? TagList { get; set; }
    /// <summary>会话级 tagKey — 选标签后发 post_create 时透传</summary>
    [JsonPropertyName("tagKey")]
    public string? TagKey { get; set; }
}

public sealed class ObjectTagItem
{
    [JsonPropertyName("tagType")]
    public int TagType { get; set; }
    [JsonPropertyName("tagName")]
    public string? TagName { get; set; }
    /// <summary>预览展示名（如"作者提示: 内容为转载"）</summary>
    [JsonPropertyName("previewTagName")]
    public string? PreviewTagName { get; set; }
    /// <summary>二级操作提示（如"添加拍摄时间和地点"）</summary>
    [JsonPropertyName("secondTagTips")]
    public string? SecondTagTips { get; set; }
    /// <summary>地区树（仅 tagType=5 内容为自行拍摄 时有值，用于拍摄地点选择器）</summary>
    [JsonPropertyName("regionInfos")]
    public List<RegionInfoItem>? RegionInfos { get; set; }
}

/// <summary>视频标注地区树节点（省→市二级）。仅 tagType=5（内容为自行拍摄）返回。</summary>
public sealed class RegionInfoItem
{
    [JsonPropertyName("code")]
    public int Code { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("sub")]
    public List<RegionInfoItem>? Sub { get; set; }
}

// ═══ Clip Video APIs ═══

/// <summary>post_clip_video 响应</summary>
internal sealed class ClipVideoData
{
    [JsonPropertyName("clipKey")]
    public string? ClipKey { get; set; }
    [JsonPropertyName("draftId")]
    public string? DraftId { get; set; }
}

/// <summary>post_clip_video_result 响应（字段名对照 docs/wechatvideo/接口/post_clip_video_result_result.json）</summary>
public sealed class ClipVideoResultData
{
    [JsonPropertyName("flag")]
    public int Flag { get; set; }           // 0=processing, 1=done
    [JsonPropertyName("url")]
    public string? Url { get; set; }        // 处理后的永久视频 URL（含 basedata + sign）
    [JsonPropertyName("width")]
    public int Width { get; set; }
    [JsonPropertyName("height")]
    public int Height { get; set; }
    [JsonPropertyName("duration")]
    public double Duration { get; set; }
    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }
    [JsonPropertyName("fileSize")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long FileSize { get; set; }
}
