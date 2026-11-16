using System.Text.Json.Serialization;

namespace CreatFlow.Platforms.Bilibili;

/// <summary>
/// B站投稿字幕（2026-11-15 B5 抓包，wire 见 <c>docs/bilibili/wire-2026-11-15.md</c>）。
///
/// 两步，全部在**投稿前**（拿到 cid 之后、add/v3 之前）：
/// <code>
/// ① POST https://api.bilibili.com/x/upload/web/image?t=…&amp;csrf=…
///    multipart: bucket=subtitle / file=&lt;.srt&gt; / csrf / content_type=application/x-subrip
/// → data.location = http://i0.hdslb.com/bfs/subtitle/&lt;md5&gt;.srt
///
/// ② POST https://api.bilibili.com/x/v2/dm/subtitle/draft/preSave?t=…&amp;csrf=…
///    multipart: oid=&lt;cid&gt; / type=1 / files=[{"url":…,"lan":"zh","subtitle_id":0}] / aid=0 / csrf
/// → {"code":0,"message":"OK"}
/// </code>
///
/// 发布体落点 <c>videos[0].pre_subtitle = true</c>（<see cref="BilibiliPublishService"/>，
/// 页面 bundle <c>getPreSubtitle</c> 同款）；<c>subtitle</c> 仍是页面恒下发的占位
/// <c>{open:0,lan:""}</c>，**不是**开关。
///
/// 只收 <c>.srt</c>（页面 input accept 与 JS 正则双重校验）；上传时统一
/// Blob type = <c>application/x-subrip</c>。
/// </summary>
public sealed class BilibiliSubtitleService
{
    /// <summary>字幕文件上传（抓包 <c>POST /x/upload/web/image</c>，bucket=subtitle）</summary>
    public const string UploadPath = "/x/upload/web/image";

    /// <summary>草稿字幕绑定（抓包 <c>POST /x/v2/dm/subtitle/draft/preSave</c>）</summary>
    public const string PreSavePath = "/x/v2/dm/subtitle/draft/preSave";

    /// <summary>页面 bundle <c>uploadSubtitle</c> 写死的 Blob 类型</summary>
    public const string SubtitleContentType = "application/x-subrip";

    /// <summary>上传桶名（抓包 <c>bucket=subtitle</c>）</summary>
    public const string SubtitleBucket = "subtitle";

    /// <summary>与投稿页同源的 Referer（字幕弹窗在投稿页内）</summary>
    public const string Referer = BilibiliUploadService.VideoReferer;

    /// <summary>默认字幕语言（页面下拉默认中文；语言表 subtitle_lan_v3.json 里 zh = 中文）</summary>
    public const string DefaultLan = "zh";

    /// <summary>本地保护上限（.srt 是纯文本，20 MB 只是防呆）</summary>
    private const long MaxSubtitleBytes = 20 * 1024 * 1024;

    private readonly PlatformOptions _options;

