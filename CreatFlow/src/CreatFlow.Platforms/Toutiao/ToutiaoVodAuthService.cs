namespace CreatFlow.Platforms.Toutiao;

using CreatFlow.Platforms.Toutiao.Requests;

/// <summary>
/// 头条视频上传凭证（阶段 5.3）—— 两个端点按视频方向二选一（接口说明书 §4.2.1）：
///
///   · 横版：<c>GET /xigua/api/upload/getAuthKey/?params={"type":"video",…,"useStsToken":true}</c>
///     → <c>data.spaceName</c>（middlevideo_toutiao）+ <c>data.uploadToken.{AccessKeyId,SecretAccessKey,SessionToken}</c>；
///   · 竖版：<c>GET /toutiao/upload/auth_token/v1?aid=1231&amp;device_platform=web&amp;type=video&amp;upload_source=10020004</c>
///     → 顶层 <c>space_name</c>（short_video_toutiao）+ <c>access_key_id/secret_access_key/session_token</c>。
///
/// 拿到的 STS 直接喂给 <see cref="ToutiaoUploadService"/>（ByteDance VOD Apply/Commit 的 SigV4）。
/// </summary>
public sealed class ToutiaoVodAuthService
{
    /// <summary>横版视频凭证路径（抓包 <c>video_upload_get_auth_key.json</c>）</summary>
    public const string AuthKeyPath = "/xigua/api/upload/getAuthKey/";

    /// <summary>竖版小视频凭证路径（抓包 <c>auth_token</c>）</summary>
    public const string AuthTokenPath = "/toutiao/upload/auth_token/v1";

    /// <summary>视频上传页 Referer（两个凭证接口同款）</summary>
    public const string UploadReferer = "https://mp.toutiao.com/profile_v4/xigua/upload-video";

    /// <summary>竖版 auth_token 的 upload_source（抓包原文）</summary>
    public const string VerticalUploadSource = "10020004";

    private readonly PlatformOptions _options;

    public ToutiaoVodAuthService(PlatformOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 当前登录用户的 <c>user_id</c>（VOD 的 <c>user_id</c> query 与 TOS 的
    /// <c>X-Storage-U</c> 都用它；**不是** page 的 media.id）。
    /// </summary>
    public async Task<string?> GetUserIdAsync(
        string cookie, string? csrfToken, CancellationToken ct = default)
    {
        using var client = new ToutiaoApiClient(cookie, _options, csrfToken, UploadReferer);
        return await client.GetUserIdAsync(ct);
    }

    /// <summary>
    /// 取视频上传 STS。失败（业务码非 0 / 缺字段）→ null，由调用方给用户可读文案。
    /// </summary>
    public async Task<ToutiaoVodCredentials?> GetAsync(
        string cookie, string? csrfToken, bool portrait, CancellationToken ct = default)
    {
        using var client = new ToutiaoApiClient(cookie, _options, csrfToken, UploadReferer);

        if (portrait)
        {
            var url = _options.ToutiaoHost.TrimEnd('/') + AuthTokenPath
                + $"?aid={Uri.EscapeDataString(_options.ToutiaoAppId)}"
                + $"&device_platform=web&type=video&upload_source={VerticalUploadSource}";
            var resp = await client.GetJsonAsync<ToutiaoAuthTokenResponse>(url, ct);
            if (resp is null || resp.Code != 0
                || string.IsNullOrWhiteSpace(resp.AccessKeyId)
                || string.IsNullOrWhiteSpace(resp.SecretAccessKey)
                || string.IsNullOrWhiteSpace(resp.SessionToken))
            {
                return null;
            }

            return new ToutiaoVodCredentials(
                resp.AccessKeyId, resp.SecretAccessKey, resp.SessionToken,
                string.IsNullOrWhiteSpace(resp.SpaceName)
                    ? ToutiaoSpaceName.Vertical
                    : resp.SpaceName);
        }

        const string paramsJson =
            """{"type":"video","column":false,"ugc":false,"useImageX":true,"useStsToken":true}""";
        var authUrl = _options.ToutiaoHost.TrimEnd('/') + AuthKeyPath
            + "?params=" + Uri.EscapeDataString(paramsJson);

        var auth = await client.GetJsonAsync<ToutiaoVideoAuthResponse>(authUrl, ct);
        var token = auth?.Data?.UploadToken;
        if (auth?.Status != 0 || token is null
            || string.IsNullOrWhiteSpace(token.AccessKeyId)
            || string.IsNullOrWhiteSpace(token.SecretAccessKey)
            || string.IsNullOrWhiteSpace(token.SessionToken))
        {
            return null;
        }

        return new ToutiaoVodCredentials(
            token.AccessKeyId, token.SecretAccessKey, token.SessionToken,
            string.IsNullOrWhiteSpace(auth.Data?.SpaceName)
                ? ToutiaoSpaceName.Horizontal
                : auth.Data!.SpaceName!);
    }
}

/// <summary>头条视频上传 STS（含平台给的 SpaceName）。</summary>
public sealed record ToutiaoVodCredentials(
    string AccessKeyId,
    string SecretAccessKey,
    string SessionToken,
    string SpaceName);
