using System.Security.Cryptography;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书上传统具 — COS/ROS 分片上传管线。
///
/// 对标 Python fangai/xiaohongshu/upload.py 的 XhsUploader + _ros_upload。
///
/// 流程（4 阶段）：
///   1. Permit       — GET /api/media/v1/upload/creator/permit → STS 凭证 + COS 上传地址
///   2. COS Upload   — PUT https://{uploadAddr}/spectrum/{fileKey}
///                     算法: HMAC-SHA1 q-signature (兼容腾讯云 COS)
///                     >5MB 分片上传 (init → parts → complete)
///   3. VideoId      — GET /web_api/sns/capa/postgw/videoid
///   4. Transcode    — Poll GET /web_api/sns/capa/postgw/query_transcode
/// </summary>
public sealed class XhsUploadService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<XhsUploadService>? _logger;

    public int ChunkSize { get; set; } = 5 * 1024 * 1024;  // 5 MB
    public int MaxRetries { get; set; } = 3;

    /// <summary>转码轮询上限（次数）。默认值会在 <see cref="UploadAsync"/> 里按
    /// <see cref="TranscodePollCount"/> 抬高 —— 固定 30 次（90 秒）连 43 秒的片子都不够（2026-11-09 实测）。</summary>
    public int TranscodePollMax { get; set; } = 30;
    public int TranscodePollIntervalMs { get; set; } = 3000;

    /// <summary>转码轮询间隔（毫秒）基准 —— 与 <see cref="TranscodePollIntervalMs"/> 的默认值一致。</summary>
    public const int DefaultTranscodePollIntervalMs = 3000;

    /// <summary>
    /// 转码轮询预算（次数，间隔 <see cref="DefaultTranscodePollIntervalMs"/> = 3 秒）。
    ///
    /// 2026-11-09 真账号实测：43 秒 / 29MB 的视频在旧的固定 30 次（90 秒）里
    /// 始终 `status=0, progress=0`，靠「转码超时非致命」兜底才发出去 ——
    /// 小视频靠运气、大视频更没戏。预算 = 3 分钟基线 + 0.6×时长
    /// （与视频号 clip 轮询同一口径：转码约 5 倍速）；时长未知按体积回落
    /// （每 MB 约 1 秒），夹在 [40, 400] 次 = 2 ~ 20 分钟。
    /// </summary>
    public static int TranscodePollCount(double? durationSeconds, long fileSizeBytes)
    {
        var expectedSeconds = 180
            + (durationSeconds is > 0
                ? durationSeconds.Value * 0.6
                : Math.Max(0, fileSizeBytes / 1_000_000.0));
        var count = (int)Math.Ceiling(expectedSeconds / (DefaultTranscodePollIntervalMs / 1000.0));
        return Math.Clamp(count, 40, 400);
    }

    public XhsUploadService(PlatformOptions options, ILogger<XhsUploadService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    // 日志落点：桌面端 new XhsUploadService(options) 不传 logger，而这里原来只用 _logger?.LogXxx
    // —— permit / COS 分片 / videoId / 转码轮询的细节一条都进不了日志（2026-10-25 排障时全是盲区）。
    // 与 WechatVideoContentService 同款兜底：没有注入 logger 就退到 Serilog 静态写法。
    private void LogInfo(string message, params object?[] args)
    {
        if (_logger is not null) _logger.LogInformation(message, args);
        else Serilog.Log.Information(message, args);
    }

    private void LogWarn(string message, params object?[] args)
    {
        if (_logger is not null) _logger.LogWarning(message, args);
        else Serilog.Log.Warning(message, args);
    }

    private void LogWarn(Exception ex, string message, params object?[] args)
    {
        if (_logger is not null) _logger.LogWarning(ex, message, args);
        else Serilog.Log.Warning(ex, message, args);
    }

    private void LogDebug(string message, params object?[] args)
    {
        if (_logger is not null) _logger.LogDebug(message, args);
        else Serilog.Log.Debug(message, args);
    }

    /// <summary>
    /// 完整上传视频到小红书（不含发布）。
    /// </summary>
    /// <param name="cookie">Cookie 字符串</param>
    /// <param name="filePath">本地视频文件路径</param>
    /// <param name="onProgress">进度回调 (current, total, bytes, totalBytes)</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>上传结果（fileId, videoId, previewUrl），失败抛异常</returns>
    public async Task<XhsUploadResult> UploadAsync(
        string cookie, string filePath,
        Func<int, int, long, long, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);

        var fileSize = new FileInfo(filePath).Length;
        var fileName = Path.GetFileName(filePath);
        var ext = Path.GetExtension(fileName).ToLowerInvariant().TrimStart('.');

        // 轮询预算按时长/体积抬高（调用方已调得更大时不动它）—— 见 TranscodePollCount 的实测注释
        var plannedPolls = TranscodePollCount(null, fileSize);
        if (TranscodePollMax < plannedPolls) TranscodePollMax = plannedPolls;
        LogInfo("XHS transcode poll budget: {Polls} 次 × {Sec}s（文件 {MB} MB）",
            TranscodePollMax, TranscodePollIntervalMs / 1000, Math.Round(fileSize / 1_000_000.0, 1));

        LogInfo("[XHS Upload Step 1/4] Getting upload permit (scene=video)...");
        using var apiClient = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        // Step 1: Permit (use raw API to handle XHS "data" envelope)
        var permitParams = new Dictionary<string, string>
        {
            ["biz_name"] = "spectrum",
            ["scene"] = "video",
            ["file_count"] = "1",
            ["version"] = "1",
            ["source"] = "web",
        };
        if (!string.IsNullOrEmpty(ext))
            permitParams["file_format"] = ext;

        var permitResp = await apiClient.GetCreatorRawAsync(
            "/api/media/v1/upload/creator/permit", permitParams, ct);
        var permit = ParseFirstPermit(permitResp);

        var fileId = permit.FileId;          // e.g. "spectrum/abc123..."
        var fileKey = permit.FileKey;
        var uploadAddr = permit.UploadAddr;
        var token = permit.Token;

        LogInfo("XHS permit: uploadAddr={Addr}, fileId={Fid}, bucket={Bucket}, region={Region}",
            uploadAddr, fileId, permit.Bucket, permit.Region);

        // Step 2: COS Upload
        LogInfo("[XHS Upload Step 2/4] COS upload {Size} bytes to {Addr}...", fileSize, uploadAddr);

        var cosResult = fileSize > ChunkSize
            ? await CosMultipartUploadAsync(filePath, fileSize, fileKey, uploadAddr, token, onProgress, ct)
            : await CosDirectUploadAsync(filePath, fileSize, fileKey, uploadAddr, token, ct);

        var videoId = cosResult.VideoId;
        var previewUrl = cosResult.PreviewUrl;

        LogInfo("XHS COS upload done: videoId={Vid}, preview={Preview}",
            videoId, previewUrl?[..Math.Min(80, previewUrl?.Length ?? 0)]);

        // Step 3: VideoId (if not returned by COS)
        if (string.IsNullOrWhiteSpace(videoId))
        {
            LogInfo("[XHS Upload Step 3/4] Getting videoId...");
            try
            {
                var vidResp = await apiClient.GetEdithAsync<XhsVideoIdResponse>(
                    "/web_api/sns/capa/postgw/videoid",
                    new Dictionary<string, string>
                    {
                        ["fileKey"] = fileKey,
                        ["bizName"] = "spectrum",
                    }, ct);
                videoId = vidResp?.VideoId ?? "";
                LogInfo("XHS videoId: {Vid}", videoId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户暂停/撤回：取消不吞（否则会带着空 videoId 继续往下走）
            }
            catch (Exception ex)
            {
                LogWarn(ex, "XHS videoId lookup failed (non-fatal)");
            }
        }

        // Step 4: Transcode poll (optional — publishing may handle this server-side)
        if (!string.IsNullOrWhiteSpace(videoId))
        {
            LogInfo("[XHS Upload Step 4/4] Waiting for transcode...");
            var transcoded = await PollTranscodeAsync(apiClient, videoId, ct);
            if (!transcoded)
                LogWarn("XHS transcode poll timed out or failed; publish may still succeed");
        }

        return new XhsUploadResult(
            FileId: fileId,
            VideoId: videoId ?? "",
            PreviewUrl: previewUrl ?? cosResult.StaticUrl ?? "",
            FileKey: fileKey);
    }

    // ═══ 图片上传（自定义封面，2.5.3）═══

    /// <summary>
    /// 上传一张图片（自定义封面）→ 返回 permit 分配的 <c>spectrum/...</c> fileId。
    ///
    /// 与视频共用同一条 permit + ROS PUT 通道，只有两处不同：
    ///   · permit 用 <c>scene=image&amp;file_count=1</c>（2026-11-05 抓包，**不带 file_format**）；
    ///   · PUT <c>https://{uploadAddr}/spectrum/{fileKey}</c>，Content-Type 按图片扩展名
    ///     （抓包 <c>image/png</c>）。
    ///
    /// 发布体只认 <c>video_info.cover.file_id</c>（接口说明书 §4.4）—— 调用方把返回值
    /// 填进 <see cref="XhsPublishRequest.CoverFileId"/>，帧设置同时归 0（见
    /// <see cref="XhsPublishService.ResolveCover"/>）。
    /// </summary>
    public async Task<string> UploadImageAsync(
        string cookie, string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException(filePath);

        var fileSize = new FileInfo(filePath).Length;
        if (fileSize <= 0)
            throw new XhsPublishException($"封面图是空文件：{filePath}");

        using var apiClient = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        LogInfo("[XHS Image] Getting upload permit (scene=image)...");
        var permitResp = await apiClient.GetCreatorRawAsync(
            "/api/media/v1/upload/creator/permit", BuildImagePermitQuery(), ct);
        var permit = ParseFirstPermit(permitResp);

        LogInfo("[XHS Image] ROS upload {Size} bytes → {Addr}/{Fid}",
            fileSize, permit.UploadAddr, permit.FileId);
        await CosDirectUploadAsync(filePath, fileSize, permit.FileKey,
            permit.UploadAddr, permit.Token, ct);
        LogInfo("[XHS Image] uploaded: fileId={Fid}", permit.FileId);
        return permit.FileId;
    }

    /// <summary>
    /// 图片 permit 的 query —— 2026-11-05 抓包：
    /// <c>biz_name=spectrum&amp;scene=image&amp;file_count=1&amp;version=1&amp;source=web</c>，
    /// 与视频不同**不带 file_format**。纯函数，自检直接断言。
    /// </summary>
    public static Dictionary<string, string> BuildImagePermitQuery() => new()
    {
        ["biz_name"] = "spectrum",
        ["scene"] = "image",
        ["file_count"] = "1",
        ["version"] = "1",
        ["source"] = "web",
    };

    /// <summary>permit 响应里的第一组上传凭证（视频 / 图片共用同一条通道）。</summary>
    public sealed record XhsPermitInfo(
        string FileId, string FileKey, string UploadAddr, string Token,
        string Bucket, string Region);

    /// <summary>
    /// 解析 permit 响应 → 第一组凭证。响应外层可能是
    /// <c>{"code":0,"success":true,"data":{...}}</c>，也可能已剥壳；两种都认。
    /// 纯函数（零网络），自检按 2026-11-05 抓包形状钉住。
    /// </summary>
    public static XhsPermitInfo ParseFirstPermit(JsonElement permitResp)
    {
        // XHS wraps responses in {"code":0,"success":true,"data":{...}}
        var permitJson = permitResp;
        if (permitResp.TryGetProperty("data", out var dataEl) &&
            dataEl.TryGetProperty("uploadTempPermits", out _))
            permitJson = dataEl;

        var permits = permitJson.TryGetProperty("uploadTempPermits", out var permitsArr) &&
            permitsArr.ValueKind == JsonValueKind.Array && permitsArr.GetArrayLength() > 0
            ? permitsArr[0]
            : throw new XhsPublishException("XHS permit: no uploadTempPermits in response");

        var fileIdsArr = permits.TryGetProperty("fileIds", out var fids) &&
            fids.ValueKind == JsonValueKind.Array ? fids : default;
        if (fileIdsArr.ValueKind != JsonValueKind.Array || fileIdsArr.GetArrayLength() == 0)
            throw new XhsPublishException("XHS permit: no fileIds returned");

        var fileId = fileIdsArr[0].GetString()!;  // e.g. "spectrum/abc123..."
        var fileKey = fileId.Contains('/') ? fileId.Split('/', 2)[1] : fileId;
        return new XhsPermitInfo(
            FileId: fileId,
            FileKey: fileKey,
            UploadAddr: TryGetStr(permits, "uploadAddr") ?? "",
            Token: TryGetStr(permits, "token") ?? "",
            Bucket: TryGetStr(permits, "bucket") ?? "",
            Region: TryGetStr(permits, "region") ?? "");
    }

    // ═══ COS Direct Upload (≤5MB) ═══

    private async Task<CosUploadResult> CosDirectUploadAsync(
        string filePath, long fileSize, string fileKey, string uploadAddr, string token,
        CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        var (accessKey, deadline) = ParseRosToken(token);
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var auth = ComputeRosSignature(accessKey, fileKey, fileSize, uploadAddr, startTime, deadline);

        var url = $"https://{uploadAddr}/spectrum/{fileKey}";
        var mimeType = GuessMimeType(Path.GetExtension(filePath));

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                using var content = new StreamContent(File.OpenRead(filePath));
                content.Headers.TryAddWithoutValidation("Content-Type", mimeType);
                content.Headers.TryAddWithoutValidation("Content-Length", fileSize.ToString());

                var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
                req.Headers.TryAddWithoutValidation("Authorization", auth);
                req.Headers.TryAddWithoutValidation("x-cos-security-token", token);
                req.Headers.TryAddWithoutValidation("Host", uploadAddr);

                var resp = await http.SendAsync(req, ct);

                // Extract video_id / preview_url from response headers
                var videoId = GetResponseHeader(resp, "X-Ros-Video-Id");
                var previewUrl = GetResponseHeader(resp, "X-Ros-Preview-Url");
                var staticUrl = GetResponseHeader(resp, "X-Ros-Static-Url");

                if (resp.IsSuccessStatusCode)
                    return new CosUploadResult(videoId, previewUrl, staticUrl);

                LogWarn("XHS COS direct upload attempt {Attempt}/{Max}: HTTP {Code}",
                    attempt + 1, MaxRetries, (int)resp.StatusCode);

                if ((int)resp.StatusCode is >= 400 and < 500 && (int)resp.StatusCode is not (403 or 408 or 429))
                    throw new Exception($"COS upload failed: HTTP {(int)resp.StatusCode}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户暂停/撤回：取消不重试
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                if (attempt >= MaxRetries - 1) throw;
                LogWarn(ex, "XHS COS direct upload attempt {Attempt} error", attempt + 1);
            }

            if (attempt < MaxRetries - 1)
                await Task.Delay((int)Math.Pow(2, attempt) * 1000, ct);
        }

        throw new Exception($"COS direct upload failed after {MaxRetries} attempts");
    }

    // ═══ COS Multipart Upload (>5MB) ═══

    private async Task<CosUploadResult> CosMultipartUploadAsync(
        string filePath, long fileSize, string fileKey, string uploadAddr, string token,
        Func<int, int, long, long, Task>? onProgress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        var (accessKey, deadline) = ParseRosToken(token);
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var auth = ComputeRosSignature(accessKey, fileKey, fileSize, uploadAddr, startTime, deadline);

        var baseUrl = $"https://{uploadAddr}/spectrum/{fileKey}";
        var baseHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = auth,
            ["x-cos-security-token"] = token,
            ["Host"] = uploadAddr,
        };

        // Step 2a: Init multipart
        LogInfo("XHS COS: init multipart upload...");
        var uploadId = await CosInitMultipartAsync(http, baseUrl, baseHeaders, ct);
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            LogWarn("XHS COS: multipart not supported, falling back to direct upload");
            return await CosDirectUploadAsync(filePath, fileSize, fileKey, uploadAddr, token, ct);
        }
        LogInfo("XHS COS: UploadId={UploadId}", uploadId[..Math.Min(30, uploadId.Length)]);

        // Step 2b: Upload parts
        var totalChunks = (int)Math.Ceiling((double)fileSize / ChunkSize);
        var parts = new List<(int PartNumber, string ETag)>();
        var uploadedBytes = 0L;

        await using (var fs = File.OpenRead(filePath))
        {
            for (int i = 0; i < totalChunks; i++)
            {
                var chunkSize = (int)Math.Min(ChunkSize, fileSize - fs.Position);
                var chunkData = new byte[chunkSize];
                await fs.ReadExactlyAsync(chunkData, ct);
                var partNum = i + 1;

                var etag = await CosUploadPartAsync(http, baseUrl, baseHeaders, uploadId, partNum, chunkData, ct);
                parts.Add((partNum, etag));

                uploadedBytes += chunkSize;
                if (onProgress is not null)
                    await onProgress(partNum, totalChunks, uploadedBytes, fileSize);
            }
        }

        // Step 2c: Complete
        LogInfo("XHS COS: completing multipart ({Count} parts)...", parts.Count);
        return await CosCompleteMultipartAsync(http, baseUrl, baseHeaders, uploadId, parts, ct);
    }

    private static async Task<string?> CosInitMultipartAsync(
        HttpClient http, string baseUrl, Dictionary<string, string> headers, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}?uploads");
                foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

                var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;

                var body = await resp.Content.ReadAsStringAsync(ct);
                var match = System.Text.RegularExpressions.Regex.Match(body, @"<UploadId>(.+?)</UploadId>");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户暂停/撤回：取消不吞
            }
            catch
            {
                if (attempt < 2) await Task.Delay(2000, ct);
            }
        }
        return null;
    }

    private async Task<string> CosUploadPartAsync(
        HttpClient http, string baseUrl, Dictionary<string, string> headers,
        string uploadId, int partNum, byte[] data, CancellationToken ct)
    {
        var url = $"{baseUrl}?partNumber={partNum}&uploadId={uploadId}";
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var content = new ByteArrayContent(data);
                content.Headers.TryAddWithoutValidation("Content-Length", data.Length.ToString());

                var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
                foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

                var resp = await http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();

                return GetResponseHeader(resp, "ETag")?.Trim('"') ?? "";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 取消别包成普通 Exception（否则队列会当失败而不是「用户停止」）
            }
            catch (Exception ex)
            {
                if (attempt >= MaxRetries - 1)
                    throw new Exception($"COS part {partNum} failed: {ex.Message}", ex);
                await Task.Delay((int)Math.Pow(2, attempt) * 1000, ct);
            }
        }
        throw new Exception($"COS part {partNum} failed after {MaxRetries} attempts");
    }

    private async Task<CosUploadResult> CosCompleteMultipartAsync(
        HttpClient http, string baseUrl, Dictionary<string, string> headers,
        string uploadId, List<(int PartNumber, string ETag)> parts, CancellationToken ct)
    {
        var xml = "<CompleteMultipartUpload>";
        foreach (var (num, etag) in parts)
            xml += $"<Part><PartNumber>{num}</PartNumber><ETag>{etag}</ETag></Part>";
        xml += "</CompleteMultipartUpload>";

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var content = new StringContent(xml, Encoding.UTF8, "application/xml");
                var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}?uploadId={uploadId}") { Content = content };
                foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

                var resp = await http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    return new CosUploadResult(
                        GetResponseHeader(resp, "X-Ros-Video-Id"),
                        GetResponseHeader(resp, "X-Ros-Preview-Url"),
                        GetResponseHeader(resp, "X-Ros-Static-Url"));
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                LogWarn("XHS COS complete attempt {Attempt}: HTTP {Code} — {Body}",
                    attempt + 1, (int)resp.StatusCode, body[..Math.Min(200, body.Length)]);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户暂停/撤回：取消不吞
            }
            catch (Exception ex)
            {
                if (attempt >= 2) throw;
                LogWarn(ex, "XHS COS complete error");
            }
            await Task.Delay(2000, ct);
        }
        throw new Exception("COS multipart complete failed");
    }

    // ═══ Transcode Poll ═══

    private async Task<bool> PollTranscodeAsync(XhsApiClient client, string videoId, CancellationToken ct)
    {
        for (int i = 0; i < TranscodePollMax; i++)
        {
            try
            {
                var resp = await client.GetEdithAsync<XhsTranscodeResponse>(
                    "/web_api/sns/capa/postgw/query_transcode",
                    new Dictionary<string, string>
                    {
                        ["video_id"] = videoId,
                        ["need_transcode"] = "false",
                        ["resource_type"] = "0",
                    }, ct);

                if (resp?.Status == 1)
                {
                    LogInfo("XHS transcode complete after {Attempts} polls", i + 1);
                    return true;
                }
                if (resp?.Status == -1)
                {
                    LogWarn("XHS transcode FAILED after {Attempts} polls", i + 1);
                    return false;
                }
                LogDebug("XHS transcode poll {I}/{Max}: status={Status}, progress={Progress}%",
                    i + 1, TranscodePollMax, resp?.Status, resp?.Progress);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户暂停/撤回：取消不吞
            }
            catch (Exception ex)
            {
                LogWarn(ex, "XHS transcode poll {I} error", i + 1);
            }
            await Task.Delay(TranscodePollIntervalMs, ct);
        }
        return false;
    }

    // ═══ ROS Signature (HMAC-SHA1, from xhs_creator_signature.js) ═══

    internal static (string accessKey, int deadline) ParseRosToken(string token)
    {
        var parts = token.Split(':', 2);
        var accessKey = parts[0];
        var deadline = (int)DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();

        if (parts.Length > 1)
        {
            try
            {
                var payload = parts[1] + new string('=', (4 - parts[1].Length % 4) % 4);
                var bytes = Convert.FromBase64String(payload);
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.TryGetProperty("deadline", out var dl))
                    deadline = dl.GetInt32();
            }
            catch { /* use default */ }
        }
        return (accessKey, deadline);
    }

    internal static string ComputeRosSignature(
        string accessKey, string fileKey, long fileSize, string host,
        long startTime, int deadline)
    {
        var message = $"{startTime};{deadline}";

        // Step 1: derive signing key = HMAC-SHA1(message, "null")
        var keyBytes = HMACSHA1.HashData(
            Encoding.UTF8.GetBytes("null"),
            Encoding.UTF8.GetBytes(message));
        var key = Convert.ToHexStringLower(keyBytes);

        // Step 2: build HttpString = SHA1("put\n/path\n\nheaders\n")
        var httpString = $"put\n/spectrum/{fileKey}\n\ncontent-length={fileSize}&host={host}\n";
        var sha1Http = SHA1.HashData(Encoding.UTF8.GetBytes(httpString));
        var sha1HttpHex = Convert.ToHexStringLower(sha1Http);

        // Step 3: StringToSign = "sha1\n{message}\n{sha1}\n"
        var toSign = $"sha1\n{message}\n{sha1HttpHex}\n";

        // Step 4: signature = HMAC-SHA1(toSign, key)
        var sigBytes = HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(key),
            Encoding.UTF8.GetBytes(toSign));
        var signature = Convert.ToHexStringLower(sigBytes);

        return $"q-sign-algorithm=sha1&q-ak={accessKey}&q-sign-time={message}&q-key-time={message}&q-header-list=content-length;content-type;host&q-url-param-list=&q-signature={signature}";
    }

    // ═══ Helpers ═══

    private static string? GetResponseHeader(HttpResponseMessage resp, string name)
    {
        if (resp.Headers.TryGetValues(name, out var vals)) return vals.FirstOrDefault();
        if (resp.Content.Headers.TryGetValues(name, out var cVals)) return cVals.FirstOrDefault();
        // Try case-insensitive via non-validated headers
        foreach (var h in resp.Headers)
            if (string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
                return h.Value.FirstOrDefault();
        foreach (var h in resp.Content.Headers)
            if (string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
                return h.Value.FirstOrDefault();
        return null;
    }

    private static string GuessMimeType(string ext) => ext.ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".mkv" => "video/x-matroska",
        ".flv" => "video/x-flv",
        ".wmv" => "video/x-ms-wmv",
        ".webm" => "video/webm",
        ".m4v" => "video/x-m4v",
        // 封面图（2.5.3）：抓包 PUT 的 Content-Type 是 image/png
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".heic" => "image/heic",
        _ => "video/mp4",
    };

    private sealed record CosUploadResult(
        string? VideoId, string? PreviewUrl, string? StaticUrl);

    private static string? TryGetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

}

/// <summary>小红书上传统计。</summary>
public sealed record XhsUploadResult(
    string FileId,      // COS 文件路径 ("spectrum/abc123")
    string VideoId,     // 小红书视频 ID（可能为空，由 publish 侧补查）
    string PreviewUrl,  // CDN 预览地址
    string FileKey      // COS key（不含 spectrum/ 前缀，用于 videoId 查询）
);
