namespace CreatFlow.Platforms.Kuaishou.Handlers;

/// <summary>
/// 快手 API 请求中自动注入 kuaishou.web.cp.api_ph 到 JSON body。
///
/// 对标 Python api.py _post 方法中 body.setdefault("kuaishou.web.cp.api_ph", self.api_ph)。
/// </summary>
internal sealed class KuaishouApiPhHandler : DelegatingHandler
{
    private readonly string _apiPh;

    public KuaishouApiPhHandler(string apiPh)
    {
        _apiPh = apiPh;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is not null &&
            request.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var body = await request.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                var newBody = body;
                if (!body.Contains("\"kuaishou.web.cp.api_ph\""))
                {
                    // Insert kuaishou.web.cp.api_ph before the closing }.
                    // 空对象 {} 不能加逗号（`{,"kuaishou...":…}` 不是合法 JSON）——
                    // 活动 tab/filter 就是发空 body 的，2026-11-06 探针实测踩到。
                    var trimmed = body.TrimEnd();
                    if (trimmed.EndsWith('}'))
                    {
                        var head = trimmed[..^1];
                        var separator = head.TrimEnd().EndsWith('{') ? "" : ",";
                        newBody = head + separator
                                  + "\"kuaishou.web.cp.api_ph\":\"" + _apiPh + "\"}";
                    }
                }

                // CRITICAL: always recreate ByteArrayContent so downstream handlers
                // (SigHandler) can re-read the body. StreamContent is single-read.
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(newBody));
                request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json;charset=UTF-8");
            }
        }

        return await base.SendAsync(request, ct);
    }
}
