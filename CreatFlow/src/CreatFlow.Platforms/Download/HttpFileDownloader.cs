using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace CreatFlow.Platforms.Download;

/// <summary>
/// 直链下载器（mp4/flv 等单文件）：Range 断点续传 + 进度上报 + HTTP 错误分类。
///
/// **不做重试** —— 重试策略在队列层（4.6 错误分类），这里只负责把一次尝试跑完并把
/// 失败原因翻译成 <see cref="DownloadErrorKind"/>。这样「403 → 重新解析」这类决策只有一处。
/// </summary>
public sealed class HttpFileDownloader
{
    private const int BufferSize = 128 * 1024;
    private const int ProgressIntervalMs = 200;

    /// <summary>
    /// 下载到 <paramref name="targetPath"/>（已存在则续传；本方法只追加，不截断）。
    /// 返回最终字节数。
    /// </summary>
    public async Task<long> DownloadAsync(
        MediaFormat format,
        string targetPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        if (format.Kind != StreamKind.Direct)
            throw new DownloadException(DownloadErrorKind.Unsupported,
                $"直链下载器不支持 {format.Kind} 流");

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var existing = File.Exists(targetPath) ? new FileInfo(targetPath).Length : 0;

        // 续传前核对「是不是同一个资源」：签名 URL 每次解析都会变，但路径（CDN 上的对象名）不变；
        // 路径变了说明换了文件，旧的 .part 不能接着写（否则拼出坏文件）。
        var marker = targetPath + ".source";
        var key = ResourceKey(format.Url);
        try
        {
            if (File.Exists(marker))
            {
                var previous = await File.ReadAllTextAsync(marker, ct).ConfigureAwait(false);
                if (!string.Equals(previous, key, StringComparison.Ordinal))
                {
                    if (File.Exists(targetPath)) File.Delete(targetPath);
                    existing = 0;
                }
            }
            await File.WriteAllTextAsync(marker, key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 标记文件写不了不致命：最坏情况是把两个不同文件拼在一起（极少见），
            // 由下载完成后的 Content-Length 校验兜底。
        }

        using var client = CreateClient(format);
        using var request = new HttpRequestMessage(HttpMethod.Get, format.Url);
        request.Headers.Accept.ParseAdd("*/*");
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // 416 = 续传偏移已经越过文件尾（多半是上次已下完或服务端换了文件）—— 从头再来一次
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            File.Delete(targetPath);
            existing = 0;
            using var retry = new HttpRequestMessage(HttpMethod.Get, format.Url);
            retry.Headers.Accept.ParseAdd("*/*");
            using var retryResp = await client.SendAsync(
                retry, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return await CopyAsync(retryResp, targetPath, start: 0, progress, ct).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
            throw MapStatus(response.StatusCode);

        // 服务端忽略 Range 返回 200 → 只能从头写（不能追加，否则文件被拼坏）
        var append = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
        var start = append ? existing : 0;
        if (!append && existing > 0)
        {
            try { File.Delete(targetPath); } catch { /* 删不掉就按新建写，下面 FileMode.Create 会覆盖 */ }
            existing = 0;
        }

        return await CopyAsync(response, targetPath, start, progress, ct).ConfigureAwait(false);
    }

    private static async Task<long> CopyAsync(
        HttpResponseMessage response,
        string targetPath,
        long start,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var totalFromHeader = response.Content.Headers.ContentLength;
        var total = totalFromHeader is > 0 ? start + totalFromHeader.Value : (long?)null;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(
            targetPath, start > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        long received = start;
        var windowStart = Stopwatch.GetTimestamp();
        var windowBytes = 0L;
        var lastReport = 0L;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;

            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            windowBytes += read;

            var now = Stopwatch.GetTimestamp();
            var elapsedMs = Stopwatch.GetElapsedTime(lastReport, now).TotalMilliseconds;
            if (progress is null || elapsedMs < ProgressIntervalMs) continue;

            var windowSeconds = Math.Max(Stopwatch.GetElapsedTime(windowStart, now).TotalSeconds, 0.001);
            var speed = windowBytes / windowSeconds;
            TimeSpan? eta = speed > 1 && total is > 0
                ? TimeSpan.FromSeconds(Math.Max(0, (total.Value - received) / speed))
                : null;

            progress.Report(new DownloadProgress(received, total, speed, eta));
            lastReport = now;
            windowBytes = 0;
            windowStart = now;
        }

        await target.FlushAsync(ct).ConfigureAwait(false);
        progress?.Report(new DownloadProgress(received, total ?? received, 0, TimeSpan.Zero));

        if (total is > 0 && received != total.Value)
            throw new DownloadException(DownloadErrorKind.Network,
                $"下载不完整：{received}/{total} 字节（连接可能被中断，可续传重试）");

        return received;
    }

    /// <summary>把 HTTP 状态码翻译成统一的错误分类（队列据此决定重试 / 重新解析 / 失败）。</summary>
    public static DownloadException MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Forbidden => new DownloadException(DownloadErrorKind.UrlExpired,
            "播放地址已失效或拒绝访问（403）—— 需要重新解析"),
        HttpStatusCode.Unauthorized => new DownloadException(DownloadErrorKind.NeedsLogin,
            "该作品需要登录后才能下载（401）"),
        HttpStatusCode.NotFound or HttpStatusCode.Gone => new DownloadException(DownloadErrorKind.NotFound,
            "文件不存在或作品已删除（404/410）"),
        HttpStatusCode.TooManyRequests => new DownloadException(DownloadErrorKind.RiskControl,
            "平台限流（429），稍后再试"),
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
            new DownloadException(DownloadErrorKind.Network, $"服务端错误（{(int)status}），可重试"),
        _ => new DownloadException(DownloadErrorKind.Http, $"下载失败：HTTP {(int)status}"),
    };

    /// <summary>资源身份 = 去掉查询串的 URL（签名参数每次都变，对象路径不变）。</summary>
    private static string ResourceKey(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : url;

    /// <summary>按格式自带的 UA / Referer / Cookie 建客户端 —— 防盗链三件套缺一不可。</summary>
    internal static HttpClient CreateClient(MediaFormat format)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var client = new HttpClient(handler)
        {
            // 大文件下载可能很久；超时由 CancellationToken 与连接超时负责
            Timeout = TimeSpan.FromHours(2),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            format.UserAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                                 "(KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36");
        if (!string.IsNullOrWhiteSpace(format.Referer))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", format.Referer);
        if (!string.IsNullOrWhiteSpace(format.Cookie))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", format.Cookie);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        return client;
    }
}
