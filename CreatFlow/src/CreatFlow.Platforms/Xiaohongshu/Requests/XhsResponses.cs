namespace CreatFlow.Platforms.Xiaohongshu.Requests;

// ═══ 上传凭证 ═══

/// <summary>上传凭证响应（GET /api/media/v1/upload/creator/permit）。</summary>
internal sealed class XhsPermitResponse
{
    [JsonPropertyName("uploadTempPermits")]
    public List<XhsPermitItem>? UploadTempPermits { get; set; }
}

internal sealed class XhsPermitItem
{
    [JsonPropertyName("uploadAddr")]
    public string UploadAddr { get; set; } = "";

    [JsonPropertyName("secretId")]
    public string SecretId { get; set; } = "";

    [JsonPropertyName("secretKey")]
    public string SecretKey { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("bucket")]
    public string Bucket { get; set; } = "";

    [JsonPropertyName("region")]
    public string Region { get; set; } = "";

    [JsonPropertyName("fileIds")]
    public List<string>? FileIds { get; set; }

    [JsonPropertyName("cdnDomain")]
    public string CdnDomain { get; set; } = "";

    [JsonPropertyName("cloudType")]
    public string CloudType { get; set; } = "";
}

// ═══ 视频 ID ═══

/// <summary>获取视频 ID 响应。</summary>
internal sealed class XhsVideoIdResponse
{
    [JsonPropertyName("video_id")]
    public string VideoId { get; set; } = "";

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

// ═══ 转码 ═══

/// <summary>转码状态查询响应。</summary>
internal sealed class XhsTranscodeResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("progress")]
    public int Progress { get; set; }
}

// ═══ 笔记 ═══

/// <summary>笔记发布/更新响应（POST /web_api/sns/v2/note）。</summary>
internal sealed class XhsNoteResponse
{
    [JsonPropertyName("noteId")]
    public string? NoteId { get; set; }

    [JsonPropertyName("note_id")]
    public string? NoteId2 { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

/// <summary>笔记删除响应。</summary>
internal sealed class XhsDeleteNoteResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

// ═══ 视频列表 ═══

/// <summary>视频列表响应（galaxy API: /api/galaxy/v2/creator/note/user/posted）。</summary>
internal sealed class XhsVideoListResponse
{
    [JsonPropertyName("data")]
    public XhsVideoListData? Data { get; set; }
}

internal sealed class XhsVideoListData
{
    [JsonPropertyName("notes")]
    public List<XhsVideoItem>? Notes { get; set; }
}

/// <summary>视频作品条目。</summary>
public sealed class XhsVideoItem
{
    [JsonPropertyName("note_id")]
    public string NoteId { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("display_title")]
    public string DisplayTitle { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("likes")]
    public int Likes { get; set; }

    [JsonPropertyName("view_count")]
    public int ViewCount { get; set; }

    [JsonPropertyName("time")]
    public long Time { get; set; }
}

// ═══ 权限验证 ═══

/// <summary>权限验证响应。</summary>
internal sealed class XhsPermissionResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

// ═══ 合集列表 ═══

/// <summary>合集列表响应（POST /api/sns/v1/note/collection/pc/list_v2）。</summary>
internal sealed class XhsCollectionListResponse
{
    [JsonPropertyName("data")]
    public XhsCollectionListData? Data { get; set; }
}

internal sealed class XhsCollectionListData
{
    // 平台实际字段名是 collection_info_list（抓包 list_v2.json 实证）——
    // 曾按 collections 解析 → 永远 0 条（列表/按名收养全瞎）
    [JsonPropertyName("collection_info_list")]
    public List<XhsCollectionItem>? Collections { get; set; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }
}

/// <summary>合集条目。</summary>
public sealed class XhsCollectionItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("collected")]
    public bool Collected { get; set; }

    [JsonPropertyName("note_num")]
    public int NoteNum { get; set; }

    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    // paid 平台返回的是**数字** 0/1（抓包 examples/xiaohongshu/collection/list_v2.json 实证：
    // "paid": 0），同响应里 collected/has_more 才是真布尔。声明成裸 bool 会让整个列表解析
    // 抛 JsonException → 「同名合集查重」静默失效（按未建处理）→ 重建同名合集时重复创建。
    // 2026-10-14 用户实测日志抓到这条路径。挂灵活转换器：数字/布尔/字符串都吃。
    [JsonPropertyName("paid")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool Paid { get; set; }

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("view_num")]
    public int ViewNum { get; set; }

    [JsonPropertyName("total_episodes")]
    public int TotalEpisodes { get; set; }

    [JsonPropertyName("create_time")]
    public long CreateTime { get; set; }
}

// ═══ 合集详情 ═══

/// <summary>合集详情响应（GET /api/sns/v1/note/collection/pc/info）。</summary>
internal sealed class XhsCollectionInfoResponse
{
    [JsonPropertyName("data")]
    public XhsCollectionDetail? Data { get; set; }
}

/// <summary>合集详情。</summary>
public sealed class XhsCollectionDetail
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("note_num")]
    public int NoteNum { get; set; }

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("create_time")]
    public long CreateTime { get; set; }
}

// ═══ 合集创建/更新 ═══

/// <summary>
/// 合集创建/更新/删除响应。
/// 各接口信封不一致（2026-11-05 抓包）：create/update 用 <c>result</c>，
/// delete/sorting_note 用 <c>code</c>，两者都缺省为 0；失败时 HTTP 仍可能是 200，
/// 必须看 success/code/result，不能只看状态码（否则删除失败会被误报成成功）。
/// </summary>
internal sealed class XhsCollectionMutationResponse
{
    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("result")]
    public int Result { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    /// <summary>成功判定：success=true 且 code/result 都为 0（缺省字段按 0）。</summary>
    public bool IsOk => Success && Code == 0 && Result == 0;
}

// ═══ 添加/移除笔记 ═══

/// <summary>添加/移除笔记响应。</summary>
internal sealed class XhsNoteManagementResponse
{
    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }
}
