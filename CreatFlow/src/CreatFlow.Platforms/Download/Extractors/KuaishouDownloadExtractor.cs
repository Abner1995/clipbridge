using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace CreatFlow.Platforms.Download.Extractors;

/// <summary>
/// 快手作品解析（视频）。
///
/// 2026-10-21 实测结论（改之前先看）：
///  · 桌面页 `www.kuaishou.com/short-video/{id}` 已是纯 SPA 空壳 —— HTML 里不再有 `photoUrl`，
///    拿桌面 UA 请求只会得到十几 KB 的外壳（旧版 Lux 那套 `"photoUrl"` 正则已失效）；
///  · **移动端分享页 `https://m.gifshow.com/fw/photo/{id}` 仍是 SSR**，用 iPhone Safari UA
///    匿名请求就能拿到 `window.INIT_STATE`，作品对象里有 `mainMvUrls[].url`（mp4 直链）、
///    `caption` / `userName` / `coverUrls` / `duration` 等字段；
///  · 直链下载必须带同一个 iPhone UA + Referer（Range 实测 206，无 Cookie 即可）。
///
/// 所以解析路线是：**先把任意快手链接归一成 photoId，再打移动端分享页**。
/// 链接形态：`/short-video/{id}`、`/f/{token}`、`m.gifshow.com/fw/photo/{id}`、`v.kuaishou.com/{code}`。
/// </summary>
public sealed class KuaishouDownloadExtractor : IVideoExtractor
{
    public string Platform => PlatformNames.Kuaishou;

    /// <summary>必须是 iPhone Safari —— 桌面 UA 拿到的是 SPA 空壳，没有 INIT_STATE 作品数据。</summary>
    private const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 " +
        "(KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1";

    private const string SharePagePrefix = "https://m.gifshow.com/fw/photo/";

    private static readonly string[] Hosts =
        ["kuaishou.com", "chenzhongtech.com", "gifshow.com"];

    /// <summary>`/short-video/{id}`、`/fw/photo/{id}`、`/photo/{id}`、`/f/{token}`</summary>
    private static readonly Regex PhotoIdPathPattern = new(
        @"/(?:short-video|fw/photo|photo|f)/([A-Za-z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PhotoIdQueryPattern = new(
        @"[?&]photoId=([A-Za-z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DirectPhotoPathPattern = new(
        @"/(?:short-video|fw/photo|photo)/[A-Za-z0-9_-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Mp4UrlPattern = new(
        @"https?://[^""'\s\\]+?\.mp4(?:\?[^""'\s\\]*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool CanHandle(Uri uri) =>
        Hosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
                       || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    public async Task<VideoInfo> ExtractAsync(string url, ExtractContext context, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                    | DecompressionMethods.Brotli,
                // Cookie 由我们自己按请求决定：分享页匿名可用，登录态只在兜底时带上
                UseCookies = false,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", MobileUserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://m.gifshow.com/");

            var photoId = TryParsePhotoId(url);
            var looksDirect = IsDirectPhotoUrl(url);
            var html = "";
            var finalUrl = url;

            if (photoId is not null && looksDirect)
            {
                // 用户给的就是作品页（含长链 short-video）—— 直接打移动端 SSR 分享页
                (html, finalUrl) = await FetchAsync(
                    client, SharePagePrefix + Uri.EscapeDataString(photoId), null, ct);
            }
            else
            {
                // 短链 / 未知形态：先跟随跳转（v.kuaishou.com、/f/ 等），
                // 跳转后的页面本身就是 SSR 时直接用，否则从最终 URL 取 photoId 再打分享页
                var first = await FetchAsync(client, url, null, ct);
                html = first.Html;
                finalUrl = first.FinalUrl;
                photoId ??= TryParsePhotoId(finalUrl);

                if (!HasPhotoData(html) && photoId is not null)
                {
                    var shareUrl = SharePagePrefix + Uri.EscapeDataString(photoId);
                    (html, finalUrl) = await FetchAsync(client, shareUrl, null, ct);
                }
            }

            var data = ParseSharePage(html);

            // 匿名拿不到时，用快手账号会话再试一次（私密/仅粉丝可见作品只有登录态能看）
            if (data is null && !string.IsNullOrWhiteSpace(context.Cookie))
            {
                var (retryHtml, retryUrl) = await FetchAsync(
                    client,
                    photoId is not null
                        ? SharePagePrefix + Uri.EscapeDataString(photoId)
                        : finalUrl,
                    context.Cookie, ct);
                var retryData = ParseSharePage(retryHtml);
                if (retryData is not null)
                {
                    data = retryData;
                    html = retryHtml;
                    finalUrl = retryUrl;
                }
            }

            if (data is null)
            {
                if (html.Contains("\"result\":2", StringComparison.Ordinal)
                    || html.Contains("captcha.zt.kuaishou.com", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("identity-verification", StringComparison.OrdinalIgnoreCase))
                    throw new DownloadException(DownloadErrorKind.RiskControl,
                        "快手返回风控校验，请稍后重试，或在「平台账号」窗口打开一次快手");

                throw new DownloadException(DownloadErrorKind.Unsupported,
                    "快手分享页里没有找到视频数据（可能是图文作品、作品已删除，或页面结构变化）");
            }

            if (data.IsImagePost || string.IsNullOrWhiteSpace(data.VideoUrl))
                throw new DownloadException(DownloadErrorKind.Unsupported,
                    "该作品是图文 / 图集，当前只支持视频作品");

            var videoId = photoId ?? data.PhotoId
                          ?? ExtractorHelpers.StableId(ShareText.NormalizeForIdentity(url));
            var title = string.IsNullOrWhiteSpace(data.Title) ? videoId : data.Title;

            var format = new MediaFormat
            {
                Url = UpgradeToHttps(data.VideoUrl),
                FormatId = "ks-default",
                Container = "mp4",
                Width = data.Width,
                Height = data.Height,
                UserAgent = MobileUserAgent,
                Referer = finalUrl,
            };

            Log.Debug("[KuaishouDownload] {PhotoId} 解析成功（{} 字节页面，直链={Url}）",
                videoId, html.Length, format.Url);

            return new VideoInfo
            {
                Platform = Platform,
                VideoId = videoId,
                SourceUrl = finalUrl,
                Title = title,
                Author = data.Author,
                CoverUrl = data.CoverUrl,
                DurationSeconds = data.DurationSeconds,
                Formats = [format],
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new DownloadException(DownloadErrorKind.Cancelled, "解析已取消");
        }
        catch (DownloadException) { throw; }
        catch (Exception ex)
        {
            throw DownloadHttp.Translate(ex, "快手解析");
        }
    }

    /// <summary>从链接里取作品 ID（`/short-video/{id}`、`/fw/photo/{id}`、`?photoId=` 等）。</summary>
    public static string? TryParsePhotoId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var query = PhotoIdQueryPattern.Match(url);
        if (query.Success) return query.Groups[1].Value;

        var path = PhotoIdPathPattern.Match(url);
        return path.Success ? path.Groups[1].Value : null;
    }

