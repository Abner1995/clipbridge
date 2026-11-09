namespace CreatFlow.Platforms.Kuaishou.Handlers;

/// <summary>
/// 快手平台参数注入 — 在每个请求的 URL 上附加 kpn / subBiz / kpf / api_ph。
///
/// 对应浏览器的请求 URL：
///   ?kpn=kuaishou_cp&subBiz=CP%2FCREATOR_PLATFORM&kpf=PC_WEB&kuaishou.web.cp.api_ph=...
///
/// ⚠️ 此 handler 必须在 KuaishouSigHandler 之前执行，
/// 因为签名的 MD5 计算包含这些 query params。
/// </summary>
internal sealed class KuaishouQueryHandler : DelegatingHandler
{
    private readonly string _apiPh;

    private const string PlatformParams = "kpn=kuaishou_cp&subBiz=CP%2FCREATOR_PLATFORM&kpf=PC_WEB";

    public KuaishouQueryHandler(string apiPh)
    {
        _apiPh = apiPh;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is not null)
        {
            var uri = request.RequestUri.ToString();
            // 避免重复追加（例如重定向后）
            if (!uri.Contains("kpn=kuaishou_cp", StringComparison.Ordinal))
            {
                // CRITICAL: kpn/subBiz/kpf MUST be in URL for server routing.
                // But do NOT add api_ph to URL — Python only puts it in the POST body.
                // Adding api_ph to URL query would change the signature MD5 (Python signs
                // body-only, C# includes URL query params in MD5 → mismatch → 500002).
                var sep = uri.Contains('?') ? "&" : "?";
                var newUri = $"{uri}{sep}{PlatformParams}";
                request.RequestUri = new Uri(newUri);
            }
        }

        return base.SendAsync(request, ct);
    }
}
