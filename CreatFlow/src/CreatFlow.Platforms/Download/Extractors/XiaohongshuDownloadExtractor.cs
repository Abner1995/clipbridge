using System.Text.Json;
using System.Text.RegularExpressions;

namespace CreatFlow.Platforms.Download.Extractors;

/// <summary>
/// 小红书笔记解析（视频）。
///
/// 逻辑要点（来自公开页面行为，非代码移植）：
///  1. 笔记页 HTML 里带 SSR 数据，视频地址在 `"backupUrls"` 数组中；
///  2. 标题在 `&lt;title&gt;`，作者在 `"nickname"`；
///  3. 短链 xhslink.com 需要先跟随 302 到正式笔记页。
///
/// ⚠️ 页面结构随时可能改版：解析失败时给出可操作的错误分类，而不是抛裸异常。
/// </summary>
public sealed class XiaohongshuDownloadExtractor : IVideoExtractor
{
    public string Platform => PlatformNames.Xiaohongshu;

    private static readonly string[] Hosts = ["xiaohongshu.com", "xhslink.com"];

    public bool CanHandle(Uri uri) =>
        Hosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
                       || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    public async Task<VideoInfo> ExtractAsync(string url, ExtractContext context, CancellationToken ct)
    {
        try
        {
            using var client = DownloadHttp.Create(context, referer: "https://www.xiaohongshu.com/",
                accept: "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw HttpFileDownloader.MapStatus(response.StatusCode);

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;

            var videoUrls = ExtractVideoUrls(html);
            if (videoUrls.Count == 0)
                throw new DownloadException(DownloadErrorKind.Unsupported,
                    "这个笔记里没有找到视频（可能是图文笔记，或页面结构与预期不同）");

            var videoId = ExtractVideoId(finalUrl) ?? ExtractVideoId(url)
                          ?? ExtractorHelpers.StableId(ShareText.NormalizeForIdentity(url));
            var title = ExtractorHelpers.CleanTitle(
                ExtractorHelpers.Match(html, @"<title>(.*?)</title>", RegexOptions.Singleline),
                " - 小红书", "-小红书");
            var author = JsonString(html, "nickname");
            var cover = ExtractorHelpers.DecodeHtml(
                ExtractorHelpers.Match(html, @"<meta[^>]+property=""og:image""[^>]+content=""([^""]+)""",
                    RegexOptions.IgnoreCase));
            if (string.IsNullOrWhiteSpace(title)) title = videoId;

            var formats = videoUrls
                .Select((u, i) => new MediaFormat
                {
                    Url = UpgradeToHttps(u),
                    FormatId = $"xhs-{i}",
                    Container = "mp4",
                    UserAgent = context.UserAgent ?? context.Environment?.UserAgent,
                    Referer = finalUrl,
                    Cookie = context.Cookie,
                })
                .ToList();

            return new VideoInfo
            {
                Platform = Platform,
                VideoId = videoId,
                SourceUrl = finalUrl,
                Title = title,
                Author = author,
                CoverUrl = cover,
                Formats = formats,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new DownloadException(DownloadErrorKind.Cancelled, "解析已取消");
        }
        catch (DownloadException) { throw; }
        catch (Exception ex)
        {
            throw DownloadHttp.Translate(ex, "小红书解析");
        }
    }

    /// <summary>`"backupUrls":[...]` → mp4 列表；再补 masterUrl / og:video 兜底。</summary>
    private static List<string> ExtractVideoUrls(string html)
    {
        var urls = new List<string>();

        var arrayJson = ExtractorHelpers.Match(html, @"""backupUrls""\s*:\s*(\[[^\]]*\])",
            RegexOptions.Singleline);
        urls.AddRange(ExtractorHelpers.ParseStringArray(arrayJson));

        if (urls.Count == 0)
        {
            urls.AddRange(ExtractorHelpers.MatchAll(html,
                @"""(?:masterUrl|url)""\s*:\s*""(https?:[^""]+?\.mp4[^""]*)"""));
            urls.AddRange(ExtractorHelpers.MatchAll(html,
                @"<meta[^>]+property=""og:video""[^>]+content=""([^""]+)"""));
        }

        return urls
            .Select(ExtractorHelpers.UnescapeUrl)
            .Where(u => u.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ExtractVideoId(string url)
    {
        var m = Regex.Match(url, @"/(?:explore|discovery/item|item)/([0-9a-fA-F]{16,})");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(url, @"[?&]noteId=([0-9a-zA-Z]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string UpgradeToHttps(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url[7..]
            : url;

    /// <summary>从 HTML 里的 JSON 片段取字符串值（处理 \uXXXX 转义）。</summary>
    private static string? JsonString(string html, string key)
    {
        var raw = ExtractorHelpers.Match(html, $@"""{Regex.Escape(key)}""\s*:\s*""((?:[^""\\]|\\.)*)""");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonSerializer.Deserialize<string>($"\"{raw}\""); }
        catch (JsonException) { return raw; }
    }
}