    public BilibiliSubtitleService(PlatformOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 上传本地 <c>.srt</c> 并绑定到 <paramref name="cid"/>。
    /// 返回平台返回的字幕 URL（<c>data.location</c>，已按页面口径归一成 <c>http://</c>）。
    /// 失败一律 <see cref="BilibiliApiException"/>（带平台 message；-101 追加登录引导）。
    /// </summary>
    public async Task<string> UploadAsync(
        string cookie, long cid, string subtitlePath, string? lan = null,
        CancellationToken ct = default)
    {
        if (cid <= 0)
            throw new BilibiliApiException($"B站字幕：cid 不合法（{cid}）", -1);

        var path = RequireSrtPath(subtitlePath);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new BilibiliApiException($"B站字幕文件不存在：{path}", -1);
        if (info.Length <= 0)
            throw new BilibiliApiException($"B站字幕文件是空文件：{path}", -1);
        if (info.Length > MaxSubtitleBytes)
        {
            throw new BilibiliApiException(
                $"B站字幕 {info.Length / 1024.0 / 1024.0:0.#} MB，"
                + $"超过本地保护上限 {MaxSubtitleBytes / 1024 / 1024} MB", -1);
        }

        var csrf = BilibiliUploadService.RequireCsrf(cookie);
        var bytes = await File.ReadAllBytesAsync(path, ct);

        using var client = new BilibiliApiClient(cookie, _options);

        // ① 上传文件
        var uploadUrl = BuildUploadUrl(
            csrf, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _options.BilibiliApiHost);
        using var uploadForm = BuildUploadForm(bytes, info.Name, csrf);
        var uploadBody = await client.PostMultipartAsync(uploadUrl, uploadForm, Referer, ct);
        var location = ParseUploadLocation(uploadBody);

        // ② 绑定到 cid（草稿态；未发布 aid 传 0）
        var preSaveUrl = BuildPreSaveUrl(
            csrf, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _options.BilibiliApiHost);
        using var preSaveForm = BuildPreSaveForm(cid, NormalizeLan(lan), location, csrf);
        var preSaveBody = await client.PostMultipartAsync(preSaveUrl, preSaveForm, Referer, ct);
        BilibiliUploadService.RequireCodeZero(preSaveBody, "字幕绑定");

        return location;
    }

    // ═══ 纯函数（自检直接验 wire 形体） ═══

    /// <summary>
    /// 校验字幕路径：只收 <c>.srt</c>（大小写不敏感）。空白 → 报错。
    /// </summary>
    public static string RequireSrtPath(string? subtitlePath)
    {
        var path = subtitlePath?.Trim();
        if (string.IsNullOrEmpty(path))
            throw new BilibiliApiException("B站字幕：文件路径为空", -1);
        if (!path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            throw new BilibiliApiException(
                $"B站字幕只支持 .srt 文件（当前：{Path.GetFileName(path)}）", -1);
        }
        return path;
    }

    /// <summary>语言代码：空白 → <see cref="DefaultLan"/>（zh）</summary>
    public static string NormalizeLan(string? lan) =>
        string.IsNullOrWhiteSpace(lan) ? DefaultLan : lan.Trim();

    /// <summary>上传 URL —— 与封面同为 axios 编码（t → locale → device → csrf）。</summary>
    public static string BuildUploadUrl(string csrf, long timestampMs, string? apiHost = null)
        => BuildApiUrl(apiHost, UploadPath, csrf, timestampMs);

    /// <summary>草稿字幕绑定 URL（query 与上传同款）。</summary>
    public static string BuildPreSaveUrl(string csrf, long timestampMs, string? apiHost = null)
        => BuildApiUrl(apiHost, PreSavePath, csrf, timestampMs);

    private static string BuildApiUrl(string? apiHost, string path, string csrf, long timestampMs)
    {
        var host = string.IsNullOrWhiteSpace(apiHost)
            ? "https://api.bilibili.com"
            : apiHost.TrimEnd('/');
        return $"{host}{path}?t={timestampMs}"
               + $"&x-bili-locale-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.LocaleJson)}"
               + $"&x-bili-device-req-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.DeviceJson)}"
               + $"&csrf={BilibiliUploadService.AxiosEscape(csrf)}";
    }

    /// <summary>
    /// 上传表单（页面 <c>uploadSubtitle</c> 逐字）：<c>bucket</c> / <c>file</c> /
    /// <c>csrf</c> / <c>content_type</c>。文件 part 带文件名与 <c>application/x-subrip</c>；
    /// 其余是纯文本 part（浏览器 FormData 的文本字段不带 Content-Type）。
    /// </summary>
    public static MultipartFormDataContent BuildUploadForm(
        ReadOnlySpan<byte> bytes, string fileName, string csrf)
    {
        var form = new MultipartFormDataContent();
        form.Add(TextPart(SubtitleBucket), "bucket");

        var file = new ByteArrayContent(bytes.ToArray());
        file.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(SubtitleContentType);
        form.Add(file, "file", fileName);

        form.Add(TextPart(csrf), "csrf");
        form.Add(TextPart(SubtitleContentType), "content_type");
        return form;
    }

    /// <summary>
    /// 绑定表单（页面 <c>checkUploadSubtitle</c> 逐字）：
    /// <c>oid</c> / <c>type=1</c> / <c>files</c>（JSON 字符串）/ <c>aid=0</c> / <c>csrf</c>。
    /// </summary>
    public static MultipartFormDataContent BuildPreSaveForm(
        long cid, string lan, string location, string csrf)
    {
        var form = new MultipartFormDataContent
        {
            { TextPart(cid.ToString()), "oid" },
            { TextPart("1"), "type" },
            { TextPart(BuildFilesJson(location, lan)), "files" },
            { TextPart("0"), "aid" },
            { TextPart(csrf), "csrf" },
        };
        return form;
    }

    /// <summary>
    /// <c>files</c> 字段的 JSON 串：<c>[{"url":…,"lan":"zh","subtitle_id":0}]</c>
    /// （抓包逐字；<c>subtitle_id=0</c> = 新建）。
    /// </summary>
    public static string BuildFilesJson(string location, string lan)
        => JsonSerializer.Serialize(new[] { new SubtitleFileRef(location, lan, 0) });

    /// <summary>
    /// 解析上传响应 → <c>data.location</c>。
    /// 页面把 <c>https?:</c> 统一换成 <c>http:</c> 再往下用（抓包同款）。
    /// </summary>
    public static string ParseUploadLocation(string bodyText)
    {
        BilibiliUploadService.RequireCodeZero(bodyText, "字幕上传");

        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("location", out var locEl)
            || locEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(locEl.GetString()))
        {
            throw new BilibiliApiException(
                "B站字幕上传：响应里没有 data.location，原文：" + Snippet(bodyText), -1, bodyText);
        }

        return NormalizeLocation(locEl.GetString()!);
    }

    /// <summary><c>https://…</c> → <c>http://…</c>（页面 <c>replace(/https?:/,"http:")</c>）</summary>
    public static string NormalizeLocation(string location)
    {
        if (location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "http://" + location[8..];
        return location;
    }

    private static ByteArrayContent TextPart(string value)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
        content.Headers.ContentType = null;   // 与抓包同形（文本字段不带 Content-Type）
        return content;
    }

    private static string Snippet(string text) =>
        text.Length > 300 ? text[..300] + "…" : text;

    /// <summary><c>files</c> 里的一个元素（属性名与抓包一致，别改）</summary>
    private sealed record SubtitleFileRef(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("lan")] string Lan,
        [property: JsonPropertyName("subtitle_id")] int SubtitleId);
}
