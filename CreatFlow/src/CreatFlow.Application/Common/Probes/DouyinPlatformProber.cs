using System.Net;
using CreatFlow.Application.Common.Interfaces;
using CreatFlow.Domain.Entities;
using CreatFlow.Shared;

namespace CreatFlow.Application.Common.Probes;

/// <summary>
/// Douyin cookie probe — requests creator.douyin.com with stored cookie,
/// checks if the server redirects to login (cookie expired) or returns the SPA (valid).
///
/// Strategy:
///   GET https://creator.douyin.com/ (with Cookie header only, no msToken/a_bogus)
///   - 302 → login redirect → EXPIRED
///   - 200 → SPA HTML returned → ACTIVE
///   - other → network error → no change (don't mark expired on transient failures)
/// </summary>
public class DouyinPlatformProber : IPlatformProber
{
    private readonly HttpClient _http;

    public DouyinPlatformProber(HttpClient http)
    {
        _http = http;
    }

    public async Task<(PlatformSessionStatus status, string result)> ProbeAsync(
        PlatformSession session, CancellationToken ct = default)
    {
        if (session.PlatformKey != PlatformNames.Douyin)
            return (session.Status, "skipped");

        if (string.IsNullOrWhiteSpace(session.Cookie))
            return (PlatformSessionStatus.Expired, "no_cookie");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://creator.douyin.com/");

            request.Headers.Add("Cookie", session.Cookie);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36");
            request.Headers.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            // Don't auto-follow redirects — we want to see the 302 ourselves
            using var response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);

            // If redirected to login → expired
            if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Moved)
            {
                var location = response.Headers.Location?.ToString() ?? "";
                if (location.Contains("login") || location.Contains("passport")
                    || location.Contains("signin"))
                {
                    return (PlatformSessionStatus.Expired, "expired");
                }
            }

            // 401/403 → explicitly rejected
            if (response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden)
            {
                return (PlatformSessionStatus.Expired, "expired");
            }

            // 200 → SPA returned, cookie valid
            // Even if the body is a login page (some edge case), check response URI
            var finalUri = response.RequestMessage?.RequestUri?.ToString() ?? "";
            if (finalUri.Contains("login") || finalUri.Contains("passport"))
                return (PlatformSessionStatus.Expired, "expired");

            return (session.Status, "ok"); // keep current status
        }
        catch (HttpRequestException)
        {
            // Network error — don't change status, don't penalize
            return (session.Status, "network_error");
        }
        catch (TaskCanceledException)
        {
            return (session.Status, "timeout");
        }
    }
}
