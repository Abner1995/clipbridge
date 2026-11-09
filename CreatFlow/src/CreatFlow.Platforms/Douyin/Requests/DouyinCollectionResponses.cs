namespace CreatFlow.Platforms.Douyin.Requests;

// ═══ List Response ═══
// 对标: GET /web/api/mix/list/ 的 JSON 响应
// 参考: examples/douyin/collection/collection.json

/// <summary>
/// 抖音合集列表 API 响应。
/// </summary>
public sealed class DouyinListResponse
{
    /// <summary>合集列表。实际 API 字段名是 mix_list。</summary>
    [JsonPropertyName("mix_list")]
    public List<DouyinMixInfo>? MixList { get; set; }

    /// <summary>是否有更多。抖音 API 可能返回 0/1 (int) 或 true/false (bool)。</summary>
    [JsonPropertyName("has_more")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool HasMore { get; set; }

    [JsonPropertyName("cursor")]
    public long Cursor { get; set; }

    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    /// <summary>扩展数据字段可能是数字或字符串</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// 抖音合集信息 — mix_list 数组中的单项。
/// 对标: collection.json 中 mix_list[] 的结构
/// </summary>
public sealed class DouyinMixInfo
{
    [JsonPropertyName("mix_id")]
    public string MixId { get; set; } = "";

    [JsonPropertyName("mix_name")]
    public string MixName { get; set; } = "";

    /// <summary>合集简介。实际字段名是 desc，不是 mix_desc。</summary>
    [JsonPropertyName("desc")]
    public string? Desc { get; set; }

    [JsonPropertyName("cover_url")]
    public JsonElement? CoverUrl { get; set; }

    /// <summary>合集状态。实际结构是 {"is_collected":0,"status":2} 嵌套对象。</summary>
    [JsonPropertyName("status")]
    public JsonElement StatusElement { get; set; }

    /// <summary>解析后的状态码（从 status.status 或 status 直接取）。</summary>
    public int StatusValue => ParseStatus(StatusElement);

    /// <summary>创建时间（Unix 秒）。</summary>
    [JsonPropertyName("create_time")]
    public long CreateTime { get; set; }

    /// <summary>统计信息（包含视频数等）。</summary>
    [JsonPropertyName("statis")]
    public DouyinMixStatis? Statis { get; set; }

    /// <summary>合集内视频数量。</summary>
    public int VideoCount => Statis?.UpdatedToEpisode ?? 0;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>解析 status 字段（嵌套对象 {"status":2} 或直接int）。</summary>
    private static int ParseStatus(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.Number)
            return elem.GetInt32();
        if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out var n))
            return n;
        if (elem.ValueKind == JsonValueKind.Object)
        {
            if (elem.TryGetProperty("status", out var sv))
                return sv.GetInt32();
        }
        return 0;
    }
}

/// <summary>
/// 抖音合集统计信息 — mix_list[].statis
/// </summary>
public sealed class DouyinMixStatis
{
    [JsonPropertyName("collect_vv")]
    public int CollectVv { get; set; }

    [JsonPropertyName("current_episode")]
    public int CurrentEpisode { get; set; }

    [JsonPropertyName("play_vv")]
    public int PlayVv { get; set; }

    [JsonPropertyName("updated_to_episode")]
    public int UpdatedToEpisode { get; set; }
}

// ═══ Create Response ═══
// 对标: POST /web/api/mix/create 的 JSON 响应
// 参考: examples/douyin/collection/createcollection.json

