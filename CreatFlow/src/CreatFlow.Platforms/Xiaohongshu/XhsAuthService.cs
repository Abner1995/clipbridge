using CreatFlow.Platforms.Xiaohongshu.Requests;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书认证服务 — 会话探活。
///
/// 对标 Python fangai/xiaohongshu/api.py 的 validate_permission()。
/// POST /web_api/sns/capa/postgw/permission/validate
/// </summary>
public sealed class XhsAuthService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<XhsAuthService>? _logger;

    public XhsAuthService(PlatformOptions options, ILogger<XhsAuthService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 验证当前 Cookie 是否有效（轻量探活）。
    /// </summary>
    /// <param name="cookie">Cookie 字符串</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>true 表示会话有效</returns>
    public async Task<bool> ProbeAsync(string cookie, CancellationToken ct = default)
    {
        try
        {
            using var client = new XhsApiClient(cookie, _options,
                _logger as ILogger<PlatformHttpClient>);
            var resp = await client.PostEdithRawAsync(
                "/web_api/sns/capa/postgw/permission/validate", new { }, ct);
            return resp.TryGetProperty("success", out var s) && s.GetBoolean();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "XHS permission/validate probe failed");
            return false; // 探活失败 = 会话过期
        }
    }
}
