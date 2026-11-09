using System.Text.Json;
using System.Text.RegularExpressions;
using CreatFlow.Platforms.Douyin;
using Serilog;

namespace CreatFlow.Platforms.Download.Extractors;

/// <summary>
/// 抖音作品解析（视频）。
///
/// 四条解析路径，按「信息完整度 / 稳定性」排序，**前一条失败不放弃**：
///  1. Web 详情接口 `www.douyin.com/aweme/v1/web/aweme/detail/`（C# 自算 a_bogus）——
///     2026-10-23 实测恒 403 `Sign Invalid`，留着等签名算法修好，别再指望它；
///  2. **浏览器 WebView2（当前唯一可靠）** —— 页面自签的 `aweme/detail` 能拿到 `play_addr`
///     （**无水印**），详见 <see cref="TryParseBrowserPayload"/>；
///  3. 旧版 iteminfo —— **已下线**（HTTP 200 但 body 长度 0，2026-10-23 实测）；
///  4. 分享页正则 —— **最后兜底，产物可能是带水印的下载版**，只在浏览器不可用时才会走到。
///
/// 水印的关键事实（2026-10-23 实测，别再走回头路）：`download_addr` = 720p **带**右下角
/// 「抖音号 + logo」水印；`play_addr` = 1080p **无水印**。下载器只能取后者。
///
/// 失败分类：403/429 → RiskControl（提示重新登录），其余 → NeedsLogin/Unknown 合并文案。
/// </summary>
public sealed class DouyinDownloadExtractor : IVideoExtractor
{
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/150.0.0.0 Safari/537.36";

    public string Platform => PlatformNames.Douyin;

    private static readonly string[] Hosts = ["douyin.com", "iesdouyin.com"];

    public bool CanHandle(Uri uri) =>
        Hosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
                       || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    public async Task<VideoInfo> ExtractAsync(string url, ExtractContext context, CancellationToken ct)
    {
        try
        {
            var resolved = await ResolveShortLinkAsync(url, context, ct).ConfigureAwait(false);
            var videoId = ExtractVideoId(resolved) ?? ExtractVideoId(url)
                          ?? throw new DownloadException(DownloadErrorKind.Unsupported,
                              "无法从链接里识别抖音作品 ID（支持 /video/{id}、modal_id、短链分享）");

            var ua = context.UserAgent ?? context.Environment?.UserAgent ?? DefaultUserAgent;
            var failures = new List<DownloadException>();

            // 1. Web 详情接口（自算 a_bogus）—— 2026-10-23 起恒 403，留着等签名算法修好
            try
            {
                var info = await TryExtractFromApiAsync(videoId, resolved, ua, context, ct)
                    .ConfigureAwait(false);
                if (info is not null) return info;
            }
            catch (DownloadException ex)
            {
                failures.Add(ex);
                Log.Debug("[DouyinDownload] 详情接口失败：{Kind} {Message}", ex.Kind, ex.Message);
            }

            // 2. 浏览器（唯一能拿到 play_addr 的路）：借已登录的 WebView2 账号页，
            //    在页面上下文里发 XHR 让 secsdk-strategy 自己签名。
            //
            // ⚠️ 这一条**必须排在下面两条 HTTP 兜底之前**（2026-10-23 修）：
            //    分享页那条能从 `play_addr` 正则里捞到地址并"成功"返回 —— 但那是
            //    **带「抖音号 + logo」水印的下载版**（用户实测下到 720p 水印件，
            //    而浏览器 detail 给的是 1080p 干净流）。只在浏览器不可用时才退过去。
            if (context.BrowserExtract is null)
                throw new DownloadException(DownloadErrorKind.NeedsLogin,
                    "抖音下载必须借用账号页面的签名才能拿到无水印源 —— " +
                    "请先到「平台账号」点开抖音账号窗口（确认已登录），再点「重试」");

            try
            {
                // 用规范作品页（/video/{id}）而不是精选项页 —— 页面结构更稳定
                var browserUrl = $"https://www.douyin.com/video/{videoId}";
                var payload = await context.BrowserExtract(browserUrl, ct).ConfigureAwait(false);
                var info = TryParseBrowserPayload(payload, browserUrl, videoId, context);
                if (info is not null) return info with { SourceUrl = resolved };

                Log.Debug("[DouyinDownload] 浏览器页面里没找到作品数据（payload={Len}）", payload?.Length ?? 0);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (DownloadException ex)
            {
                failures.Add(ex);
                Log.Debug("[DouyinDownload] 浏览器兜底失败：{Kind} {Message}", ex.Kind, ex.Message);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DouyinDownload] 浏览器抓取失败");
            }

            // 浏览器没拿到就**到此为止**，不再退回任何 HTTP 兜底：
            //   · iteminfo 已下线（200 空 body）；
            //   · 分享页正则能"成功"返回，但给的是**带「抖音号」水印的下载版** ——
            //     2026-10-23 用户连续两次下到 720p 水印件，都是它抢在浏览器前面。
            // 宁可报错让用户去开账号窗口，也不能悄悄给一个带水印的文件。
            var definitive = failures.FirstOrDefault(e =>
                e.Kind is DownloadErrorKind.Unsupported or DownloadErrorKind.NeedsLogin);
            if (definitive is not null) throw definitive;

            throw new DownloadException(DownloadErrorKind.RiskControl,
                "没能从抖音账号页面拿到作品数据 —— 请到「平台账号」点开抖音账号窗口" +
                "（确认已登录、页面能正常播放该作品），再点「重试」");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new DownloadException(DownloadErrorKind.Cancelled, "解析已取消");
        }
        catch (DownloadException) { throw; }
        catch (Exception ex)
        {
            throw DownloadHttp.Translate(ex, "抖音解析");
        }
    }

