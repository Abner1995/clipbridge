using System.Net;
using System.Text.Json;
using CreatFlow.Application.Common.Interfaces;
using CreatFlow.Domain.Entities;
using CreatFlow.Shared;

namespace CreatFlow.Application.Common.Probes;

/// <summary>
/// B站创作中心 cookie 探活 — 带存档 Cookie 请求
/// <c>GET https://api.bilibili.com/x/web-interface/nav</c>（页面自己取用户信息也是这条）：
///   - 200 + code=0 + data.isLogin=true  → 有效（顺手把本地预估过期状态纠回 Active）
///   - code=-101（账号未登录）/ 401 / 403 → 已过期
///   - 网络错误 / 响应不可解析 → 不变（不因瞬时失败判死）
/// </summary>
public class BilibiliPlatformProber : IPlatformProber
{
    private readonly HttpClient _http;

    public BilibiliPlatformProber(HttpClient http)
    {
        _http = http;
    }

    public async Task<(PlatformSessionStatus status, string result)> ProbeAsync(
        PlatformSession session, CancellationToken ct = default)
    {
        if (session.PlatformKey != PlatformNames.Bilibili)
            return (session.Status, "skipped");

        if (string.IsNullOrWhiteSpace(session.Cookie))
            return (PlatformSessionStatus.Expired, "no_cookie");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.bilibili.com/x/web-interface/nav");
            request.Headers.Add("Cookie", session.Cookie);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36");
            request.Headers.Add("Referer", "https://member.bilibili.com/");
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
            // -101 = 账号未登录（真正的过期）。其它非 0（风控 -352 / 服务端异常）不判死，
            // 否则一次风控就把有效会话误标成已过期。
            if (code == -101)
                return (PlatformSessionStatus.Expired, "expired");
            if (code != 0)
                return (session.Status, "bad_response");

            var isLogin = root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("isLogin", out var li)
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
