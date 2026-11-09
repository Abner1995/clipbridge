namespace CreatFlow.Platforms.Douyin.Handlers;

/// <summary>
/// a_bogus / msToken 注入 Handler。
///
/// 抖音安全 SDK（WASM）动态生成 msToken 和 a_bogus，
/// 每个请求的 URL query string 都需携带这两个签名参数。
///
/// 当前 Phase 1：由外部提供者注入（WebView2 拦截提取 / 服务端签名桥）。
/// 后续可改为从本地缓存或服务端拉取。
/// </summary>
public sealed class ABogusHandler : DelegatingHandler
{
    private readonly Func<string> _msTokenProvider;
    private readonly Func<string> _aBogusProvider;

    /// <summary>
    /// 创建 a_bogus / msToken Handler。
    /// </summary>
    /// <param name="msTokenProvider">msToken 提供函数（WebView2 提取后缓存）</param>
    /// <param name="aBogusProvider">a_bogus 提供函数（每次请求不同，动态生成）</param>
    public ABogusHandler(
        Func<string> msTokenProvider,
        Func<string> aBogusProvider)
    {
        _msTokenProvider = msTokenProvider ?? (() => "");
        _aBogusProvider = aBogusProvider ?? (() => "");
    }

    /// <summary>
    /// 使用固定的 msToken / a_bogus（短时间内有效）。
    /// </summary>
    public ABogusHandler(string msToken, string aBogus)
        : this(() => msToken, () => aBogus) { }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var msToken = _msTokenProvider();
        var aBogus = _aBogusProvider();

        if (!string.IsNullOrWhiteSpace(msToken) || !string.IsNullOrWhiteSpace(aBogus))
        {
            var uri = request.RequestUri!;
            var query = uri.Query.TrimStart('?');
            var queryParts = string.IsNullOrWhiteSpace(query)
                ? []
                : query.Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();

            // 移除已存在的 msToken / a_bogus（幂等）
            queryParts.RemoveAll(p =>
                p.StartsWith("msToken=", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("a_bogus=", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(msToken))
                queryParts.Add($"msToken={Uri.EscapeDataString(msToken)}");
            if (!string.IsNullOrWhiteSpace(aBogus))
                queryParts.Add($"a_bogus={Uri.EscapeDataString(aBogus)}");

            var uriBuilder = new UriBuilder(uri)
            {
                Query = string.Join("&", queryParts)
            };
            request.RequestUri = uriBuilder.Uri;
        }

        return base.SendAsync(request, ct);
    }
}
