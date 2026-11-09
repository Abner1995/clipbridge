namespace CreatFlow.Platforms.Core.Handlers;

/// <summary>
/// 请求日志 Handler — 记录所有发出的请求和收到的响应状态码。
/// 放在 Handler 管道的最后（最靠近网络层）以便捕获最终发送的请求（含签名后 URL + apiPh 注入后 body）。
/// </summary>
public sealed class LoggingHandler : DelegatingHandler
{
    public LoggingHandler(ILogger<LoggingHandler>? logger = null)
    {
        InnerHandler = new HttpClientHandler
        {
            UseCookies = false,   // 关闭自动 Cookie 管理，改为手动 Cookie 头
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        // multipart（封面图等二进制）不读成字符串：既避免日志里出现二进制乱码，
        // 也避免把整个文件缓冲两遍
        var body = request.Content is not null and not MultipartFormDataContent
            ? await request.Content.ReadAsStringAsync(ct)
            : request.Content is MultipartFormDataContent
                ? "(multipart/form-data)"
                : null;
        var bodyLen = body?.Length ?? 0;
        var bodyLog = bodyLen > 600
            ? $"(len={bodyLen}) {body?[..300]}...{body?[^200..]}"
            : body;
        Serilog.Log.Debug("[HTTP] {Method} {Url} body={BodyLog}",
            request.Method, request.RequestUri, bodyLog);

        var resp = await base.SendAsync(request, ct);

        if (resp.IsSuccessStatusCode)
        {
            Serilog.Log.Debug("[HTTP] → {StatusCode}",
                (int)resp.StatusCode);
        }
        else
        {
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            var snippet = respBody.Length > 600 ? respBody[..600] : respBody;
            Serilog.Log.Warning("[HTTP] → {StatusCode} body={Body}",
                (int)resp.StatusCode, snippet);
            // Re-create content so the caller can still read it
            var newContent = new ByteArrayContent(Encoding.UTF8.GetBytes(respBody));
            foreach (var h in resp.Content.Headers)
                newContent.Headers.TryAddWithoutValidation(h.Key, h.Value);
            resp.Content = newContent;
        }

        return resp;
    }
}
