using System.Net;
using System.Text.Json;
using CreatFlow.Application.Common.Interfaces;
using CreatFlow.Domain.Entities;
using CreatFlow.Shared;

namespace CreatFlow.Application.Common.Probes;

/// <summary>
/// 头条号 cookie 探活 — 带存档 Cookie 请求
/// <c>GET https://mp.toutiao.com/mp/agw/media/user_login_status_api</c>
/// （抓包 2026-11-05：页面每次进首页都会用它判登录态）：
///   - 200 + data.is_login=true → 有效
///   - data.is_login=false / 401 / 403 → 已过期
///   - 网络错误 / 响应不可解析 → 不变（不因瞬时失败判死）
/// </summary>
public class ToutiaoPlatformProber : IPlatformProber
{
    private readonly HttpClient _http;

    public ToutiaoPlatformProber(HttpClient http)
    {
        _http = http;
    }

    public async Task<(PlatformSessionStatus status, string result)> ProbeAsync(
        PlatformSession session, CancellationToken ct = default)
    {
        if (session.PlatformKey != PlatformNames.Toutiao)
            return (session.Status, "skipped");

        if (string.IsNullOrWhiteSpace(session.Cookie))
            return (PlatformSessionStatus.Expired, "no_cookie");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://mp.toutiao.com/mp/agw/media/user_login_status_api");
            request.Headers.Add("Cookie", session.Cookie);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36");
            request.Headers.Add("Referer", "https://mp.toutiao.com/profile_v4/index");
            request.Headers.Add("Accept", "application/json, text/plain, */*");

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (PlatformSessionStatus.Expired, "expired");
            if (!response.IsSuccessStatusCode)
                return (session.Status, "network_error");

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : -1;
            if (code != 0)
                return (session.Status, "bad_response");   // 风控/服务端异常：不判死

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return (session.Status, "bad_response");

            var isLogin = data.TryGetProperty("is_login", out var li)
                && li.ValueKind == JsonValueKind.True;

            return isLogin
                ? (PlatformSessionStatus.Active, "ok")
                : (PlatformSessionStatus.Expired, "not_login");
        }
        catch (HttpRequestException)
        {
            return (session.Status, "network_error");
        }
        catch (TaskCanceledException)
        {
            return (session.Status, "timeout");
        }
        catch (JsonException)
        {
            return (session.Status, "bad_response");
        }
    }
}
