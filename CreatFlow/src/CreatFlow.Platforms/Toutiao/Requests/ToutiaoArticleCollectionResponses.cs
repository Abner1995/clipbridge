namespace CreatFlow.Platforms.Toutiao.Requests;

// ═══════════════════════════════════════════════════════════════════
// 头条**文章合集**（/mp/fe_api/collection/*）与文章封面登记（/spice/image）。
//
// 与视频合集（/xigua/api/pSeries/*）是两套完全独立的接口和响应壳：
//   文章：{ "BaseResp": { StatusCode, StatusMessage }, "code": 0, "message": "" }
//   视频：{ "status": 0, "message": "success", "data": { ... } }
// 千万别混用（2026-11-05 抓包，见 docs/toutiao/20261105/合集接口.md）。
// ═══════════════════════════════════════════════════════════════════

/// <summary>文章合集列表（GET /mp/fe_api/collection/list）。</summary>
internal sealed class ToutiaoArticleCollectionListResponse
{
    [JsonPropertyName("BaseResp")] public ToutiaoBaseResp? BaseResp { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("total_count")] public string? TotalCount { get; set; }
    [JsonPropertyName("data")] public List<ToutiaoArticleCollectionItem>? Data { get; set; }
}

internal sealed class ToutiaoArticleCollectionItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("collection_attr")] public ToutiaoArticleCollectionAttr? CollectionAttr { get; set; }
    [JsonPropertyName("sub_item_count")] public int SubItemCount { get; set; }
    [JsonPropertyName("item_status_desc")] public string? ItemStatusDesc { get; set; }
    [JsonPropertyName("menu_list")] public ToutiaoArticleMenuList? MenuList { get; set; }
}

internal sealed class ToutiaoArticleCollectionAttr
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("cover_image")] public ToutiaoArticleCoverImage? CoverImage { get; set; }
    [JsonPropertyName("item_status")] public int ItemStatus { get; set; }
    [JsonPropertyName("visibility_level")] public int VisibilityLevel { get; set; }
    [JsonPropertyName("create_time")] public string? CreateTime { get; set; }
    [JsonPropertyName("user_id")] public string? UserId { get; set; }
}

internal sealed class ToutiaoArticleCoverImage
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("uri")] public string? Uri { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("mime_type")] public string? MimeType { get; set; }
}

internal sealed class ToutiaoArticleMenuList
{
    [JsonPropertyName("show_delete")] public bool ShowDelete { get; set; }
    [JsonPropertyName("show_modify")] public bool ShowModify { get; set; }
    [JsonPropertyName("forbid_delete_reason")] public string? ForbidDeleteReason { get; set; }
    [JsonPropertyName("forbid_modify_reason")] public string? ForbidModifyReason { get; set; }
}

/// <summary>文章合集详情 / 编辑回显（GET /mp/fe_api/collection/edit）。</summary>
internal sealed class ToutiaoArticleCollectionDetailResponse
{
    [JsonPropertyName("BaseResp")] public ToutiaoBaseResp? BaseResp { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("collection_id")] public string? CollectionId { get; set; }
    [JsonPropertyName("collection_attr")] public ToutiaoArticleCollectionAttr? CollectionAttr { get; set; }
    [JsonPropertyName("sub_item_list")] public List<object>? SubItemList { get; set; }
    [JsonPropertyName("all_sub_item_id_list")] public List<string>? AllSubItemIdList { get; set; }
}

/// <summary>文章合集创建/编辑响应（POST /mp/fe_api/collection/publish）。</summary>
internal sealed class ToutiaoArticlePublishResponse
{
    [JsonPropertyName("CollectionID")] public string? CollectionId { get; set; }
    [JsonPropertyName("BaseResp")] public ToutiaoBaseResp? BaseResp { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

/// <summary>文章合集删除响应（POST /mp/fe_api/collection/delete）。</summary>
internal sealed class ToutiaoArticleDeleteResponse
{
    [JsonPropertyName("BaseResp")] public ToutiaoBaseResp? BaseResp { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

/// <summary>
/// 文章封面登记（POST /spice/image?upload_source=20020010）。
/// 请求是 multipart，字段名 <c>image</c>（页面 collection-publish.js 实测）；
/// 响应里的 <c>origin_image_uri</c> 才是合集 cover_image.uri 用的值。
/// </summary>
internal sealed class ToutiaoSpiceImageResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public ToutiaoSpiceImageData? Data { get; set; }
}

internal sealed class ToutiaoSpiceImageData
{
    [JsonPropertyName("image_uri")] public string? ImageUri { get; set; }
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
    [JsonPropertyName("origin_image_uri")] public string? OriginImageUri { get; set; }
    [JsonPropertyName("origin_image_url")] public string? OriginImageUrl { get; set; }
    [JsonPropertyName("image_width")] public int ImageWidth { get; set; }
    [JsonPropertyName("image_height")] public int ImageHeight { get; set; }
    [JsonPropertyName("image_format")] public string? ImageFormat { get; set; }
    [JsonPropertyName("image_mime_type")] public string? ImageMimeType { get; set; }
}
