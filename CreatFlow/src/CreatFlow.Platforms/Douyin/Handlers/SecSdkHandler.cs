namespace CreatFlow.Platforms.Douyin.Handlers;

/// <summary>
/// 抖音安全 SDK 反爬请求头 Handler。
///
/// 对标 douyin-api 的 headers:
///   X-Secsdk-Csrf-Token: DOWNGRADE — 告诉安全 SDK 这是程序化请求，跳过 CSRF 严格校验
///
/// 不做成泛型（如通过浏览器 headers 列表）是为了保持简单和可追溯。
/// </summary>
public sealed class SecSdkHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // 安全 SDK CSRF 降级头 — douyin-api 所有 POST 请求都带这个
        if (request.Method == HttpMethod.Post)
        {
            request.Headers.TryAddWithoutValidation("X-Secsdk-Csrf-Token", "DOWNGRADE");
        }

        return base.SendAsync(request, ct);
    }
}
