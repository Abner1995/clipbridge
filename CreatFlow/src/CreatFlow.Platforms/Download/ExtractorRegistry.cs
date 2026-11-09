using System.Net;
using System.Text.RegularExpressions;

namespace CreatFlow.Platforms.Download;

/// <summary>
/// 平台提取器。实现必须遵守：**只解析、不下载**（返回元数据与候选流），
/// 且所有 HTTP 请求的 UA/Referer/Cookie 与页面一致（防盗链）。
/// </summary>
public interface IVideoExtractor
{
    /// <summary>平台标识（用 <see cref="PlatformNames"/> 常量）</summary>
    string Platform { get; }

    /// <summary>快速路由：只做主机/短链判断，不做网络请求</summary>
    bool CanHandle(Uri uri);

    /// <summary>解析作品元数据与候选流；失败抛 <see cref="DownloadException"/>（带错误分类）</summary>
    Task<VideoInfo> ExtractAsync(string url, ExtractContext context, CancellationToken ct);
}

/// <summary>提取器注册表 —— 按顺序问每个 extractor「能不能处理」。</summary>
public sealed class ExtractorRegistry
{
    private readonly IReadOnlyList<IVideoExtractor> _extractors;

    public ExtractorRegistry(IEnumerable<IVideoExtractor> extractors) =>
        _extractors = extractors.ToList();

    public IReadOnlyList<IVideoExtractor> All => _extractors;

    public IVideoExtractor? Find(Uri uri) =>
        _extractors.FirstOrDefault(e =>
        {
            try { return e.CanHandle(uri); }
            catch { return false; }
        });

    public IVideoExtractor Get(Uri uri) =>
        Find(uri) ?? throw new DownloadException(DownloadErrorKind.Unsupported,
            $"暂不支持该链接：{uri.Host}（当前支持抖音 / 快手 / 小红书）");
}

/// <summary>
/// 从分享文案里捞 URL（抖音/快手分享口令都是「一句话 + 链接」混排）。
/// 纯函数，--selftest 断言。
/// </summary>
public static class ShareText
{
    // 停在空白与中文标点：分享文案里 URL 后面常直接跟「复制此链接…」
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s，。！？；：、（）【】《》""''“”‘’]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>抽出全部 URL（去重、去尾部英文标点），保持出现顺序。</summary>
    public static List<string> ExtractUrls(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match m in UrlPattern.Matches(text))
        {
            var url = m.Value.TrimEnd('.', ',', ';', ')', ']', '>', '!', '?');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme is not ("http" or "https")) continue;
            var normalized = uri.GetLeftPart(UriPartial.Query);
            if (seen.Add(normalized)) result.Add(normalized);
        }

        // 分享口令没带 URL 时，尝试把整段里的裸短链捞出来（如 v.douyin.com/xxxx）
        if (result.Count == 0)
        {
            var bare = Regex.Match(text, @"(?<url>(?:v\.douyin\.com|v\.kuaishou\.com|xhslink\.com)/[A-Za-z0-9_\-/]+)");
            if (bare.Success) result.Add("https://" + bare.Groups["url"].Value);
        }
        return result;
    }

    /// <summary>去重用的 URL 规范化：去掉锚点与常见分享参数（不改变平台标识类参数）。</summary>
    public static string NormalizeForIdentity(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url.Trim();
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0) return uri.GetLeftPart(UriPartial.Path);

        var kept = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair =>
            {
                var key = pair.Split('=', 2)[0];
                return !key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
                       && !key.Equals("share_token", StringComparison.OrdinalIgnoreCase)
                       && !key.Equals("share_from", StringComparison.OrdinalIgnoreCase)
                       && !key.Equals("from", StringComparison.OrdinalIgnoreCase)
                       && !key.Equals("source", StringComparison.OrdinalIgnoreCase);
            });
        var q = string.Join('&', kept);
        return q.Length == 0 ? uri.GetLeftPart(UriPartial.Path) : $"{uri.GetLeftPart(UriPartial.Path)}?{q}";
    }
}

/// <summary>页面抓取用 HttpClient（跟随跳转、带登录态与浏览器指纹）。</summary>
public static class DownloadHttp
{
    public static HttpClient Create(
        ExtractContext context,
        string? referer = null,
        bool allowAutoRedirect = true,
        string? accept = null)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli,
            UseCookies = false,   // Cookie 由我们自己拼（与平台会话完全一致）
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var ua = context.UserAgent ?? context.Environment?.UserAgent
                 ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            accept ?? "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        var env = context.Environment;
        if (env is { IsValid: true })
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua", env.SecChUa);
            client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", env.SecChUaMobile);
            client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", env.SecChUaPlatform);
        }

        if (!string.IsNullOrWhiteSpace(referer))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrWhiteSpace(context.Cookie))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", context.Cookie);
        return client;
    }

    /// <summary>把网络层异常翻译成统一下载异常（超时/连接失败 → Network）。</summary>
    public static DownloadException Translate(Exception ex, string what = "请求") =>
        ex switch
        {
            DownloadException de => de,
            TaskCanceledException or OperationCanceledException =>
                new DownloadException(DownloadErrorKind.Cancelled, $"{what}已取消", ex),
            HttpRequestException or IOException or TimeoutException =>
                new DownloadException(DownloadErrorKind.Network, $"{what}失败：{ex.Message}", ex),
            _ => new DownloadException(DownloadErrorKind.Unknown, $"{what}失败：{ex.Message}", ex),
        };
}