    /// <summary>
    /// 分享页 HTML → 作品数据（纯函数，自检直接断言）。
    /// 主路径解析 `window.INIT_STATE`；解析失败退回老式裸字段 / mp4 直链正则。
    /// 返回 null = 页面里没有作品数据（风控页 / 空壳 / 已删除）。
    /// </summary>
    public static KuaishouShareData? ParseSharePage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var state = TryExtractInitState(html);
        if (state is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(state);
                var photo = FindPhotoObject(doc.RootElement);
                if (photo is { } p) return ReadPhoto(p);
            }
            catch (JsonException ex)
            {
                Log.Debug(ex, "[KuaishouDownload] INIT_STATE 解析失败，改用正则兜底");
            }
        }

        // 兜底：老版 SSR 页面（或结构再次变化时）里的裸字段
        var direct = ExtractPlayUrl(html);
        if (string.IsNullOrWhiteSpace(direct)) return null;

        return new KuaishouShareData(
            VideoUrl: direct,
            Title: NormalizeText(JsonString(html, "caption")),
            Author: NormalizeText(JsonString(html, "userName")),
            CoverUrl: ExtractCover(html),
            DurationSeconds: 0,
            Width: 0,
            Height: 0,
            IsImagePost: false,
            PhotoId: JsonString(html, "photoId"));
    }

    // ── HTTP ──

    private static async Task<(string Html, string FinalUrl)> FetchAsync(
        HttpClient client, string url, string? cookieHeader, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(cookieHeader))
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new DownloadException(DownloadErrorKind.NotFound, "快手作品不存在或已删除");
            throw HttpFileDownloader.MapStatus(response.StatusCode);
        }

        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        return (html, finalUrl);
    }

    private static bool HasPhotoData(string html) =>
        html.Contains("mainMvUrls", StringComparison.Ordinal)
        || html.Contains("photoType", StringComparison.Ordinal)
        || Mp4UrlPattern.IsMatch(html);

    private static bool IsDirectPhotoUrl(string url) =>
        DirectPhotoPathPattern.IsMatch(url) || PhotoIdQueryPattern.IsMatch(url);

    // ── 页面数据 ──

    /// <summary>取 `window.INIT_STATE = { ... }` 的 JSON 本体（按括号配对，避开字符串里的花括号）。</summary>
    private static string? TryExtractInitState(string html)
    {
        var marker = html.IndexOf("window.INIT_STATE", StringComparison.Ordinal);
        if (marker < 0) return null;

        var start = html.IndexOf('{', marker);
        if (start < 0) return null;

        var end = FindMatchingBrace(html, start);
        return end < 0 ? null : html[start..(end + 1)];
    }

    private static int FindMatchingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>递归找作品对象（含 `photoType` + `photoId` 的那个），不依赖固定的嵌套层级。</summary>
    private static JsonElement? FindPhotoObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("photoType", out _)
                    && element.TryGetProperty("photoId", out _))
                    return element;

                foreach (var property in element.EnumerateObject())
                {
                    var found = FindPhotoObject(property.Value);
                    if (found.HasValue) return found;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var found = FindPhotoObject(item);
                    if (found.HasValue) return found;
                }
                break;
        }
        return null;
    }

    private static KuaishouShareData ReadPhoto(JsonElement photo)
    {
        var urls = ReadUrlArray(photo, "mainMvUrls");
        if (urls.Count == 0 && photo.TryGetProperty("manifest", out var manifest))
            urls = ReadManifestUrls(manifest);

        var cover = ReadUrlArray(photo, "coverUrls").FirstOrDefault()
                    ?? ReadUrlArray(photo, "webpCoverUrls").FirstOrDefault();

        var photoType = GetString(photo, "photoType");
        var singlePicture = GetBool(photo, "singlePicture");
        var durationMs = GetInt64(photo, "duration");

        return new KuaishouShareData(
            VideoUrl: urls.FirstOrDefault(),
            Title: NormalizeText(GetString(photo, "caption")),
            Author: NormalizeText(GetString(photo, "userName")),
            CoverUrl: cover,
            DurationSeconds: durationMs > 0 ? (int)Math.Round(durationMs / 1000.0) : 0,
            Width: (int)GetInt64(photo, "width"),
            Height: (int)GetInt64(photo, "height"),
            IsImagePost: urls.Count == 0 || singlePicture
                || (photoType is not null && !photoType.Equals("VIDEO", StringComparison.OrdinalIgnoreCase)),
            PhotoId: GetString(photo, "photoId"));
    }

    /// <summary>`[{"cdn":"...","url":"..."}]` → URL 列表（也兼容纯字符串数组）。</summary>
    private static List<string> ReadUrlArray(JsonElement owner, string property)
    {
        var result = new List<string>();
        if (!owner.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in array.EnumerateArray())
        {
            var url = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("url", out var u)
                                          && u.ValueKind == JsonValueKind.String => u.GetString(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(url))
                result.Add(ExtractorHelpers.UnescapeUrl(url));
        }
        return result;
    }

    /// <summary>manifest.adaptationSet[].representation[].url —— mainMvUrls 缺失时的兜底。</summary>
    private static List<string> ReadManifestUrls(JsonElement manifest)
    {
        var result = new List<string>();
        if (manifest.ValueKind != JsonValueKind.Object
            || !manifest.TryGetProperty("adaptationSet", out var sets)
            || sets.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var set in sets.EnumerateArray())
        {
            if (!set.TryGetProperty("representation", out var reps)
                || reps.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var rep in reps.EnumerateArray())
            {
                var url = GetString(rep, "url");
                if (!string.IsNullOrWhiteSpace(url))
                    result.Add(ExtractorHelpers.UnescapeUrl(url));
            }
        }
        return result;
    }

    private static string? GetString(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetInt64(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)) return n;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var s)) return s;
        return 0;
    }

    private static bool GetBool(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var b) && b,
            _ => false,
        };
    }

    // ── 正则兜底 ──

    private static string? ExtractPlayUrl(string html)
    {
        foreach (var key in new[] { "photoUrl", "playUrl", "srcNoMark" })
        {
            var raw = ExtractorHelpers.Match(html,
                $@"""{Regex.Escape(key)}""\s*:\s*""((?:[^""\\]|\\.)+)""");
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var url = ExtractorHelpers.UnescapeUrl(JsonUnescape(raw));
            if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase)) return url;
        }

        var match = Mp4UrlPattern.Match(html);
        return match.Success ? match.Value : null;
    }

    private static string? ExtractCover(string html)
    {
        foreach (var key in new[] { "coverUrl", "poster" })
        {
            var raw = ExtractorHelpers.Match(html,
                $@"""{Regex.Escape(key)}""\s*:\s*""((?:[^""\\]|\\.)+)""");
            if (!string.IsNullOrWhiteSpace(raw))
                return ExtractorHelpers.UnescapeUrl(JsonUnescape(raw));
        }
        return null;
    }

    private static string? JsonString(string html, string key)
    {
        var raw = ExtractorHelpers.Match(html,
            $@"""{Regex.Escape(key)}""\s*:\s*""((?:[^""\\]|\\.)*)""");
        return string.IsNullOrWhiteSpace(raw) ? null : JsonUnescape(raw);
    }

    private static string JsonUnescape(string raw)
    {
        try { return JsonSerializer.Deserialize<string>($"\"{raw}\"") ?? raw; }
        catch (JsonException) { return raw; }
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace(' ', ' ').Trim();
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.Length == 0 ? null : normalized;
    }

    private static string UpgradeToHttps(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url[7..]
            : url;
}

/// <summary>快手移动端分享页里与下载相关的作品字段（<see cref="KuaishouDownloadExtractor.ParseSharePage"/> 的产物）。</summary>
public sealed record KuaishouShareData(
    string? VideoUrl,
    string? Title,
    string? Author,
    string? CoverUrl,
    int DurationSeconds,
    int Width,
    int Height,
    bool IsImagePost,
    string? PhotoId);
