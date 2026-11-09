using System.Security.Cryptography;
using Serilog;

namespace CreatFlow.Platforms.Xiaohongshu.Handlers;

/// <summary>
/// 小红书签名 Handler — 在每个请求发出前注入 x-s / x-s-common / x-t 等反爬 header。
///
/// 签名算法（来自 library-launcher.js + sign.py）：
///   x-t: 毫秒级时间戳
///   x-b3-traceid: 16 位 hex UUID
///   x-xray-traceid: 32 位 hex UUID
///   x-s: XYS_ + customBase64(JSON({x0..x4})) — x3 需要 mnsv2(url+body, md5, md5)
///   x-s-common: customBase64(JSON({s0..s1, x0..x12})) — 设备指纹
///
/// ⚠️ mnsv2 (x3) 无法纯 C# 实现，需要外部提供签名委托（WebView2 / node bridge）。
/// 未设置 delegate 时，x-s/x-s-common 不注入（会 403 但允许手动模式）。
/// </summary>
internal sealed class XhsSignHandler : DelegatingHandler
{
    /// <summary>
    /// 签名委托 — App 层可注入 WebView2 或 node bridge 签名。
    ///
    /// 参数:
    ///   urlPath: API 路径（不含域名），如 "/api/sns/v1/note/collection/pc/list_v2"
    ///   bodyJson: 请求体 JSON 字符串（可为 null）
    ///   sessionId: 发起请求的会话 ID（PlatformOptions.SessionId 透传；可为 null）
    ///              —— 签名器据此优先选同账号 WebView2 档案
    ///   ct: 取消令牌
    ///
    /// 返回: XhsSignHeaders 或 null 表示签名不可用
    /// </summary>
    public static Func<string, string?, Guid?, CancellationToken, Task<XhsSignHeaders?>>? SignerDelegate { get; set; }

    /// <summary>发起方会话 ID（PlatformOptions 注入；签名器据此选档案）</summary>
    private readonly Guid? _sessionId;

    public XhsSignHandler(Guid? sessionId = null) => _sessionId = sessionId;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // x-t: 毫秒时间戳（始终注入）
        var xT = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        request.Headers.TryAddWithoutValidation("x-t", xT);

        // x-b3-traceid: 16 位 hex
        request.Headers.TryAddWithoutValidation("x-b3-traceid", Guid.NewGuid().ToString("N")[..16]);

        // x-xray-traceid: 32 位 hex
        request.Headers.TryAddWithoutValidation("x-xray-traceid", Guid.NewGuid().ToString("N"));

        // x-s / x-s-common: 需要外部签名委托
        if (SignerDelegate is not null && request.RequestUri is not null)
        {
            try
            {
                var path = request.RequestUri.PathAndQuery;
                string? bodyJson = null;
                if (request.Content is not null)
                {
                    var bodyBytes = await request.Content.ReadAsByteArrayAsync(ct);
                    bodyJson = Encoding.UTF8.GetString(bodyBytes);
                }

                var result = await SignerDelegate(path, bodyJson, _sessionId, ct);
                if (result is not null)
                {
                    var xS = result.XS;
                    var xSCommon = result.XSCommon;
                    var sigXT = result.XT;

                    if (!string.IsNullOrWhiteSpace(xS))
                        request.Headers.TryAddWithoutValidation("x-s", xS);
                    if (!string.IsNullOrWhiteSpace(xSCommon))
                        request.Headers.TryAddWithoutValidation("x-s-common", xSCommon);
                    // Update x-t if signer returned a specific one
                    if (!string.IsNullOrWhiteSpace(sigXT))
                    {
                        request.Headers.Remove("x-t");
                        request.Headers.TryAddWithoutValidation("x-t", sigXT);
                    }
                }
                else
                {
                    // Signer returned null — x-s will be missing, XHS may return 406 for APIs that require it
                    Serilog.Log.Warning("[XhsSign] Signer returned null for {Path} — " +
                        "request will be sent without x-s header (may cause 406 if x-s is required)", path);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[XhsSign] Sign failed for {Path}: {Msg} — " +
                    "request will be sent without x-s header", request.RequestUri.PathAndQuery, ex.Message);
            }
        }
        else if (request.RequestUri is not null)
        {
            // Signer delegate not registered — App layer didn't call XhsPlatform.SetSigner()
            Serilog.Log.Warning("[XhsSign] No signer registered for {Path} — " +
                "request will be sent without x-s header (likely 406)", request.RequestUri.PathAndQuery);
        }

        return await base.SendAsync(request, ct);
    }
}
