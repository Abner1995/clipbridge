namespace CreatFlow.Platforms.Douyin;

using CreatFlow.Platforms.Core;
using Serilog;

/// <summary>
/// 抖音认证服务 — STS 凭证刷新、会话探活等。
/// </summary>
public sealed class DouyinAuthService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<DouyinAuthService>? _logger;

    public DouyinAuthService(
        PlatformOptions options,
        ILogger<DouyinAuthService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    private void LogDebug(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogDebug(message, args);
        else Log.Debug(message, args);
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

    /// <summary>CookieJar 模式 — 响应 Set-Cookie 自动回写。</summary>
    public async Task<DouyinStsToken?> RefreshStsAsync(
        CookieJar? cookieJar,
        string? msToken = null,
        string? aBogus = null,
        CancellationToken ct = default)
    {
        var result = await RefreshStsDetailedAsync(cookieJar, msToken, aBogus, ct);
        return result.Token;
    }

    /// <summary>
    /// CookieJar 模式（带失败原因）—— 上传凭证被拒时平台会给 <c>status_code</c>，
    /// 但把码吞掉只报「Cookie 可能已过期」，用户只会反复重登（2026-10-28 实测：
    /// 重登后仍 status_code=8，其实是账号被要求完成身份验证）。
    /// </summary>
    public async Task<DouyinStsResult> RefreshStsDetailedAsync(
        CookieJar? cookieJar,
        string? msToken = null,
        string? aBogus = null,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookieJar);

        var path = "/web/api/media/upload/auth/v5/";
        var qs = PlatformHttpClient.BuildQueryString(msToken: msToken, aBogus: aBogus);
        var url = $"{_options.DouyinCreatorHost}{path}?{qs}";

        LogDebug("RefreshSts → {Url}", url);

        var resp = await client.GetJsonAsync<DouyinAuthResponse>(url, ct);
        if (resp == null)
        {
            LogWarn("RefreshSts failed: 无响应体");
            return new DouyinStsResult(null, -1, null);
        }

        if (resp.StatusCode != 0 || string.IsNullOrWhiteSpace(resp.AuthJson))
        {
            LogWarn("RefreshSts failed: status_code={Code}, status_msg={Msg}",
                resp.StatusCode, string.IsNullOrWhiteSpace(resp.StatusMsg) ? "(无)" : resp.StatusMsg);
            return new DouyinStsResult(null, resp.StatusCode, resp.StatusMsg);
        }

        try
        {
            var auth = JsonSerializer.Deserialize<DouyinStsToken>(resp.AuthJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (auth is not null && string.IsNullOrWhiteSpace(auth.AccessKeyId))
            {
                LogWarn("RefreshSts: STS token parsed but AccessKeyId is empty");
                return new DouyinStsResult(null, resp.StatusCode, resp.StatusMsg);
            }
            return new DouyinStsResult(auth, 0, null);
        }
        catch (Exception ex)
        {
            LogWarn(ex, "RefreshSts: auth JSON 解析失败");
            return new DouyinStsResult(null, resp.StatusCode, resp.StatusMsg);
        }
    }

    /// <summary>字符串模式 — 向后兼容。</summary>
    public async Task<DouyinStsToken?> RefreshStsAsync(
        string cookie,
        string? msToken = null,
        string? aBogus = null,
        CancellationToken ct = default)
    {
        return await RefreshStsAsync(new CookieJar(cookie), msToken, aBogus, ct);
    }

    private PlatformHttpClient BuildClient(CookieJar? cookieJar)
    {
        var cookieHandler = cookieJar != null
            ? new Core.Handlers.CookieAuthHandler(cookieJar)
            : new Core.Handlers.CookieAuthHandler("");
        var loggingHandler = new Core.Handlers.LoggingHandler(null);
        cookieHandler.InnerHandler = loggingHandler;
        return new PlatformHttpClient(cookieHandler, _options, null);
    }
}
