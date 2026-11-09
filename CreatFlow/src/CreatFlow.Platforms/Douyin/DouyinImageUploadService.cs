namespace CreatFlow.Platforms.Douyin;

/// <summary>
/// 抖音图片上传服务 — 将图片上传到抖音 ImageX/TOS，返回 TOS 路径。
///
/// 从 DouyinCollectionService 抽离，单一职责：图片 → TOS。
/// Collections 和 Videos 需要封面图时都通过本服务上传。
///
/// API 端点：
///   ApplyImageUpload:  GET  imagex.bytedanceapi.com/?Action=ApplyImageUpload  (AWS SigV4)
///   TOS PUT:           PUT  tos-d-x-lf.douyin.com/upload/v1/{StoreUri}        (TOS Auth)
///   CommitImageUpload: POST imagex.bytedanceapi.com/?Action=CommitImageUpload  (AWS SigV4)
///
/// 三步管线对标 Python examples/douyin/collection/createcollection.py：
///   1. ApplyImageUpload — 获取 TOS 上传凭证
///   2. TOS PUT — 上传图片二进制数据 + CRC32 校验
///   3. CommitImageUpload — 确认上传完成
///
/// 返回 TOS 路径（如 tos-cn-i-jm8ajry58r/xxx），可用于:
///   - 合集 create/update 的 mix_cover 字段
///   - 发视频时的自定义封面
/// </summary>
public sealed class DouyinImageUploadService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<DouyinImageUploadService>? _logger;

    /// <summary>ImageX ServiceId — 从浏览器抓包获取，可能随版本变化</summary>
    private const string ImageXServiceId = "jm8ajry58r";
    private const string ImageXVersion = "2018-08-01";
    private const string ImageXRegion = "cn-north-1";

    public DouyinImageUploadService(
        PlatformOptions options,
        ILogger<DouyinImageUploadService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    // ═══ Public API ═══

    /// <summary>
    /// 上传图片到抖音 TOS（ImageX）。
    ///
    /// 三步管线：ApplyImageUpload → TOS PUT → CommitImageUpload
    /// </summary>
    /// <param name="imageData">图片二进制数据</param>
    /// <param name="fileName">文件名（用于 Content-Disposition）</param>
    /// <param name="stsAccessKey">STS AccessKey（来自 DouyinAuthService.RefreshStsAsync）</param>
    /// <param name="stsSecretKey">STS SecretKey</param>
    /// <param name="stsSessionToken">STS SessionToken</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>TOS 路径（StoreUri），如 tos-cn-i-jm8ajry58r/xxx；上传失败返回 null</returns>
    public async Task<string?> UploadAsync(
        byte[] imageData,
        string fileName,
        string stsAccessKey,
        string stsSecretKey,
        string stsSessionToken,
        CancellationToken ct = default)
    {
        // Step 1: ApplyImageUpload
        var applyResult = await ApplyImageUploadAsync(
            stsAccessKey, stsSecretKey, stsSessionToken, ct);
        if (applyResult is null)
            return null;

        // Step 2: Upload to TOS
        var uploadOk = await UploadToTosAsync(imageData, applyResult, fileName, ct);
        if (!uploadOk)
            return null;

        // Step 3: CommitImageUpload
        var commitOk = await CommitImageUploadAsync(
            applyResult, stsAccessKey, stsSecretKey, stsSessionToken, ct);
        if (!commitOk)
            return null;

        // 返回 StoreUri（如 tos-cn-i-jm8ajry58r/xxx）
        return applyResult.StoreInfos?.FirstOrDefault()?.StoreUri;
    }

    // ═══ Step 1: ApplyImageUpload ═══

    /// <summary>
    /// 申请图片上传凭证。
    /// 对标: GET imagex.bytedanceapi.com/?Action=ApplyImageUpload
    /// </summary>
    private async Task<DouyinUploadAddress?> ApplyImageUploadAsync(
        string stsAccessKey,
        string stsSecretKey,
        string stsSessionToken,
        CancellationToken ct)
    {
        // Handler 管道: StsAuth → AwsSigV4 → HTTP
        var stsHandler = new Core.Handlers.StsAuthHandler(
            stsAccessKey, stsSecretKey, stsSessionToken);
        var sigHandler = new AwsSigV4Handler(
            stsAccessKey, stsSecretKey, ImageXRegion, "imagex");
        stsHandler.InnerHandler = sigHandler;
        sigHandler.InnerHandler = new HttpClientHandler();

        using var client = new PlatformHttpClient(stsHandler, _options, null);

        var qs = new Dictionary<string, string>
        {
            ["Action"] = "ApplyImageUpload",
            ["Version"] = ImageXVersion,
            ["ServiceId"] = ImageXServiceId,
            ["app_id"] = "2906",
            ["user_id"] = "",
            ["s"] = GenerateRandomS(),
        };
        var queryString = string.Join("&",
            qs.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{_options.DouyinImageXHost}/?{queryString}";

        try
        {
            var json = await client.GetStringAsync(url, ct);
            var resp = JsonSerializer.Deserialize<DouyinApplyImageUploadResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return resp?.Result?.UploadAddress;
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "ApplyImageUpload HTTP error");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ApplyImageUpload error: {Msg}", ex.Message);
            return null;
        }
    }

    // ═══ Step 2: Upload to TOS ═══

    /// <summary>
    /// 上传图片二进制数据到 TOS。
    /// 对标: PUT tos-d-x-lf.douyin.com/upload/v1/{StoreUri} + Content-CRC32
    /// </summary>
    private async Task<bool> UploadToTosAsync(
        byte[] imageData,
        DouyinUploadAddress addr,
        string fileName,
        CancellationToken ct)
    {
        var storeInfo = addr.StoreInfos?.FirstOrDefault();
        var storeUri = storeInfo?.StoreUri ?? "";
        var auth = storeInfo?.Auth ?? "";

        var host = addr.UploadHosts?.FirstOrDefault() ?? _options.DouyinTosHost;
        var url = $"https://{host}/upload/v1/{storeUri}";

        // CRC32
        var crc32 = Crc32CHex(imageData);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var content = new ByteArrayContent(imageData);
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Headers.TryAddWithoutValidation("Content-CRC32", crc32);
        // ⚠️ Content-Disposition 是 HTTP 头，只能放 ASCII：封面文件名经常是中文（如「封面_16x9.jpg」、
        //    「萌宠指南.jpg」），原样拼进来 HttpClient 会直接抛 "Request headers must contain only
        //    ASCII characters."（2026-10-22 用户实测抖音合集同步失败，3 平台成功、抖音报这个）。
        //    TOS 不依赖这个文件名，统一换成安全 ASCII 名。
        content.Headers.TryAddWithoutValidation("Content-Disposition",
            $"attachment; filename=\"{ToAsciiFileName(fileName)}\"");
        content.Headers.TryAddWithoutValidation("X-Storage-U", "");

        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.TryAddWithoutValidation("Authorization", auth);

        var resp = await http.SendAsync(req, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger?.LogWarning("TOS image PUT failed: HTTP {Code}, body={Body}",
                (int)resp.StatusCode, respBody[..Math.Min(200, respBody.Length)]);
        }
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// 文件名 → 只含可见 ASCII 的安全名（中文等非 ASCII 字符、引号、反斜杠都换成 <c>_</c>）。
    /// 全空/全是下划线时回退 <c>cover.jpg</c>。HTTP 头只允许 ASCII，中文名必须过这一道。
    /// </summary>
    private static string ToAsciiFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        var chars = new char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            chars[i] = ch is >= ' ' and <= '~' && ch != '"' && ch != '\\' ? ch : '_';
        }
        var safe = new string(chars).Trim();
        return safe.Length == 0 || safe.All(c => c == '_') || safe.All(c => c is '_' or '.')
            ? "cover.jpg"
            : safe;
    }

    // ═══ Step 3: CommitImageUpload ═══

    /// <summary>
    /// 确认图片上传完成。
    /// 对标: POST imagex.bytedanceapi.com/?Action=CommitImageUpload
    /// </summary>
    private async Task<bool> CommitImageUploadAsync(
        DouyinUploadAddress addr,
        string stsAccessKey,
        string stsSecretKey,
        string stsSessionToken,
        CancellationToken ct)
    {
        // Handler 管道: StsAuth → AwsSigV4 → HTTP
        var stsHandler = new Core.Handlers.StsAuthHandler(
            stsAccessKey, stsSecretKey, stsSessionToken);
        var sigHandler = new AwsSigV4Handler(
            stsAccessKey, stsSecretKey, ImageXRegion, "imagex");
        stsHandler.InnerHandler = sigHandler;
        sigHandler.InnerHandler = new HttpClientHandler();

        using var client = new PlatformHttpClient(stsHandler, _options, null);

        var qs = new Dictionary<string, string>
        {
            ["Action"] = "CommitImageUpload",
            ["Version"] = ImageXVersion,
            ["ServiceId"] = ImageXServiceId,
            ["app_id"] = "2906",
            ["user_id"] = "",
        };
        var queryString = string.Join("&",
            qs.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{_options.DouyinImageXHost}/?{queryString}";

        // 使用 API 返回的 SessionKey
        if (string.IsNullOrWhiteSpace(addr.SessionKey))
        {
            _logger?.LogWarning("CommitImageUpload: SessionKey is null from ApplyImageUpload");
            return false;
        }

        var bodyJson = JsonSerializer.Serialize(new { SessionKey = addr.SessionKey });

        var json = await client.PostJsonRawAsync(url, bodyJson, ct);
        var resp = JsonSerializer.Deserialize<DouyinCommitImageUploadResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return resp?.Result?.Results?.FirstOrDefault()?.Uri != null;
    }

    // ═══ Private Helpers ═══

    /// <summary>生成 ApplyImageUpload 的随机 s 参数。</summary>
    private static string GenerateRandomS()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(10);
        for (int i = 0; i < 10; i++)
            sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }

    /// <summary>CRC32 十六进制字符串（TOS 上传校验用）。</summary>
    private static string Crc32CHex(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return (~crc).ToString("x8");
    }
}
