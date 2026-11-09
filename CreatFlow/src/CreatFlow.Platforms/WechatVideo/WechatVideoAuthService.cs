namespace CreatFlow.Platforms.WechatVideo;

using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.WechatVideo.Requests;
using Serilog;

/// <summary>
/// 微信视频号认证服务 — 通过 Cookie + device_id 获取上传所需的 authKey、uin 等。
///
/// 对标 Python fangai/wechatvideo/auth.py 的 create_session。
///
/// 端点: POST /cgi-bin/mmfinderassistant-bin/helper/helper_upload_params
/// </summary>
public sealed class WechatVideoAuthService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<WechatVideoAuthService>? _logger;

    public WechatVideoAuthService(
        PlatformOptions options,
        ILogger<WechatVideoAuthService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    private void LogInfo(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogInformation(message, args);
        else Log.Information(message, args);
    }

    private void LogWarn(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogWarning(message, args);
        else Log.Warning(message, args);
    }

    private void LogWarn(Exception ex, string message, params object[] args)
    {
        if (_logger is not null) _logger.LogWarning(ex, message, args);
        else Log.Warning(ex, message, args);
    }

    private void LogDebug(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogDebug(message, args);
        else Log.Debug(message, args);
    }

    // ═══ Public API ═══

    /// <summary>
    /// 通过 Cookie + device_id 获取上传凭证。
    ///
    /// 对标 Python: create_session(cookie, device_id)
    /// </summary>
    /// <param name="cookie">登录 channels.weixin.qq.com 后的完整 Cookie 字符串</param>
    /// <param name="deviceId">finger-print-device-id header 值</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>上传凭证（authKey, uin, appType, fileTypes），失败返回 null</returns>
    public async Task<WechatVideoAuthResult?> GetUploadParamsAsync(
        string cookie,
        string deviceId,
        CancellationToken ct = default)
    {
        var path = "/cgi-bin/mmfinderassistant-bin/helper/helper_upload_params";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var aid = Guid.NewGuid().ToString("N");
        var rid = Guid.NewGuid().ToString("N");

        var qs = string.Join("&", new Dictionary<string, string>
        {
            ["_aid"] = aid,
            ["_rid"] = rid,
            ["_pageUrl"] = "https://channels.weixin.qq.com/micro/content/post/create",
        }.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        var url = $"{_options.WechatVideoApiHost}{path}?{qs}";

        var body = new Dictionary<string, object?>
        {
            ["timestamp"] = now.ToString(),
            ["_log_finder_uin"] = "",
            ["_log_finder_id"] = "",
            ["rawKeyBuff"] = null,
            ["pluginSessionId"] = null,
            ["scene"] = 7,
            ["reqScene"] = 7,
        };

        LogDebug("[WechatVideo] helper_upload_params → {Url}", url);

        // Handler 管道: WechatVideoHeaders → CookieAuth → Logging → HTTP
        // WechatVideoHeadersHandler 注入 finger-print-device-id（必须，否则 401）
        var wxHandler = new WechatVideoHeadersHandler(deviceId);
        var cookieHandler = new Core.Handlers.CookieAuthHandler(cookie);
        var loggingHandler = new Core.Handlers.LoggingHandler(null);
        wxHandler.InnerHandler = cookieHandler;
        cookieHandler.InnerHandler = loggingHandler;
        using var client = new PlatformHttpClient(wxHandler, _options, null,
            origin: "https://channels.weixin.qq.com",
            referer: "https://channels.weixin.qq.com/platform/post/create");

        try
        {
            var json = await client.PostJsonAsync(url, body, ct);
            var resp = JsonSerializer.Deserialize<UploadParamsResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (resp?.ErrCode != 0 || resp?.Data == null)
            {
                LogWarn("[WechatVideo] helper_upload_params failed: errCode={Code}, errMsg={Msg}",
                    resp?.ErrCode, resp?.ErrMsg);
                return null;
            }

            var data = resp.Data;
            var result = new WechatVideoAuthResult(
                Uin: data.Uin ?? "",
                AppType: data.AppType,
                AuthKey: data.AuthKey ?? "",
                Scene: data.Scene,
                VideoFileType: data.VideoFileType,
                PictureFileType: data.PictureFileType,
                ThumbFileType: data.ThumbFileType,
                MusicType: data.MusicType,
                PagFileType: data.PagFileType,
                ZipType: data.ZipType,
                LiveAppType: data.LiveAppType
            );

            LogInfo("[WechatVideo] helper_upload_params OK: uin={Uin}", result.Uin);
            return result;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 用户暂停/删除选中：取消不是「拿不到上传凭证」
            throw new OperationCanceledException(ct);
        }
        catch (HttpRequestException ex)
        {
            LogWarn(ex, "[WechatVideo] helper_upload_params HTTP error");
            return null;
        }
    }
}

/// <summary>
/// 微信视频号上传凭证 — 从 helper_upload_params 接口获取。
/// </summary>
public sealed record WechatVideoAuthResult(
    string Uin,
    int AppType,
    string AuthKey,
    int Scene,
    int VideoFileType,
    int PictureFileType,
    int ThumbFileType,
    int MusicType,
    int PagFileType,
    int ZipType,
    int LiveAppType
);
