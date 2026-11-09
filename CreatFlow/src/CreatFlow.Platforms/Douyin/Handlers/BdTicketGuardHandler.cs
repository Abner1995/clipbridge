namespace CreatFlow.Platforms.Douyin.Handlers;

using System.Text.Json;
using CreatFlow.Platforms.Core;

/// <summary>
/// 抖音 Bd-Ticket-Guard 反爬 Handler。
///
/// 两种模式：
///   1. BdTicketSigner 模式（优先）— ECDSA 签名 + 响应头 server_data 处理
///   2. Cookie 提取模式（兜底）— 从 cookie 提取 bd_ticket_guard_client_data_v2 转发
/// </summary>
public sealed class BdTicketGuardHandler : DelegatingHandler
{
    private readonly Func<string> _cookieProvider;
    private readonly BdTicketSigner? _signer;
    private readonly CookieJar? _jar;

    /// <summary>BdTicketSigner + CookieJar 模式（推荐）— 完整 ECDSA 签名循环。</summary>
    public BdTicketGuardHandler(Func<string> cookieProvider, BdTicketSigner? signer = null, CookieJar? jar = null)
    {
        _cookieProvider = cookieProvider;
        _signer = signer;
        _jar = jar;
    }

    /// <summary>静态 cookie + signer 模式。</summary>
    public BdTicketGuardHandler(string cookie, BdTicketSigner? signer = null, CookieJar? jar = null)
        : this(() => cookie, signer, jar) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // ── 请求前：应用签名头 ──
        if (_signer is { IsValid: true })
        {
            // ECDSA 签名模式
            _signer.ApplyHeaders(request);

            // 也转发原始 client_data（兼容性）
            var cookie = _cookieProvider();
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                var clientDataV2 = ExtractCookieValue(cookie, "bd_ticket_guard_client_data_v2");
                if (!string.IsNullOrWhiteSpace(clientDataV2))
                    request.Headers.TryAddWithoutValidation("bd-ticket-guard-client-data", clientDataV2);
            }
        }
        else
        {
            // 兜底：从 cookie 提取转发
            var cookie = _cookieProvider();
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                var clientDataV2 = ExtractCookieValue(cookie, "bd_ticket_guard_client_data_v2");
                if (!string.IsNullOrWhiteSpace(clientDataV2))
                {
                    request.Headers.TryAddWithoutValidation("bd-ticket-guard-client-data", clientDataV2);

                    var reeKey = DecodeReePublicKey(clientDataV2);
                    if (!string.IsNullOrWhiteSpace(reeKey))
                        request.Headers.TryAddWithoutValidation("bd-ticket-guard-ree-public-key", reeKey);

                    request.Headers.TryAddWithoutValidation("bd-ticket-guard-version", "2");
                    request.Headers.TryAddWithoutValidation("bd-ticket-guard-web-version", "2");
                }
            }
        }

        // ── 发请求 ──
        var response = await base.SendAsync(request, ct);

        // ── 响应后：签名器处理 server_data ──
        _signer?.ProcessResponse(response, _jar);

        return response;
    }

    private static string? ExtractCookieValue(string cookie, string key)
    {
        var prefix = key + "=";
        var idx = cookie.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + prefix.Length;
        var end = cookie.IndexOf(';', start);
        if (end < 0) end = cookie.Length;
        return cookie[start..end].Trim();
    }

    private static string? DecodeReePublicKey(string base64Value)
    {
        try
        {
            base64Value = Uri.UnescapeDataString(base64Value);
            var missing = base64Value.Length % 4;
            if (missing > 0) base64Value += new string('=', 4 - missing);
            var bytes = Convert.FromBase64String(base64Value);
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ree_public_key", out var reeKey))
            {
                var key = reeKey.GetString();
                if (!string.IsNullOrWhiteSpace(key)) return key;
            }
        }
        catch { }
        return null;
    }
}
