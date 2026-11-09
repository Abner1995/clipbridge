using System.Net.Http.Headers;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手自定义封面 —— 上传本地封面图，换到平台侧的 <c>coverKey</c>。
///
/// 端点与请求形体来自页面 JS（<c>docs/kuaishou/js/20260616/index.ts</c>）：
/// <c>postCoverUploadV4({ file: File })</c> →
/// <c>POST /rest/cp/works/v4/video/pc/cover/edit/cover/upload</c>，multipart，字段名 <c>file</c>。
///
/// ⚠️ **该端点没有抓到真实请求/响应**（2026-11-05 抓包只覆盖到封面抽帧查询
/// <c>upload/cover/extract/query</c>）。因此响应解析只认两种常见形状
/// （<c>data.coverKey</c> / <c>data</c> 直接是字符串）；两种都不是就抛错并附原始响应，
/// 不猜字段名、不静默当成功。真实账号探针拿到响应形状后再收窄。
///
/// 提交时 <c>coverTimeStamp</c> 归 0：页面默认组合是 <c>coverType=1 / coverTimeStamp=0</c>，
/// 且 <c>upload.js</c> 里 <c>editCoverKeyRef.current</c> 会在 submit 前整体替换
/// <c>upload/finish</c> 返回的默认封面 Key —— 自定义封面替代「封面帧」，两者不叠加。
/// </summary>
public sealed class KuaishouCoverService
{
    /// <summary>封面图上传端点（页面常量 <c>POST_COVER_UPLOAD_V4</c>）</summary>
    public const string UploadPath = "/rest/cp/works/v4/video/pc/cover/edit/cover/upload";

    /// <summary>封面大小上限 —— upload/config 的 <c>coverMaxSize</c>（15 MB，2026-11-05 抓包）</summary>
    public const long MaxCoverBytes = 15728640;

    private readonly PlatformOptions _options;
    private readonly ILogger<KuaishouCoverService>? _logger;

    public KuaishouCoverService(PlatformOptions options, ILogger<KuaishouCoverService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 上传本地封面图 → 平台返回的 <c>coverKey</c>。
    /// 文件不存在 / 空文件 / 超过 15 MB / 响应形状认不出 → <see cref="KuaishouApiException"/>。
    /// </summary>
    public async Task<string> UploadAsync(
        string cookie, string filePath, string? apiPh = null, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new KuaishouApiException($"封面图不存在：{filePath}", -1);

        var info = new FileInfo(filePath);
        if (info.Length <= 0)
            throw new KuaishouApiException($"封面图是空文件：{filePath}", -1);
        if (info.Length > MaxCoverBytes)
            throw new KuaishouApiException(
                $"封面图 {info.Length / 1024.0 / 1024.0:0.#} MB，超过快手封面上限 "
                + $"{MaxCoverBytes / 1024 / 1024} MB", -1);

        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        // multipart 的 filename 统一成 ASCII —— 中文文件名容易出编码分歧，平台不看这个值
        var ext = Path.GetExtension(filePath);
        var fileName = "cover" + (string.IsNullOrEmpty(ext) ? ".jpg" : ext.ToLowerInvariant());

        // 15 MB 图片在慢上行链路上可能超过全局 30s 超时，单独放宽到 3 分钟
        using var client = new KuaishouApiClient(cookie, _options, apiPh,
            logger: _logger as ILogger<PlatformHttpClient>, timeout: TimeSpan.FromMinutes(3));
        using var form = BuildMultipart(fileName, GuessMimeType(filePath), bytes);
        var body = await client.PostMultipartTextAsync(UploadPath, form, ct);
        var key = ParseCoverKey(body);
        _logger?.LogInformation("[KuaishouCover] 上传成功：{File} → {CoverKey}",
            Path.GetFileName(filePath), key);
        return key;
    }

    /// <summary>
    /// 组装封面上传的 multipart 体（字段名 <c>file</c> —— 页面 <c>postCoverUploadV4({ file })</c>）。
    /// 纯函数，自检直接验 wire 形体。
    /// </summary>
    public static MultipartFormDataContent BuildMultipart(
        string fileName, string contentType, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return form;
    }

    /// <summary>
    /// 解析封面上传响应 → <c>coverKey</c>。响应形状未抓到，只认：
    /// ① <c>data.coverKey</c>（同族 JSON 接口的常见形状）；
    /// ② <c>data</c> 直接是字符串（某些上传接口直接回 Key）。
    /// <c>result</c> 存在且 ≠ 1 → 报错（带平台 message）；两种形状都不匹配 → 报错（带原始响应）。
    /// </summary>
    public static string ParseCoverKey(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            throw new KuaishouApiException("快手封面上传：响应为空", -1, bodyText);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bodyText);
        }
        catch (JsonException ex)
        {
            throw new KuaishouApiException(
                $"快手封面上传：响应不是 JSON（{ex.Message}），原文：{Snippet(bodyText)}", -1, bodyText);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new KuaishouApiException(
                    $"快手封面上传：响应结构认不出（根节点 {root.ValueKind}），原文：{Snippet(bodyText)}",
                    -1, bodyText);

            if (root.TryGetProperty("result", out var resultEl)
                && resultEl.ValueKind == JsonValueKind.Number
                && resultEl.GetInt32() != 1)
            {
                var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null;
                throw new KuaishouApiException(
                    $"快手封面上传失败：result={resultEl.GetInt32()}，msg={message ?? "?"}",
                    resultEl.GetInt32(), bodyText);
            }

            if (!root.TryGetProperty("data", out var data))
                throw new KuaishouApiException(
                    $"快手封面上传：响应没有 data，原文：{Snippet(bodyText)}", -1, bodyText);

            if (data.ValueKind == JsonValueKind.String)
            {
                var direct = data.GetString();
                if (!string.IsNullOrWhiteSpace(direct)) return direct;
            }
            else if (data.ValueKind == JsonValueKind.Object
                     && data.TryGetProperty("coverKey", out var keyEl)
                     && keyEl.ValueKind == JsonValueKind.String
                     && !string.IsNullOrWhiteSpace(keyEl.GetString()))
            {
                return keyEl.GetString()!;
            }

            throw new KuaishouApiException(
                "快手封面上传：响应里没有 coverKey（端点抓包未覆盖，形状可能变了），"
                + $"原文：{Snippet(bodyText)}", -1, bodyText);
        }
    }

    /// <summary>
    /// 提交时的封面取值：上传成功 → 新 Key + 帧归 0；未上传（或上传失败）→ 默认 Key + 用户填的帧。
    /// </summary>
    public static (string CoverKey, long? CoverFrameMs) ResolveCover(
        string defaultKey, string? uploadedKey, long? frameMs)
        => string.IsNullOrWhiteSpace(uploadedKey)
            ? (defaultKey, frameMs)
            : (uploadedKey, 0);

    /// <summary>封面图扩展名 → MIME（平台只校验大小与内容；未知扩展按 jpeg 发，与常见编辑器导出一致）。</summary>
    public static string GuessMimeType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/jpeg",
        };

    private static string Snippet(string text) =>
        text.Length > 300 ? text[..300] + "…" : text;
}
