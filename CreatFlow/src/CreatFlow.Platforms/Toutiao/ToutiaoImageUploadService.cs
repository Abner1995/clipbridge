using System.Net.Http.Headers;
using CreatFlow.Platforms.Toutiao.Requests;

namespace CreatFlow.Platforms.Toutiao;

/// <summary>头条图片上传结果（TOS URI + 原图尺寸，合集封面要用尺寸）。</summary>
public sealed record ToutiaoImageUploadResult(string Uri, int Width, int Height);

/// <summary>
/// 头条**文章合集**封面（POST /spice/image?upload_source=20020010 的回执）。
/// 文章合集不像视频合集只要一个 CoverUri，平台要求 cover_image 里带
/// <c>uri</c>（= origin_image_uri）、<c>originUri</c>（= image_uri）、<c>url</c> 与宽高。
/// </summary>
public sealed record ToutiaoArticleCover(
    string Uri,          // origin_image_uri —— 填 cover_image.uri（平台存这个）
    string OriginUri,    // image_uri        —— 填 cover_image.originUri
    string Url,          // origin_image_url —— cover_image.url
    int Width,
    int Height);

/// <summary>
/// 今日头条图片上传 — 合集封面用。
///
/// 2026-11-05 抓包（页面 VOD/VAS 上传 SDK）链路：
///   1. GET /xigua/api/upload/getAuthKey/?params={"type":"image_temp",...}
///      → data.accessKey + data.uploadToken（`HMAC-SHA1:1.0:expire:accessKey:sign`）
///   2. GET https://vas-lf-x.ixigua.com/video/openapi/v1/?action=GetImageUploadParams
///        &s={random}&use_edge_node=1&file_size={n}
///      Headers: X-TT-Access={accessKey} / Authorization={uploadToken}
///      → data.tos_host + data.tos_sign + data.object_ids[0]（bucket 由服务端预分配）
///   3. POST https://{tos_host}/{object_ids[0]}
///      Headers: Authorization={tos_sign} / Content-CRC32 / X-Storage-U={user_id} / Content-Disposition
///   4. `object_ids[0]` 就是 StoreUri（如 tos-cn-i-e874c6/{key}），直接填 AlbumInfo.CoverUri
///
/// ⚠️ 不能把 getAuthKey 的 uploadToken 当 TOS Authorization 用（2026-11-05 实测 TOS 401/4001）——
/// 那个 token 只用于 VAS 网关；TOS 要的是 GetImageUploadParams 现签的 tos_sign。
/// </summary>
public sealed class ToutiaoImageUploadService
{
    private const string UploadReferer = "https://mp.toutiao.com/profile_v4/xigua/upload-video";

    /// <summary>文章合集封面走 graphic 编辑器；upload_source=20020010 是合集封面的登记口。</summary>
    private const string ArticleCoverReferer =
        "https://mp.toutiao.com/profile_v4/graphic/collection-publish?from=collection_publish";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly PlatformOptions _options;
    private readonly ILogger<ToutiaoImageUploadService>? _logger;

