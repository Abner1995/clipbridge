namespace CreatFlow.Platforms.Core.Handlers;

/// <summary>
/// CSRF Token Handler — 将 x-secsdk-csrf-token 注入到每个请求头。
///
/// 抖音创建/编辑/删除合集等 POST 请求需要此 Header。
/// 对标 Python createcollection.py 中 headers['x-secsdk-csrf-token']。
/// </summary>
public sealed class CsrfTokenHandler : DelegatingHandler
{
    private readonly Func<string?> _tokenProvider;

    /// <summary>
    /// 创建 CSRF Token Handler。
    /// </summary>
    /// <param name="tokenProvider">提供 CSRF token 的函数。传 null 或返回 null 时不注入。</param>
    public CsrfTokenHandler(Func<string?> tokenProvider)
    {
        _tokenProvider = tokenProvider ?? (() => null);
    }

    public CsrfTokenHandler(string? token)
        : this(() => token) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var token = _tokenProvider();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("x-secsdk-csrf-token", token);
        }

        return await base.SendAsync(request, ct);
    }
}