/// <summary>
/// 抖音创建/编辑合集 API 响应。
/// </summary>
public sealed class DouyinCreateResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonPropertyName("mix_id")]
    public string? MixId { get; set; }

    [JsonPropertyName("status_msg")]
    public string? StatusMsg { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

// ═══ Destroy Response ═══
// 对标: POST /web/api/mix/destroy 的 JSON 响应

/// <summary>
/// 抖音删除合集 API 响应。
/// </summary>
public sealed class DouyinDestroyResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonPropertyName("status_msg")]
    public string? StatusMsg { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

// ═══ ImageX Upload Responses ═══
// 对标: GET https://imagex.bytedanceapi.com/?Action=ApplyImageUpload 的 JSON 响应

/// <summary>
/// ImageX ApplyImageUpload 响应 — 获取封面图上传凭证。
/// </summary>
public sealed class DouyinApplyImageUploadResponse
{
    [JsonPropertyName("Result")]
    public DouyinImageUploadResult? Result { get; set; }
}

/// <summary>
/// ImageX 上传凭证结果。
/// </summary>
public sealed class DouyinImageUploadResult
{
    [JsonPropertyName("UploadAddress")]
    public DouyinUploadAddress? UploadAddress { get; set; }
}

/// <summary>
/// ImageX/TOS 上传地址信息。
/// 对标 ApplyImageUpload.json: Result.UploadAddress
/// </summary>
public sealed class DouyinUploadAddress
{
    /// <summary>存储信息列表（含 StoreUri + Auth + UploadID）</summary>
    [JsonPropertyName("StoreInfos")]
    public List<DouyinStoreInfo>? StoreInfos { get; set; }

    /// <summary>上传域名列表</summary>
    [JsonPropertyName("UploadHosts")]
    public List<string>? UploadHosts { get; set; }

    /// <summary>上传 SessionKey（用于 CommitImageUpload）</summary>
    [JsonPropertyName("SessionKey")]
    public string? SessionKey { get; set; }
}

/// <summary>
/// StoreInfos 数组中的单项 — 包含 TOS 路径和认证 token。
/// </summary>
public sealed class DouyinStoreInfo
{
    /// <summary>TOS 存储路径，如 tos-cn-i-jm8ajry58r/xxx</summary>
    [JsonPropertyName("StoreUri")]
    public string? StoreUri { get; set; }

    /// <summary>TOS 上传 Authorization 头</summary>
    [JsonPropertyName("Auth")]
    public string? Auth { get; set; }

    [JsonPropertyName("UploadID")]
    public string? UploadId { get; set; }
}

/// <summary>
/// ImageX CommitImageUpload 响应 — 确认封面图上传完成。
/// </summary>
public sealed class DouyinCommitImageUploadResponse
{
    [JsonPropertyName("Result")]
    public DouyinCommitResult? Result { get; set; }
}

/// <summary>
/// ImageX Commit 结果。
/// 对标 CommitImageUpload.json: Result.Results[0].Uri
/// </summary>
public sealed class DouyinCommitResult
{
    [JsonPropertyName("Results")]
    public List<DouyinCommitItem>? Results { get; set; }
}

/// <summary>
/// Commit 结果中的单项。
/// </summary>
public sealed class DouyinCommitItem
{
    [JsonPropertyName("Uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("UriStatus")]
    public int UriStatus { get; set; }
}

// ═══ JSON Converters ═══

/// <summary>
/// 灵活布尔转换器 — 处理抖音 API 中同一字段可能返回 0/1 (int)、
/// true/false (bool)、或 "0"/"1"/"true"/"false" (string) 的情况。
/// </summary>
public sealed class FlexibleBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number => reader.GetInt32() != 0,
            JsonTokenType.String => reader.GetString()?.ToLowerInvariant() switch
            {
                "1" or "true" => true,
                _ => false
            },
            _ => false
        };
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        writer.WriteBooleanValue(value);
    }
}

// ═══ STS Auth Response (GET /web/api/media/upload/auth/v5/) ═══

/// <summary>
/// 抖音 STS 凭证 — 通过 Cookie + msToken + a_bogus 从 auth/v5 接口获取。
/// 对标 Python upload_publish_douyin.py refresh_sts()。
/// </summary>
public sealed record DouyinStsToken(
    string AccessKeyId,
    string SecretAccessKey,
    string SessionToken
);

/// <summary>
/// GET /web/api/media/upload/auth/v5/ 的 JSON 响应。
/// auth 字段本身是一个 JSON 字符串，需要二次反序列化。
/// </summary>
public sealed class DouyinAuthResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    /// <summary>失败时平台给的人话说明（可能为空）—— 只报状态码用户没法判断下一步</summary>
    [JsonPropertyName("status_msg")]
    public string? StatusMsg { get; set; }

    [JsonPropertyName("auth")]
    public string? AuthJson { get; set; }  // JSON 字符串，需二次解析
}

/// <summary>
/// STS 刷新结果 —— 成功时 <see cref="Token"/> 非空；失败时带平台状态码/说明，
/// 让上层能给出准确提示（只报「Cookie 可能已过期」会误导用户反复重登）。
/// </summary>
public sealed record DouyinStsResult(DouyinStsToken? Token, int StatusCode, string? StatusMsg)
{
    public bool Ok => Token is not null;
}

// ═══ Detail Response ═══
// 对标: GET /aweme/v1/mix/detail/ 的 JSON 响应
// 参考: examples/douyin/collection/collectiondetail.json

/// <summary>
/// 抖音合集详情 API 响应。
/// 对标: collectiondetail.json
/// </summary>
public sealed class DouyinDetailResponse
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonPropertyName("status_msg")]
    public string? StatusMsg { get; set; }

    [JsonPropertyName("mix_info")]
    public DouyinMixInfo? MixInfo { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// 抖音合集详情结果 — 从 Detail API 提取的字段。
/// </summary>
public sealed record DouyinDetailResult(
    string MixId,
    string MixName,
    string? Description,
    string? CoverUri,           // TOS object key（如 tos-cn-i-dy/xxx）
    string? CoverCdnUrl,        // 封面图 CDN URL（url_list[0]），可直接访问
    int Status,
    string StatusText,
    int VideoCount
);