    public ToutiaoImageUploadService(
        PlatformOptions options, ILogger<ToutiaoImageUploadService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>上传一张图片，返回 TOS URI 与原始宽高；失败返回 null（调用方给用户文案）。</summary>
    public async Task<ToutiaoImageUploadResult?> UploadAsync(
        string cookie, byte[] imageData, string uid, string csrfToken,
        CancellationToken ct = default)
    {
        if (imageData.Length == 0) return null;
        if (string.IsNullOrWhiteSpace(csrfToken)) return null;

        // ── 1. 拿 VAS 网关凭证（accessKey + uploadToken）＋ TOS 用的 user_id ──
        string? accessKey;
        string? uploadToken;
        using (var client = new ToutiaoApiClient(cookie, _options, csrfToken, UploadReferer))
        {
            var authUrl = _options.ToutiaoHost + "/xigua/api/upload/getAuthKey/?params=" +
                Uri.EscapeDataString(
                    "{\"type\":\"image_temp\",\"column\":false,\"ugc\":false,\"useImageX\":true,\"useStsToken\":false}");
            var auth = await client.GetJsonAsync<ToutiaoImageAuthResponse>(authUrl, ct);
            accessKey = auth?.Data?.AccessKey;
            uploadToken = auth?.Data?.UploadToken;
            if (auth is not { Status: 0 }
                || string.IsNullOrWhiteSpace(accessKey)
                || string.IsNullOrWhiteSpace(uploadToken))
            {
                Warn("Toutiao image getAuthKey failed: status={Status}, msg={Msg}",
                    auth?.Status, auth?.Message);
                return null;
            }

            // ── X-Storage-U 要的是**用户 ID**，不是 media.id（2026-11-05 实测） ──
            // 抓包账号：get_user_info.user_id = 470236218793111 = x-storage-u；
            // 而页面 pgc_info.media.id 是另一个值（本项目旧实现存的就是它）。
            // 兜底用传入的 uid（个别账号两者相同，或接口临时不可用时仍有机会成功）。
            uid = await TryGetUserIdAsync(client, uid, ct);
        }

        // ── 2. VAS 申请上传参数：tos_host + tos_sign + 预分配 object_id ──
        var uploadParams = await TryGetUploadParamsAsync(accessKey, uploadToken, imageData.Length, ct);
        var storeUri = uploadParams?.ObjectIds?.FirstOrDefault();
        if (uploadParams is null || string.IsNullOrWhiteSpace(storeUri)
            || string.IsNullOrWhiteSpace(uploadParams.TosSign))
        {
            Warn("Toutiao image upload: VAS returned no tos_sign/object_id");
            return null;
        }

        // ── 3. 带 tos_sign 直传 TOS ──
        var crc32 = Crc32Hex(imageData);
        var (width, height) = ReadImageSize(imageData);

        var hosts = new List<string>();
        foreach (var h in new[] { uploadParams.TosHost, _options.ToutiaoTosHost, _options.ToutiaoTosFallbackHost })
        {
            if (!string.IsNullOrWhiteSpace(h) && !hosts.Contains(h, StringComparer.OrdinalIgnoreCase))
                hosts.Add(h);
        }

        foreach (var host in hosts)
        {
            if (await TryUploadAsync(host, storeUri, imageData, uploadParams.TosSign, uid, crc32, ct))
            {
                _logger?.LogInformation("Toutiao image uploaded: {Uri} ({Width}x{Height})",
                    storeUri, width, height);
                return new ToutiaoImageUploadResult(storeUri, width, height);
            }
        }

        Warn("Toutiao image upload failed on all TOS edges: {Uri}", storeUri);
        return null;
    }

    /// <summary>
    /// 上传**文章合集封面**：POST /spice/image?upload_source=20020010（multipart 字段 image）。
    /// 返回的 <c>origin_image_uri</c> 是 cover_image.uri，<c>image_uri</c> 是 cover_image.originUri。
    /// 这条链路与视频合集封面（getAuthKey + VAS + TOS）不同，桶也不一样
    /// （文章是 tos-cn-i-6w9my0ksvp，视频是 tos-cn-i-e874c6）。
    /// </summary>
    public async Task<ToutiaoArticleCover?> UploadArticleCoverAsync(
        string cookie, byte[] imageData, string csrfToken, CancellationToken ct = default)
    {
        if (imageData.Length == 0) return null;
        if (string.IsNullOrWhiteSpace(csrfToken)) return null;

        using var client = new ToutiaoApiClient(cookie, _options, csrfToken, ArticleCoverReferer);
        var url = _options.ToutiaoHost +
                  "/spice/image?upload_source=20020010&aid=1231&device_platform=web";
        var (fileName, mime) = GuessImageFile(imageData);

        try
        {
            var resp = await client.PostMultipartAsync<ToutiaoSpiceImageResponse>(
                url, "image", imageData, fileName, mime, ct);

            if (resp is not { Code: 0 } || resp.Data is null
                || string.IsNullOrWhiteSpace(resp.Data.OriginImageUri))
            {
                Warn("Toutiao article cover /spice/image rejected: code={Code}, msg={Msg}",
                    resp?.Code, resp?.Message);
                return null;
            }

            Serilog.Log.Debug(
                "Toutiao article cover uploaded: uri={Uri}, origin={OriginUri}, {Width}x{Height}",
                resp.Data.OriginImageUri, resp.Data.ImageUri,
                resp.Data.ImageWidth, resp.Data.ImageHeight);

            return new ToutiaoArticleCover(
                Uri: resp.Data.OriginImageUri,
                OriginUri: resp.Data.ImageUri ?? resp.Data.OriginImageUri,
                Url: resp.Data.OriginImageUrl ?? resp.Data.ImageUrl ?? "",
                Width: resp.Data.ImageWidth,
                Height: resp.Data.ImageHeight);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Warn(ex, "Toutiao article cover upload failed");
            return null;
        }
    }

    /// <summary>按文件头猜 multipart 的文件名/MIME（平台只看图片内容，文件名只用于表单）。</summary>
    private static (string FileName, string Mime) GuessImageFile(byte[] d)
    {
        if (d.Length >= 3 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E)
            return ("cover.png", "image/png");
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
            return ("cover.jpg", "image/jpeg");
        if (d.Length >= 3 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F')
            return ("cover.gif", "image/gif");
        if (d.Length >= 12
            && d[0] == (byte)'R' && d[1] == (byte)'I' && d[2] == (byte)'F' && d[3] == (byte)'F'
            && d[8] == (byte)'W' && d[9] == (byte)'E' && d[10] == (byte)'B' && d[11] == (byte)'P')
            return ("cover.webp", "image/webp");
        return ("cover.jpg", "image/jpeg");
    }

    /// <summary>
    /// 向 VAS 网关申请图片上传参数（2026-11-05 页面 SDK 的真实链路）。
    /// <c>Authorization</c> 用 getAuthKey 的 uploadToken，<c>X-TT-Access</c> 用 accessKey；
    /// 返回的 <c>tos_sign</c> 才是 TOS 直传的 Authorization，<c>object_ids[0]</c> 是预分配的 StoreUri。
    /// </summary>
    private async Task<ToutiaoImageUploadParams?> TryGetUploadParamsAsync(
        string accessKey, string uploadToken, int fileSize, CancellationToken ct)
    {
        var random = RandomKey(10).ToLowerInvariant();
        foreach (var vasHost in new[] { _options.ToutiaoVasHost, _options.ToutiaoVasFallbackHost }
                     .Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var url = $"{vasHost}/video/openapi/v1/?action=GetImageUploadParams"
                      + $"&s={random}&use_edge_node=1&file_size={fileSize}";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds) };
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("X-TT-Access", accessKey);
                req.Headers.TryAddWithoutValidation("Authorization", uploadToken);
                req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store, must-revalidate");
                req.Headers.TryAddWithoutValidation("Referer", "https://mp.toutiao.com/");
                req.Headers.TryAddWithoutValidation("User-Agent",
                    _options.Environment?.UserAgent ?? _options.UserAgent);

                using var resp = await http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    Warn("Toutiao GetImageUploadParams {Host} → HTTP {Code}: {Body}",
                        vasHost, (int)resp.StatusCode, Snippet(body));
                    continue;
                }

                var parsed = JsonSerializer.Deserialize<ToutiaoImageUploadParamsResponse>(body, JsonOpts);
                if (parsed is not { Code: 2000 } || parsed.Data is null
                    || string.IsNullOrWhiteSpace(parsed.Data.TosSign))
                {
                    Warn("Toutiao GetImageUploadParams {Host} rejected: code={Code}, msg={Msg}, body={Body}",
                        vasHost, parsed?.Code, parsed?.Message, Snippet(body));
                    continue;
                }

                Serilog.Log.Debug(
                    "Toutiao GetImageUploadParams OK: tos_host={Host}, object={ObjectId}",
                    parsed.Data.TosHost, parsed.Data.ObjectIds?.FirstOrDefault());
                return parsed.Data;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Warn(ex, "Toutiao GetImageUploadParams {Host} exception", vasHost);
            }
        }
        return null;
    }

    /// <summary>
    /// 取当前登录用户的 <c>user_id</c>（TOS 的 X-Storage-U 要的是它，不是 media.id）。
    /// 接口失败/字段缺失时回落传入值（不让一个辅助请求阻断上传）。
    /// </summary>
    private async Task<string> TryGetUserIdAsync(
        ToutiaoApiClient client, string fallbackUid, CancellationToken ct)
    {
        try
        {
            var userId = await client.GetUserIdAsync(ct);
            if (string.IsNullOrWhiteSpace(userId))
            {
                Warn("Toutiao get_user_info has no user_id; X-Storage-U falls back to {Uid}",
                    fallbackUid);
                return fallbackUid;
            }
            if (userId != fallbackUid)
            {
                Serilog.Log.Debug("Toutiao X-Storage-U uses user_id={UserId} (不是 media.id={MediaId})",
                    userId, fallbackUid);
            }
            return userId;
        }
        catch (Exception ex)
        {
            Warn(ex, "Toutiao get_user_info failed; X-Storage-U falls back to {Uid}", fallbackUid);
            return fallbackUid;
        }
    }

    private async Task<bool> TryUploadAsync(
        string host, string storeUri, byte[] data, string uploadToken, string uid,
        string crc32, CancellationToken ct)
    {
        try
        {
            // VAS 返回的 tos_host 可能是裸域名（2026-11-05 实测：tos-d-x-lf.douyin.com），
            // 不补 scheme 会抛「An invalid request URI was provided」。
            var baseUrl = host.Contains("://", StringComparison.Ordinal) ? host : "https://" + host;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{storeUri}");
            req.Headers.TryAddWithoutValidation("Authorization", uploadToken);
            req.Headers.TryAddWithoutValidation("X-Storage-U", uid);
            req.Headers.TryAddWithoutValidation("Content-CRC32", crc32);
            // 抓包样本带 Referer/UA；TOS 防盗链不严但照抄更稳
            req.Headers.TryAddWithoutValidation("Referer", "https://mp.toutiao.com/");
            req.Headers.TryAddWithoutValidation("User-Agent",
                _options.Environment?.UserAgent ?? _options.UserAgent);

            // Content-Disposition 是**内容头**，加到请求头上会被 .NET 静默拒绝。
            var content = new ByteArrayContent(data);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.TryAddWithoutValidation("Content-Disposition",
                "attachment; filename=\"undefined\"");
            req.Content = content;

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Warn("Toutiao TOS {Host} → HTTP {Code}: {Body}",
                    host, (int)resp.StatusCode, Snippet(body));
                return false;
            }

            // 成功壳：{"Version":"","success":0,"error":{"code":200,...},"payload":{"key":"..."}}
            // 返回的 key 是对象名（不含 bucket 前缀）；StoreUri 形如 tos-cn-i-e874c6/{key}。
            var objectKey = storeUri.Contains('/')
                ? storeUri[(storeUri.IndexOf('/') + 1)..]
                : storeUri;
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("payload", out var payload)
                && payload.TryGetProperty("key", out var keyEl)
                && keyEl.GetString() == objectKey)
                return true;

            var errorCode = root.TryGetProperty("error", out var err)
                && err.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : -1;
            Warn("Toutiao TOS {Host} rejected: errorCode={ErrorCode}, body={Body}",
                host, errorCode, Snippet(body));
            return errorCode == 200;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Warn(ex, "Toutiao TOS {Host} upload exception", host);
            return false;
        }
    }

    /// <summary>
    /// 桌面端构造时不传 ILogger（见 CollectionSyncService），失败细节原本全被吞掉；
    /// 没有注入 logger 时退回 Serilog，保证日志文件里能看到 TOS 的原始响应。
    /// </summary>
    private void Warn(string template, params object?[] args)
    {
        if (_logger is not null) _logger.LogWarning(template, args);
        else Serilog.Log.Warning(template, args);
    }

    private void Warn(Exception ex, string template, params object?[] args)
    {
        if (_logger is not null) _logger.LogWarning(ex, template, args);
        else Serilog.Log.Warning(ex, template, args);
    }

    private static string Snippet(string s) => s.Length <= 400 ? s : s[..400];

    /// <summary>40 位 [A-Za-z0-9] key（抓包样本同长度；平台只看 key 唯一性）。</summary>
    private static string RandomKey(int length)
    {
        const string alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }

    /// <summary>CRC32 十六进制（TOS 上传校验用，与抖音实现同口径）。</summary>
    private static string Crc32Hex(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return (~crc).ToString("x8");
    }

    /// <summary>
    /// 读图片宽高（PNG / JPEG / GIF / BMP / WebP），失败退 1280×720
    /// —— 头条合集封面要求非 0；尺寸只影响展示裁切，不阻断创建。
    /// 需要「认不出就拒绝」的场景用 <see cref="TryReadImageSize"/>。
    /// </summary>
    internal static (int Width, int Height) ReadImageSize(byte[] d)
        => TryReadImageSize(d, out var w, out var h) ? (w, h) : (1280, 720);

    /// <summary>
    /// 认图片并读出宽高（JPEG / PNG / GIF / BMP / WebP）；认不出 → false（不猜默认值）。
    /// 视频封面必填，选错文件要给出明确文案而不是把任意字节发到 TOS。
    /// </summary>
    internal static bool TryReadImageSize(byte[] d, out int width, out int height)
    {
        // PNG: 89 50 4E 47 ... IHDR width/height 16..23 big-endian
        if (d.Length >= 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
        {
            (width, height) = (BE32(d, 16), BE32(d, 20));
            return true;
        }

        // GIF: width/height 6..9 little-endian
        if (d.Length >= 10 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F')
        {
            (width, height) = (LE16(d, 6), LE16(d, 8));
            return true;
        }

        // BMP: width at 18, height at 22 (signed)
        if (d.Length >= 26 && d[0] == (byte)'B' && d[1] == (byte)'M')
        {
            (width, height) = (BitConverter.ToInt32(d, 18), Math.Abs(BitConverter.ToInt32(d, 22)));
            return true;
        }

        // JPEG: 扫 SOF0/SOF2
        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                var marker = d[i + 1];
                if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7)
                {
                    i += 2;
                    continue;
                }
                var len = (d[i + 2] << 8) | d[i + 3];
                if (marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC)
                {
                    (width, height) = (BE16(d, i + 7), BE16(d, i + 5));
                    return true;
                }
                if (len <= 0) break;
                i += 2 + len;
            }
        }

        // WebP: VP8X / VP8
        if (d.Length >= 30 && d[0] == (byte)'R' && d[1] == (byte)'I'
            && d[2] == (byte)'F' && d[3] == (byte)'F'
            && d[8] == (byte)'W' && d[9] == (byte)'E' && d[10] == (byte)'B' && d[11] == (byte)'P')
        {
            var chunk = Encoding.ASCII.GetString(d, 12, 4);
            if (chunk == "VP8X")
            {
                (width, height) = (1 + (d[24] | d[25] << 8 | d[26] << 16),
                                   1 + (d[27] | d[28] << 8 | d[29] << 16));
                return true;
            }
            if (chunk == "VP8 ")
            {
                (width, height) = ((d[26] | d[27] << 8) & 0x3FFF,
                                   (d[28] | d[29] << 8) & 0x3FFF);
                return true;
            }
        }

        width = 0;
        height = 0;
        return false;
    }

    private static int BE16(byte[] d, int i) => (d[i] << 8) | d[i + 1];
    private static int BE32(byte[] d, int i) => (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];
    private static int LE16(byte[] d, int i) => d[i] | (d[i + 1] << 8);
}
