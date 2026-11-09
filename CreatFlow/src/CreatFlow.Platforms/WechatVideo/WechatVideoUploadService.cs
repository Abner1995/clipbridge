namespace CreatFlow.Platforms.WechatVideo;

using System.Net;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.WechatVideo.Requests;
using Serilog;

/// <summary>
/// 微信视频号视频上传服务 — 对标 Python fangai/wechatvideo/upload.py 的 3 阶段管线。
///
/// 上传流程（finder.video.qq.com CDN 直传）：
///   1. applyUpload    — PUT  /applyuploaddfs          → 获取 UploadID
///   2. uploadChunks   — PUT  /uploadpartdfs           → 逐片上传 8MB 分片（Content-MD5 校验）
///   3. completeUpload — POST /completepartuploaddfs    → 获取 DownloadURL / httpsUrl
///
/// 认证方式：Authorization + X-Arguments header（非 AWS SigV4）
/// 分片校验：MD5（非 CRC32）
/// 上传方式：PUT（非 POST）
///
/// 关键差异 vs 抖音：
///   - 无 AWS SigV4，使用简单的 authKey 认证
///   - 分片大小 8MB（抖音 3MB）
///   - MD5 而非 CRC32
///   - 小文件（&lt;10MB）可用 simpleuploaddfs 单次上传
/// </summary>
public sealed class WechatVideoUploadService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<WechatVideoUploadService>? _logger;

    /// <summary>CDN 上传专用 JSON 序列化 — 对标 Python json.dumps，不转义 &lt; &gt; &amp; &quot;</summary>
    private static readonly JsonSerializerOptions CdnJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>文件类型名 — 对应 auth 上的属性名（video/picture/thumb/music）</summary>
    public string CdnFileType { get; set; } = "video";

    /// <summary>默认分片大小 8MB（与 upload.js 一致）</summary>
    public int ChunkSize { get; set; } = 8 * 1024 * 1024;

    /// <summary>单文件阈值 — 小于此值可用 simpleuploaddfs</summary>
    public int SingleFileSize { get; set; } = 10 * 1024 * 1024;

    /// <summary>默认 Referer</summary>
    private const string DefaultReferer = "https://channels.weixin.qq.com/micro/content/post/create";

    public WechatVideoUploadService(
        PlatformOptions options,
        ILogger<WechatVideoUploadService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    // ═══ Logging helpers ═══

    private void LogInfo(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogInformation(message, args);
        else Log.Information(message, args);
    }

    private void LogDebug(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogDebug(message, args);
        else Log.Debug(message, args);
    }

    private void LogWarn(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogWarning(message, args);
        else Log.Warning(message, args);
    }

    private void LogWarn(Exception ex, string message, params object[] args)
    {
        if (_logger is not null) _logger.LogWarning(ex, message, args);
        else Log.Warning(ex, message, args);
    }

    private void LogErr(string message, params object[] args)
    {
        if (_logger is not null) _logger.LogError(message, args);
        else Log.Error(message, args);
    }

    private void LogErr(Exception ex, string message, params object[] args)
    {
        if (_logger is not null) _logger.LogError(ex, message, args);
        else Log.Error(ex, message, args);
    }

    // ═══ Public API ═══

    /// <summary>
    /// 完整上传一个视频到微信视频号 CDN。
    ///
    /// 对标 Python: WechatVideoUploader.upload(filepath)
    ///
    /// 参数:
    ///   filePath         — 本地视频文件路径
    ///   auth             — 上传凭证（来自 WechatVideoAuthService.GetUploadParamsAsync）
    ///   onChunkProgress  — 分片进度回调
    ///   uuidStr          — 可选任务 UUID（null 则自动生成）
    ///
    /// 返回:
    ///   上传结果（含 httpsUrl），失败返回 null
    /// </summary>
    public async Task<WechatVideoUploadResult?> UploadAsync(
        string filePath,
        WechatVideoAuthResult auth,
        Func<int, int, long, long, Task>? onChunkProgress = null,
        string? uuidStr = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            LogErr("[WechatVideo] Video file not found: {Path}", filePath);
            return null;
        }

        var fileSize = new FileInfo(filePath).Length;
        var fileName = Path.GetFileName(filePath);
        var taskUuid = uuidStr ?? Guid.NewGuid().ToString();

        LogInfo("[WechatVideo] Starting upload: {File} ({Size} bytes, uuid={Uuid})",
            fileName, fileSize, taskUuid);

        try
        {
            // Step 1: applyUpload
            var uploadId = await ApplyUploadAsync(auth, filePath, taskUuid, ct);
            if (uploadId is null)
            {
                LogErr("[WechatVideo] applyUpload failed");
                return null;
            }

            // Step 2: uploadChunks
            var (partInfoList, transFlag) = await UploadChunksAsync(
                auth, filePath, uploadId, taskUuid, onChunkProgress, ct);
            if (partInfoList is null)
            {
                LogErr("[WechatVideo] uploadChunks failed");
                return null;
            }

            // Step 3: completeUpload
            var result = await CompleteUploadAsync(
                auth, filePath, uploadId, taskUuid, partInfoList, transFlag, ct);
            if (result is null)
            {
                LogErr("[WechatVideo] completeUpload failed");
                return null;
            }

            LogInfo("[WechatVideo] Upload complete: {Url}", result.HttpsUrl);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户暂停/应用退出 — 取消语义继续上抛，不当作上传失败（否则会误报服务端失败）
            throw;
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            // 取消时底层有时抛的是 HttpRequestException（连接被掐断）而不是 OCE ——
            // 统一改写成 OCE，别让队列把它当「上传失败」（2026-10-22 实测）。
            throw new OperationCanceledException("视频号上传已取消", ex, ct);
        }
        catch (Exception ex)
        {
            LogErr(ex, "[WechatVideo] Upload pipeline failed");
            return null;
        }
    }

    // ═══ Step 1: applyUpload ═══

    /// <summary>
    /// 向微信视频号 CDN 申请上传凭证。
    /// 对标 Python: WechatVideoUploader.apply_upload()
    /// </summary>
    private async Task<string?> ApplyUploadAsync(
        WechatVideoAuthResult auth,
        string filePath,
        string taskUuid,
        CancellationToken ct)
    {
        var fileSize = new FileInfo(filePath).Length;
        var fileName = Path.GetFileName(filePath);
        var chunkCount = (int)Math.Ceiling((double)fileSize / ChunkSize);
        var blockPartLengths = CalculateBlockPartLengths(fileSize, chunkCount);

        var url = $"{_options.WechatVideoCdnHost}/applyuploaddfs";
        var bodyObj = new { BlockSum = chunkCount, BlockPartLength = blockPartLengths };
        var bodyJson = JsonSerializer.Serialize(bodyObj, CdnJsonOptions);

        var headers = BuildCdnHeaders(auth, fileName, fileSize, taskUuid, md5Value: "null");
        headers["Content-Type"] = "application/json";
        headers["Referer"] = DefaultReferer;
        headers["Origin"] = "https://channels.weixin.qq.com";

        LogDebug("[WechatVideo] applyUpload → {Url}, chunks={Chunks}", url, chunkCount);

        using var http = CreateCdnClient();
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        foreach (var (k, v) in headers)
            req.Headers.TryAddWithoutValidation(k, v);

        var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            LogWarn("[WechatVideo] applyUpload HTTP {Code}: {Body}", (int)resp.StatusCode, Truncate(body));
            return null;
        }

        var data = JsonSerializer.Deserialize<ApplyUploadResponse>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var uploadId = data?.UploadID ?? data?.Data?.UploadID;

        if (string.IsNullOrWhiteSpace(uploadId))
        {
            LogWarn("[WechatVideo] applyUpload: no UploadID in response");
            return null;
        }

        LogInfo("[WechatVideo] applyUpload OK: UploadID={Id}", uploadId);
        return uploadId;
    }

    // ═══ Step 2: uploadChunks ═══

    /// <summary>
    /// 逐片上传视频分片到微信视频号 CDN。
    /// 对标 Python: WechatVideoUploader.upload_chunks()
    /// </summary>
    private async Task<(List<WechatVideoChunkInfo>? Parts, string TransFlag)> UploadChunksAsync(
        WechatVideoAuthResult auth,
        string filePath,
        string uploadId,
        string taskUuid,
        Func<int, int, long, long, Task>? onChunkProgress,
        CancellationToken ct)
    {
        var fileSize = new FileInfo(filePath).Length;
        var fileName = Path.GetFileName(filePath);
        var totalChunks = (int)Math.Ceiling((double)fileSize / ChunkSize);
        var partInfoList = new List<WechatVideoChunkInfo>(totalChunks);
        var transFlag = "";

        LogInfo("[WechatVideo] Uploading {Chunks} chunks ({Size} bytes, chunk={ChunkSize})",
            totalChunks, fileSize, ChunkSize);

        using var http = CreateCdnClient();
        var buffer = new byte[ChunkSize];
        await using var fs = File.OpenRead(filePath);
        long uploadedBytes = 0;

        for (int i = 0; i < totalChunks; i++)
        {
            var partNumber = i + 1;
            int read = await fs.ReadAsync(buffer, 0, ChunkSize, ct);
            if (read == 0) break;

            var chunkData = read == ChunkSize ? buffer : buffer[..read];
            var md5 = ComputeMd5(chunkData);

            var cdnHost = new Uri(_options.WechatVideoCdnHost).Host;
            var url = $"https://{cdnHost}/uploadpartdfs?PartNumber={partNumber}&UploadID={uploadId}&QuickUpload=2";

            var headers = BuildCdnHeaders(auth, fileName, fileSize, taskUuid, md5Value: md5);
            headers["Content-Type"] = "application/octet-stream";
            headers["Referer"] = DefaultReferer;

            // Retry up to 3 times
            var uploaded = false;
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Put, url)
                    {
                        Content = new ByteArrayContent(chunkData),
                    };
                    req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    foreach (var (k, v) in headers)
                        req.Headers.TryAddWithoutValidation(k, v);

                    var resp = await http.SendAsync(req, ct);
                    var body = await resp.Content.ReadAsStringAsync(ct);

                    if (resp.IsSuccessStatusCode)
                    {
                        var data = JsonSerializer.Deserialize<UploadPartResponse>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        var etag = data?.ETag ?? data?.Data?.ETag ?? "";
                        var chunkTrans = data?.TransFlag ?? data?.Data?.TransFlag ?? "";

                        if (!string.IsNullOrWhiteSpace(chunkTrans) && string.IsNullOrWhiteSpace(transFlag))
                            transFlag = chunkTrans;

                        partInfoList.Add(new WechatVideoChunkInfo(partNumber, etag));
                        uploaded = true;
                        break;
                    }

                    if ((int)resp.StatusCode is >= 400 and < 500)
                        break; // 4xx don't retry
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    // 取消时不重试、不吞 —— 否则最后一次重试会落成「上传失败」
                    throw new OperationCanceledException(ct);
                }
                catch (Exception ex)
                {
                    LogWarn("[WechatVideo] Chunk {Part} retry {Retry}/3: {Err}",
                        partNumber, retry + 1, ex.Message);
                }

                if (retry < 2) await Task.Delay(1000 * (retry + 1), ct);
            }

            if (!uploaded)
            {
                LogErr("[WechatVideo] Chunk {Part} failed after retries", partNumber);
                return (null, "");
            }

            uploadedBytes += read;

            LogDebug("[WechatVideo] Chunk {Part}/{Total} done ({Pct:F1}%)",
                partNumber, totalChunks, uploadedBytes * 100.0 / fileSize);

            if (onChunkProgress is not null)
                await onChunkProgress(partNumber, totalChunks, uploadedBytes, fileSize);
        }

        LogInfo("[WechatVideo] All {Count} chunks uploaded, transFlag={Flag}",
            partInfoList.Count, transFlag);
        return (partInfoList, transFlag);
    }

    // ═══ Step 3: completeUpload ═══

    /// <summary>
    /// 向微信视频号 CDN 提交上传结果。
    /// 对标 Python: WechatVideoUploader.complete_upload()
    /// </summary>
    private async Task<WechatVideoUploadResult?> CompleteUploadAsync(
        WechatVideoAuthResult auth,
        string filePath,
        string uploadId,
        string taskUuid,
        List<WechatVideoChunkInfo> partInfoList,
        string transFlag,
        CancellationToken ct)
    {
        var fileSize = new FileInfo(filePath).Length;
        var fileName = Path.GetFileName(filePath);

        var url = $"{_options.WechatVideoCdnHost}/completepartuploaddfs?UploadID={uploadId}";

        var xArgs = BuildXArguments(auth, fileName, fileSize, taskUuid);
        // 对标 Python upload.py: originalurl=0 → CDN 返回 encfilekey 格式 URL
        // originalurl=1 会导致 CDN 返回 filekey 格式（clip_video 不认）
        xArgs += "&originalurl=0";

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = auth.AuthKey,
            ["X-Arguments"] = xArgs,
            ["Content-MD5"] = "null",
            ["Content-Type"] = "application/json",
            ["Referer"] = DefaultReferer,
        };

        var sortedParts = partInfoList.OrderBy(p => p.PartNumber).ToList();
        var bodyObj = new
        {
            PartInfo = sortedParts.Select(p => new { PartNumber = p.PartNumber, ETag = p.ETag }).ToArray(),
            TransFlag = transFlag ?? "",
        };

        LogDebug("[WechatVideo] completeUpload → {Url} ({Count} parts)", url, sortedParts.Count);
        LogDebug("[WechatVideo] completeUpload X-Arguments: {Args}", xArgs);
        LogDebug("[WechatVideo] completeUpload auth: AppType={AppType}, VideoFileType={Vft}, Scene={Scene}, Uin={Uin}",
            auth.AppType, auth.VideoFileType, auth.Scene, auth.Uin);

        using var http = CreateCdnClient();
        var bodyJson = JsonSerializer.Serialize(bodyObj, CdnJsonOptions);
        LogDebug("[WechatVideo] completeUpload body: {Body}", bodyJson);
        // 对标 Python urllib: 只发 Content-Type: application/json（无 charset=utf-8）
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(bodyJson)),
        };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        foreach (var (k, v) in headers)
            req.Headers.TryAddWithoutValidation(k, v);

        var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            LogWarn("[WechatVideo] completeUpload HTTP {Code}: {Body}", (int)resp.StatusCode, Truncate(body));
            return null;
        }

        // Log raw response for debugging URL format issues
        LogDebug("[WechatVideo] completeUpload raw response: {Body}", Truncate(body, 1000));

        var data = JsonSerializer.Deserialize<CompleteUploadResponse>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Check for httpsUrl directly from CDN (newer format with encfilekey/findertoken)
        string? rawHttpsUrl = null;
        using (var doc = JsonDocument.Parse(body))
        {
            if (doc.RootElement.TryGetProperty("httpsUrl", out var hu) && hu.ValueKind == JsonValueKind.String)
                rawHttpsUrl = hu.GetString();
            else if (doc.RootElement.TryGetProperty("data", out var dataEl)
                && dataEl.TryGetProperty("httpsUrl", out var hu2) && hu2.ValueKind == JsonValueKind.String)
                rawHttpsUrl = hu2.GetString();
        }
        if (!string.IsNullOrWhiteSpace(rawHttpsUrl))
            LogDebug("[WechatVideo] completeUpload httpsUrl (direct): {Url}", rawHttpsUrl);

        var downloadUrl = data?.DownloadURL ?? data?.Data?.DownloadURL ?? "";

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            // Fallback: try httpsUrl from response
            if (!string.IsNullOrWhiteSpace(rawHttpsUrl))
            {
                LogInfo("[WechatVideo] completeUpload OK (httpsUrl): {Url}", rawHttpsUrl);
                return new WechatVideoUploadResult(rawHttpsUrl, rawHttpsUrl);
            }
            LogWarn("[WechatVideo] completeUpload: no DownloadURL in response");
            return null;
        }

        // 替换 http://wxapp.tc.qq.com → https://finder.video.qq.com
        var httpsUrl = downloadUrl.Replace(
            "http://wxapp.tc.qq.com", _options.WechatVideoReplaceHost);

        LogInfo("[WechatVideo] completeUpload OK: {Url}", httpsUrl);
        return new WechatVideoUploadResult(httpsUrl, downloadUrl);
    }

    // ═══ Private Helpers ═══

    /// <summary>
    /// 创建 CDN 上传专用 HttpClient — 强制 HTTP/1.1。
    /// finder.video.qq.com 的 HTTP/2 实现有问题，会返回非标准状态行导致
    /// "Received an invalid status line" 异常。Python urllib 只用 HTTP/1.1 因此不受影响。
    /// </summary>
    private static HttpClient CreateCdnClient()
    {
        var handler = new SocketsHttpHandler
        {
            // 不自动升级到 HTTP/2/3 — CDN 可能返回非标准响应
            EnableMultipleHttp2Connections = false,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    /// <summary>构建 X-Arguments header 值。对标 Python _build_x_arguments</summary>
    /// <remarks>filetype 按 CdnFileType 动态取值：video→VideoFileType, picture→PictureFileType</remarks>
    private string BuildXArguments(
        WechatVideoAuthResult auth,
        string fileName,
        long fileSize,
        string taskUuid)
    {
        var fileType = CdnFileType switch
        {
            "picture" => auth.PictureFileType,
            "thumb" => auth.ThumbFileType,
            _ => auth.VideoFileType,
        };
        return string.Join("&",
            $"apptype={auth.AppType}",
            $"filetype={fileType}",
            $"weixinnum={auth.Uin}",
            $"filekey={Uri.EscapeDataString(fileName)}",
            $"filesize={fileSize}",
            $"taskid={taskUuid}",
            $"scene={auth.Scene}");
    }

    /// <summary>构建 CDN 上传请求 headers。对标 Python _build_headers</summary>
    private Dictionary<string, string> BuildCdnHeaders(
        WechatVideoAuthResult auth,
        string fileName,
        long fileSize,
        string taskUuid,
        string md5Value)
    {
        return new Dictionary<string, string>
        {
            ["Authorization"] = auth.AuthKey,
            ["X-Arguments"] = BuildXArguments(auth, fileName, fileSize, taskUuid),
            ["Content-MD5"] = md5Value,
        };
    }

    /// <summary>计算每个分片的结束位置列表。对标 Python _calculate_block_part_lengths</summary>
    private static List<int> CalculateBlockPartLengths(long fileSize, int chunkCount)
    {
        var lengths = new List<int>(chunkCount);
        for (int i = 0; i < chunkCount; i++)
        {
            var end = (i + 1) * 8_388_608; // 8MB
            lengths.Add(i == chunkCount - 1 ? (int)fileSize : Math.Min(end, (int)fileSize));
        }
        return lengths;
    }

    /// <summary>计算 MD5 hex 字符串（小写）</summary>
    private static string ComputeMd5(byte[] data)
    {
        var hash = MD5.HashData(data);
        return Convert.ToHexStringLower(hash);
    }

    private static string Truncate(string? s, int max = 300)
        => s is null ? "(null)" : s.Length <= max ? s : s[..max] + "...";
}

// ═══ Internal Types ═══

/// <summary>分片信息 — 用于 completeUpload 的 PartInfo。</summary>
internal sealed record WechatVideoChunkInfo(
    int PartNumber,
    string ETag
);

/// <summary>
/// 上传完成结果 — completeUpload 返回的视频 URL。
/// </summary>
public sealed record WechatVideoUploadResult(
    string HttpsUrl,      // 替换后的 HTTPS URL
    string DownloadUrl    // 原始 DownloadURL
);