    // ═══ 路径 1：Web 详情接口 ═══

    private static async Task<VideoInfo?> TryExtractFromApiAsync(
        string videoId, string sourceUrl, string ua, ExtractContext context, CancellationToken ct)
    {
        // 签名输入**不含 msToken / a_bogus**（见 ABogusGenerator.Generate 的契约）：
        // msToken 由 Cookie 携带，不放进 query —— 放进去又不参与签名会被服务端判为篡改
        var query = BuildDetailQuery(videoId);
        var sign = ABogusGenerator.Generate(query, ua, body: null, aid: 6383);
        var api = "https://www.douyin.com/aweme/v1/web/aweme/detail/?" + query
                  + "&a_bogus=" + Uri.EscapeDataString(sign);

        using var client = DownloadHttp.Create(context, referer: "https://www.douyin.com/");
        using var response = await client.GetAsync(api, ct).ConfigureAwait(false);

        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.TooManyRequests)
            throw new DownloadException(DownloadErrorKind.RiskControl,
                "抖音风控拦截了详情请求（403/429）");

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("aweme_detail", out var detail)
            || detail.ValueKind != JsonValueKind.Object)
            return null;

        return BuildFromAweme(detail, sourceUrl, videoId, ua, context);
    }

    /// <summary>
    /// Web 详情接口的查询串（设备/浏览器指纹字段，缺一容易被判为机器人）。
    /// public：`--probe-douyin` 探针在页面上下文里复刻同一条请求，两边必须是同一份参数
    /// （签名对不上时 403，表现和「没签名」一样，分不清就白查了）。
    /// </summary>
    public static string BuildDetailQuery(string videoId)
    {
        var parts = new List<string>
        {
            "device_platform=webapp",
            "aid=6383",
            "channel=channel_pc_web",
            "aweme_id=" + videoId,
            "pc_client_type=1",
            "version_code=190500",
            "version_name=19.5.0",
            "cookie_enabled=true",
            "screen_width=1920",
            "screen_height=1080",
            "browser_language=zh-CN",
            "browser_platform=Win32",
            "browser_name=Chrome",
            "browser_version=150.0.0.0",
            "browser_online=true",
            "engine_name=Blink",
            "engine_version=150.0.0.0",
            "os_name=Windows",
            "os_version=10",
            "cpu_core_num=12",
            "device_memory=8",
            "platform=PC",
            "downlink=10",
            "effective_type=4g",
            "round_trip_time=50",
        };
        return string.Join('&', parts);
    }

    /// <summary>递归找第一个「带 video 对象的作品节点」（_ROUTER_DATA 的层级随版本变化）。</summary>
    private static JsonElement? FindFirstVideoItem(JsonElement root, int depth)
    {
        if (depth > 16) return null;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("video", out var video)
                && video.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("aweme_id", out _) || root.TryGetProperty("desc", out _)))
                return root;

            foreach (var prop in root.EnumerateObject())
                if (FindFirstVideoItem(prop.Value, depth + 1) is { } found) return found;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                if (FindFirstVideoItem(item, depth + 1) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// 解析浏览器抓回来的页面数据（由 App 层 WebView2 宿主产生）：
    ///  · `detail` = 页面自签 `aweme/detail` 拿到的 `aweme_detail`（JSON 字符串）——
    ///    **唯一带 `play_addr`（无水印 1080p）的来源**，优先；
    ///  · `router` = `window._ROUTER_DATA` 的 JSON 字符串 → 递归找作品对象（detail 为空时兜底）；
    ///  · `videoSrc` = 页面里 `&lt;video&gt;` 的 currentSrc / 导航期抓到的媒体请求 → 只有一个直链（最后兜底）。
    ///
    /// `detail` 为空是**预期的正常情况**：档案被 A/B 导到 `www-hj.douyin.com` 时，
    /// 同样的 XHR 返回 HTTP 200 空 body（2026-10-23 实测），此时必须安静地退到 router/videoSrc。
    /// 返回 null = 页面里没有可用数据（未登录 / 挑战页 / 结构变化）。
    /// </summary>
    public static VideoInfo? TryParseBrowserPayload(
        string? payload, string sourceUrl, string videoId, ExtractContext context)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var ua = context.UserAgent ?? DefaultUserAgent;

            // ① 页面自签 aweme/detail —— play_addr 在这里，download_addr 是带水印的下载版
            if (root.TryGetProperty("detail", out var detailEl)
                && detailEl.ValueKind == JsonValueKind.String
                && detailEl.GetString() is { Length: > 4 } detailJson
                && !string.Equals(detailJson, "null", StringComparison.Ordinal))
            {
                try
                {
                    using var detailDoc = JsonDocument.Parse(detailJson);
                    return BuildFromAweme(detailDoc.RootElement, sourceUrl, videoId, ua, context);
                }
                catch (JsonException ex)
                {
                    Log.Debug(ex, "[DouyinDownload] 浏览器 detail 解析失败，退回页面数据");
                }
            }

            if (root.TryGetProperty("router", out var routerEl)
                && routerEl.ValueKind == JsonValueKind.String
                && routerEl.GetString() is { Length: > 2 } router)
            {
                try
                {
                    using var routerDoc = JsonDocument.Parse(router);
                    if (FindFirstVideoItem(routerDoc.RootElement, 0) is { } item)
                        return BuildFromAweme(item, sourceUrl, videoId, ua, context);
                }
                catch (JsonException ex)
                {
                    Log.Debug(ex, "[DouyinDownload] 浏览器 _ROUTER_DATA 解析失败");
                }
            }

            if (root.TryGetProperty("videoSrc", out var srcEl)
                && srcEl.ValueKind == JsonValueKind.String
                && IsHttpUrl(srcEl.GetString() ?? ""))
            {
                var title = root.TryGetProperty("title", out var titleEl)
                            && titleEl.ValueKind == JsonValueKind.String
                    ? ExtractorHelpers.CleanTitle(titleEl.GetString(), " - 抖音", "-抖音")
                    : "";

                return new VideoInfo
                {
                    Platform = PlatformNames.Douyin,
                    VideoId = videoId,
                    SourceUrl = sourceUrl,
                    Title = string.IsNullOrWhiteSpace(title) ? videoId : title,
                    Formats =
                    [
                        new MediaFormat
                        {
                            Url = UpgradeToHttps(srcEl.GetString()!),
                            FormatId = "dy-browser",
                            Container = "mp4",
                            HasAudio = true,
                            UserAgent = ua,
                            Referer = "https://www.douyin.com/",
                            Cookie = context.Cookie,
                        },
                    ],
                };
            }
        }
        catch (JsonException ex)
        {
            Log.Debug(ex, "[DouyinDownload] 浏览器 payload 解析失败");
        }

        return null;
    }

    // ═══ JSON → VideoInfo ═══

    private static VideoInfo BuildFromAweme(
        JsonElement detail, string sourceUrl, string videoId, string ua, ExtractContext context)
    {
        var title = GetString(detail, "desc") ?? "";
        var author = detail.TryGetProperty("author", out var a) ? GetString(a, "nickname") : null;
        var awemeType = detail.TryGetProperty("aweme_type", out var t) && t.TryGetInt32(out var tv) ? tv : 0;

        if (awemeType == 68)
            throw new DownloadException(DownloadErrorKind.Unsupported,
                "这是抖音图文作品，当前版本只支持视频下载");

        if (!detail.TryGetProperty("video", out var video) || video.ValueKind != JsonValueKind.Object)
            throw new DownloadException(DownloadErrorKind.Unsupported, "抖音返回的作品数据里没有视频流");

        var durationMs = video.TryGetProperty("duration", out var d) && d.TryGetInt64(out var dv) ? dv : 0;
        var cover = video.TryGetProperty("cover", out var c) ? FirstUrl(c) : null;

        var formats = new List<MediaFormat>();

        // play_addr：**无水印**、**音视频合一**（2026-10-23 实测 1080p / 648,092,889 字节，
        // ffprobe 同时报 video + audio）。这是唯一该拿的流。
        if (video.TryGetProperty("play_addr", out var play))
        {
            var pw = GetInt(play, "width");
            var ph = GetInt(play, "height");
            foreach (var u in UrlList(play))
                formats.Add(NewFormat(u, $"dy-{formats.Count}", ua, sourceUrl, context,
                    hasAudio: true, width: pw, height: ph));
        }

        // ⚠️ 刻意**不**放 download_addr：那是烧了「抖音号 + logo + 搜索昵称」水印的下载版。
        // ⚠️ 刻意**不**放 bit_rate[]：那些 URL 落在 `tos-cn-vd-*` 桶，是**纯视频轨**
        //    （ffprobe 只报 video，没有音轨）。它们一旦进候选，Smallest / MaxHeight 就会
        //    挑出一条静音视频 —— 以前没爆是因为所有格式高度都是 0、play_addr 排第一蒙对了。
        //    真要用多档，得同时拿一条纯音频流做合并，那是另一件事（暂无需求）。

        formats = formats
            .GroupBy(f => f.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (formats.Count == 0)
            throw new DownloadException(DownloadErrorKind.Unsupported, "该作品没有可用的视频地址");

        return new VideoInfo
        {
            Platform = PlatformNames.Douyin,
            VideoId = GetString(detail, "aweme_id") ?? videoId,
            SourceUrl = sourceUrl,
            Title = string.IsNullOrWhiteSpace(title) ? "抖音作品" : title,
            Author = author,
            CoverUrl = cover,
            DurationSeconds = (int)(durationMs / 1000),
            Formats = formats,
        };
    }

    private static MediaFormat NewFormat(
        string url, string formatId, string ua, string referer, ExtractContext context,
        bool hasAudio, int width = 0, int height = 0) =>
        new()
        {
            Url = UpgradeToHttps(ExtractorHelpers.UnescapeUrl(url)),
            FormatId = formatId,
            Container = "mp4",
            Width = width,
            Height = height,
            HasAudio = hasAudio,
            UserAgent = ua,
            Referer = referer,
            Cookie = context.Cookie,
        };

    private static int GetInt(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static string? FirstUrl(JsonElement obj) => UrlList(obj).FirstOrDefault();

    /// <summary>
    /// `url_list` 里不一定带 `.mp4`（分享页是 `aweme/v1/play/?video_id=...`）——
    /// 只要是 http(s) 就收，避免把唯一可用的播放地址过滤掉。
    /// </summary>
    private static List<string> UrlList(JsonElement obj)
    {
        if (!obj.TryGetProperty("url_list", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        return [.. list.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(IsHttpUrl)];
    }

    private static bool IsHttpUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string UpgradeToHttps(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..] : url;

    private static string? GetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ═══ URL 工具 ═══

    private static async Task<string> ResolveShortLinkAsync(
        string url, ExtractContext context, CancellationToken ct)
    {
        if (!url.Contains("v.douyin.com", StringComparison.OrdinalIgnoreCase)) return url;

        var current = url;
        for (var i = 0; i < 5; i++)
        {
            using var client = DownloadHttp.Create(context, allowAutoRedirect: false);
            using var response = await client.GetAsync(
                current, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var location = response.Headers.Location;
            if (location is null) return current;

            current = location.IsAbsoluteUri
                ? location.ToString()
                : new Uri(new Uri(current), location).ToString();
            if (!current.Contains("v.douyin.com", StringComparison.OrdinalIgnoreCase)) return current;
        }
        return current;
    }

    private static string? ExtractVideoId(string url)
    {
        var m = Regex.Match(url, @"/(?:video|share/video)/(\d{6,})");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(url, @"[?&](?:modal_id|aweme_id|item_id)=(\d{6,})");
        return m.Success ? m.Groups[1].Value : null;
    }
}
