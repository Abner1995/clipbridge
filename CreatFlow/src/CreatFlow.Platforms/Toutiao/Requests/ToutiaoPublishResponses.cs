namespace CreatFlow.Platforms.Toutiao.Requests;

/// <summary>
/// 横版视频上传凭证（GET /xigua/api/upload/getAuthKey/?params={"type":"video",…}）。
/// 响应 <c>data.uploadToken</c> 是**对象**（图片的 image_temp 是字符串 token，别混）。
/// </summary>
internal sealed class ToutiaoVideoAuthResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")] public ToutiaoVideoAuthData? Data { get; set; }
}

internal sealed class ToutiaoVideoAuthData
{
    [JsonPropertyName("spaceName")] public string? SpaceName { get; set; }
    [JsonPropertyName("accessKey")] public string? AccessKey { get; set; }
    [JsonPropertyName("uploadToken")] public ToutiaoVideoUploadToken? UploadToken { get; set; }
}

internal sealed class ToutiaoVideoUploadToken
{
    [JsonPropertyName("AccessKeyId")] public string? AccessKeyId { get; set; }
    [JsonPropertyName("SecretAccessKey")] public string? SecretAccessKey { get; set; }
    [JsonPropertyName("SessionToken")] public string? SessionToken { get; set; }
    [JsonPropertyName("ExpiredTime")] public string? ExpiredTime { get; set; }
}

/// <summary>
/// 竖版小视频上传凭证（GET /toutiao/upload/auth_token/v1?aid=1231&amp;type=video&amp;upload_source=10020004）。
/// 字段在**顶层**（不是 data 壳里），space_name = short_video_toutiao。
/// </summary>
internal sealed class ToutiaoAuthTokenResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("space_name")] public string? SpaceName { get; set; }
    [JsonPropertyName("access_key_id")] public string? AccessKeyId { get; set; }
    [JsonPropertyName("secret_access_key")] public string? SecretAccessKey { get; set; }
    [JsonPropertyName("session_token")] public string? SessionToken { get; set; }
    [JsonPropertyName("current_time_str")] public string? CurrentTimeStr { get; set; }
    [JsonPropertyName("expire_time_str")] public string? ExpireTimeStr { get; set; }
}
