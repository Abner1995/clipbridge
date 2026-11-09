namespace CreatFlow.Platforms.Core.Vod;

using Serilog;

/// <summary>
/// ByteDance VOD 通用视频上传引擎（阶段 5.1）—— 抖音与头条共用同一条 5 阶段管线：
///
///   1. ApplyUploadInner  (AWS SigV4) — 获取 TOS 上传凭证
///   2. InitUpload        (TOS Auth)  — 初始化分片上传会话
///   3. UploadChunks      (TOS)       — 逐片上传（默认 3 MB）
///   4. MergeFile         (TOS)       — 合并分片
///   5. CommitUploadInner (AWS SigV4) — 提交，返回 Vid
///
/// 平台差异全部收在 <see cref="BdvodUploadSettings"/>：
///   · 抖音：SpaceName = aweme，Query 不带 app_id / user_id / EnOID；
///   · 头条：SpaceName = middlevideo_toutiao（横版）/ short_video_toutiao（竖版），
///           Query 带 app_id=1231、user_id、EnOID=1（接口说明书 §4.2.2）。
///
/// 依赖 CRT（Common Runtime）签名协议 — AwsSigV4Handler 处理 STSv2 签名。
/// </summary>
public sealed class BdvodUploadService
{
    private readonly PlatformOptions _options;
    private readonly BdvodUploadSettings _settings;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    /// <summary>
    /// VOD 区域常量（抖音/头条同属 ByteDance VOD）
    /// </summary>
    private const string VodRegion = "cn-north-1";
    private const string VodService = "vod";

    public BdvodUploadService(
        PlatformOptions options,
        BdvodUploadSettings settings,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _options = options;
        _settings = settings;
        _logger = logger;
        ChunkSize = settings.ChunkSizeBytes;
    }

    /// <summary>分片大小（默认由 settings 给，抖音 3 MB 与 upload.js 一致）</summary>
    public int ChunkSize { get; set; }

    // ═══ Logging helpers (fallback to Serilog when ILogger is null) ═══

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
    /// 完整上传一个视频到 ByteDance VOD。
    ///
    /// 参数:
    ///   filePath      — 本地视频文件路径
    ///   stsAccessKey  — STS AccessKeyId
    ///   stsSecretKey  — STS SecretAccessKey
    ///   stsSessionToken — STS SessionToken
    ///   userId        — 用户 ID（X-Storage-U / 头条 user_id query 用）
    ///   extension     — 视频扩展名（mp4 / mov 等）
    ///   onChunkProgress — 分片上传进度回调 (currentChunk, totalChunks, uploadedBytes, totalBytes)
    ///
    /// 返回 Vid + 视频元数据，失败返回 null（取消照常上抛）。
    /// </summary>
    public async Task<BdvodUploadResult?> UploadAsync(
        string filePath,
        string stsAccessKey,
        string stsSecretKey,
        string stsSessionToken,
        string userId,
        string? extension = null,
        Func<int, int, long, long, Task>? onChunkProgress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            LogErr("Video file not found: {Path}", filePath);
            return null;
        }

        var fileSize = new FileInfo(filePath).Length;
        var ext = extension ?? Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

        LogInfo("Starting VOD upload: {File} ({Size} bytes, ext={Ext}, space={Space})",
            Path.GetFileName(filePath), fileSize, ext, _settings.SpaceName);

        // Step 1: ApplyUploadInner
        var creds = await ApplyUploadAsync(fileSize, ext, userId,
            stsAccessKey, stsSecretKey, stsSessionToken, ct);
        if (creds is null)
        {
            LogErr("ApplyUploadInner failed");
            return null;
        }

        // Step 2: InitUpload
        var uploadId = await InitTosUploadAsync(creds, userId, ct);
        if (uploadId is null)
        {
            LogErr("InitUpload failed");
            return null;
        }

        try
        {
            // Step 3: Upload chunks
            var crc32List = await UploadChunksAsync(
                filePath, creds, uploadId, userId, onChunkProgress, ct);
            if (crc32List is null)
            {
                LogErr("UploadChunks failed");
                return null;
            }

            // Step 4: MergeFile
            var merged = await MergeFileAsync(creds, uploadId, crc32List, userId, ct);
            if (!merged)
            {
                LogErr("MergeFile failed");
                return null;
            }

            // Step 5: CommitUploadInner
            var result = await CommitUploadAsync(creds, userId,
                stsAccessKey, stsSecretKey, stsSessionToken, ct);
            if (result is null)
            {
                LogErr("CommitUploadInner failed");
                return null;
            }

            LogInfo("Upload complete: Vid={Vid}", result.VideoId);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户暂停/应用退出 — 取消语义继续上抛（ExecuteTaskAsync 的暂停路径会静默吞掉，
            // 不会误报服务端失败，也不会把本地状态打成 Failed）
            throw;
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            // 取消时底层可能抛 HttpRequestException（连接被掐断）而不是 OCE —— 统一改写成 OCE
            throw new OperationCanceledException("VOD 上传已取消", ex, ct);
        }
        catch (Exception ex)
        {
            LogErr(ex, "Upload pipeline failed");
            return null;
        }
    }

    // ═══ 纯函数：VOD Query 组装（自检直接验 wire 形体） ═══

    /// <summary>
    /// ApplyUploadInner URL。平台差异：
    /// 抖音不带 app_id/user_id/EnOID；头条全带（接口说明书 §4.2.2）。
    /// 参数按 key 排序（与既有抖音实现一致，服务端不关心顺序）。
    /// </summary>
    public static string BuildApplyUrl(
        BdvodUploadSettings settings, string userId,
        long fileSize, string? fileExtension, string randomS)
    {
        var qs = new Dictionary<string, string>
        {
            ["Action"] = "ApplyUploadInner",
            ["Version"] = "2020-11-19",
            ["SpaceName"] = settings.SpaceName,
            ["FileType"] = "video",
            ["IsInner"] = "1",
            ["FileSize"] = fileSize.ToString(),
            ["s"] = randomS,
        };
        if (!string.IsNullOrWhiteSpace(fileExtension))
            qs["FileExtension"] = fileExtension;
        if (!string.IsNullOrWhiteSpace(settings.AppId))
            qs["app_id"] = settings.AppId;
        if (settings.SendUserIdQuery)
            qs["user_id"] = userId;
        if (settings.SendEnOid)
            qs["EnOID"] = "1";

        return $"{settings.VodHost.TrimEnd('/')}/?{JoinQuery(qs)}";
    }

    /// <summary>CommitUploadInner URL（Body 是 <c>{SessionKey}</c>）。</summary>
    public static string BuildCommitUrl(BdvodUploadSettings settings, string userId)
    {
        var qs = new Dictionary<string, string>
        {
            ["Action"] = "CommitUploadInner",
            ["Version"] = "2020-11-19",
            ["SpaceName"] = settings.SpaceName,
        };
        if (!string.IsNullOrWhiteSpace(settings.AppId))
            qs["app_id"] = settings.AppId;
        if (settings.SendUserIdQuery)
            qs["user_id"] = userId;

        return $"{settings.VodHost.TrimEnd('/')}/?{JoinQuery(qs)}";
    }

    private static string JoinQuery(Dictionary<string, string> qs) =>
        string.Join("&", qs.OrderBy(kv => kv.Key)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    // ═══ Step 1: ApplyUploadInner ═══

    private async Task<BdvodUploadCreds?> ApplyUploadAsync(
        long fileSize,
        string fileExtension,
        string userId,
        string stsAccessKey,
        string stsSecretKey,
        string stsSessionToken,
        CancellationToken ct)
    {
        // Handler 管道: StsAuth → AwsSigV4 → HTTP
        var stsHandler = new Core.Handlers.StsAuthHandler(stsAccessKey, stsSecretKey, stsSessionToken);
        var sigHandler = new AwsSigV4Handler(stsAccessKey, stsSecretKey, VodRegion, VodService);
        stsHandler.InnerHandler = sigHandler;
        sigHandler.InnerHandler = new HttpClientHandler();

        using var client = new PlatformHttpClient(stsHandler, _options, null);

        var url = BuildApplyUrl(_settings, userId, fileSize, fileExtension, GenerateRandomS());

        LogDebug("ApplyUploadInner → {Url}", url);

        try
        {
            var json = await client.GetStringAsync(url, ct);
            var resp = JsonSerializer.Deserialize<BdvodApplyUploadResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var err = resp?.ResponseMetadata?.Error;
            if (err is not null)
            {
                LogWarn("ApplyUploadInner error: {Code} - {Msg}", err.Code, err.Message);
                return null;
            }

            var nodes = resp?.Result?.InnerUploadAddress?.UploadNodes;
            var node = nodes?.FirstOrDefault();
            var storeInfo = node?.StoreInfos?.FirstOrDefault();

            if (node is null || storeInfo is null || string.IsNullOrWhiteSpace(node.UploadHost))
            {
                LogWarn("ApplyUploadInner: no valid UploadNode");
                return null;
            }

            return new BdvodUploadCreds(
                Auth: storeInfo.Auth ?? "",
                StoreUri: storeInfo.StoreUri ?? "",
                UploadHost: node.UploadHost,
                SessionKey: node.SessionKey ?? "",
                UploadId: storeInfo.UploadId ?? "",
                UploadHeader: node.UploadHeader ?? new Dictionary<string, string>());
        }
        catch (HttpRequestException ex)
        {
            LogWarn(ex, "ApplyUploadInner HTTP error");
            return null;
        }
    }

    // ═══ Step 2: InitUpload (TOS) ═══

    private async Task<string?> InitTosUploadAsync(
        BdvodUploadCreds creds,
        string userId,
        CancellationToken ct)
    {
        var url = $"https://{creds.UploadHost}/upload/v1/{creds.StoreUri}?uploadmode=part&phase=init";

        using var http = CreateTosClient();
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Authorization", creds.Auth);
        req.Headers.TryAddWithoutValidation("X-Storage-U", userId);
        req.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data");
        foreach (var (k, v) in creds.UploadHeader)
            req.Headers.TryAddWithoutValidation(k, v);

        LogDebug("InitUpload → {Url}", url);

        var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            LogWarn("InitUpload HTTP {Code}: {Body}", (int)resp.StatusCode, Truncate(body));
            return null;
        }

        var data = JsonSerializer.Deserialize<TosInitUploadResponse>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (data?.Code != 2000 || string.IsNullOrWhiteSpace(data?.Data?.UploadId))
        {
            LogWarn("InitUpload failed: code={Code}, msg={Msg}, body={Body}",
                data?.Code, data?.Message, Truncate(body));
            return null;
        }

        LogDebug("InitUpload OK: uploadId={Id}", data.Data.UploadId);
        return data.Data.UploadId;
    }

    // ═══ Step 3: UploadChunks (TOS) ═══

    private async Task<List<BdvodChunkCrc>?> UploadChunksAsync(
        string filePath,
        BdvodUploadCreds creds,
        string uploadId,
        string userId,
        Func<int, int, long, long, Task>? onChunkProgress,
        CancellationToken ct)
    {
        var fileSize = new FileInfo(filePath).Length;
        var totalChunks = (int)Math.Ceiling((double)fileSize / ChunkSize);
        var results = new List<BdvodChunkCrc>(totalChunks);

        LogInfo("Uploading {Chunks} chunks ({Size} bytes total, chunk={ChunkSize})",
            totalChunks, fileSize, ChunkSize);

        using var http = CreateTosClient();
        var buffer = new byte[ChunkSize];

        await using var fs = File.OpenRead(filePath);
        long uploadedBytes = 0;

        for (int i = 0; i < totalChunks; i++)
        {
            var partNumber = i + 1;
            int read = await fs.ReadAsync(buffer, 0, ChunkSize, ct);
            if (read == 0) break;

            var chunkData = read == ChunkSize ? buffer : buffer[..read];
            var crc32 = Crc32CHex(chunkData);

            var url = $"https://{creds.UploadHost}/upload/v1/{creds.StoreUri}"
                + $"?uploadid={uploadId}&phase=transfer"
                + $"&part_number={partNumber}&part_offset={i * ChunkSize}";

            // 带重试的上传
            var uploaded = false;
            string? lastErrorBody = null;
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    var content = new ByteArrayContent(chunkData);
                    content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    content.Headers.TryAddWithoutValidation("Content-CRC32", crc32);
                    content.Headers.TryAddWithoutValidation("X-Storage-U", userId);

                    var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                    req.Headers.TryAddWithoutValidation("Authorization", creds.Auth);
                    foreach (var (k, v) in creds.UploadHeader)
                        req.Headers.TryAddWithoutValidation(k, v);

                    var resp = await http.SendAsync(req, ct);
                    var respBody = await resp.Content.ReadAsStringAsync(ct);

                    // TOS returns "message":"Success" (capital S) → use case-insensitive match
                    if (resp.IsSuccessStatusCode
                        && respBody.Contains("\"success\"", StringComparison.OrdinalIgnoreCase))
                    {
                        uploaded = true;
                        break;
                    }

                    if ((int)resp.StatusCode is >= 400 and < 500)
                    {
                        // 4xx 不重试
                        lastErrorBody = respBody;
                        break;
                    }

                    lastErrorBody = respBody;
                    LogWarn("Chunk {Part} retry {Retry}/3: HTTP {Code}",
                        partNumber, retry + 1, (int)resp.StatusCode);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    // 取消时不重试、不吞 —— 否则最后一次重试会落成「上传失败」
                    throw new OperationCanceledException(ct);
                }
                catch (Exception ex)
                {
                    LogWarn("Chunk {Part} retry {Retry}/3: {Err}",
                        partNumber, retry + 1, ex.Message);
                }

                if (retry < 2) await Task.Delay(1000 * (retry + 1), ct);
            }

            if (!uploaded)
            {
                LogErr("Chunk {Part} failed after retries: {Body}",
                    partNumber, Truncate(lastErrorBody));
                return null;
            }

            results.Add(new BdvodChunkCrc(partNumber, crc32));
            uploadedBytes += read;

            LogDebug("Chunk {Part}/{Total} done ({Pct:F1}%)",
                partNumber, totalChunks, uploadedBytes * 100.0 / fileSize);

            if (onChunkProgress is not null)
                await onChunkProgress(partNumber, totalChunks, uploadedBytes, fileSize);
        }

        LogInfo("All {Count} chunks uploaded", results.Count);
        return results;
    }

    // ═══ Step 4: MergeFile (TOS) ═══

    private async Task<bool> MergeFileAsync(
        BdvodUploadCreds creds,
        string uploadId,
        List<BdvodChunkCrc> crc32List,
        string userId,
        CancellationToken ct)
    {
        var url = $"https://{creds.UploadHost}/upload/v1/{creds.StoreUri}"
            + $"?uploadmode=part&phase=finish&uploadid={uploadId}";

        var crcBody = string.Join(",",
            crc32List.Select(c => $"{c.PartNumber}:{c.Crc32}"));

        using var http = CreateTosClient();
        var content = new StringContent(crcBody, Encoding.UTF8, "text/plain");

        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.TryAddWithoutValidation("Authorization", creds.Auth);
        req.Headers.TryAddWithoutValidation("X-Storage-U", userId);
        foreach (var (k, v) in creds.UploadHeader)
            req.Headers.TryAddWithoutValidation(k, v);

        LogDebug("MergeFile → {Url} ({Count} parts)", url, crc32List.Count);

        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            LogWarn("MergeFile HTTP {Code}: {Body}", (int)resp.StatusCode, Truncate(body));
            return false;
        }

        LogInfo("MergeFile OK");
        return true;
    }

    // ═══ Step 5: CommitUploadInner ═══

    private async Task<BdvodUploadResult?> CommitUploadAsync(
        BdvodUploadCreds creds,
        string userId,
        string stsAccessKey,
        string stsSecretKey,
        string stsSessionToken,
        CancellationToken ct)
    {
        var stsHandler = new Core.Handlers.StsAuthHandler(stsAccessKey, stsSecretKey, stsSessionToken);
        var sigHandler = new AwsSigV4Handler(stsAccessKey, stsSecretKey, VodRegion, VodService);
        stsHandler.InnerHandler = sigHandler;
        sigHandler.InnerHandler = new HttpClientHandler();

        using var client = new PlatformHttpClient(stsHandler, _options, null);

        var url = BuildCommitUrl(_settings, userId);

        var body = JsonSerializer.Serialize(new
        {
            SessionKey = creds.SessionKey,
            Functions = Array.Empty<object>(),
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        LogDebug("CommitUploadInner → sessionKey={Key}",
            creds.SessionKey?[..Math.Min(20, creds.SessionKey?.Length ?? 0)]);

        try
        {
            var json = await client.PostJsonRawAsync(url, body, ct);
            var resp = JsonSerializer.Deserialize<BdvodCommitUploadResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var err = resp?.ResponseMetadata?.Error;
            if (err is not null)
            {
                LogWarn("CommitUploadInner error: {Code} - {Msg}", err.Code, err.Message);
                return null;
            }

            var first = resp?.Result?.Results?.FirstOrDefault();
            if (first?.Vid is null)
            {
                LogWarn("CommitUploadInner: no Vid in response");
                return null;
            }

            return new BdvodUploadResult(
                VideoId: first.Vid,
                Width: first.VideoMeta?.Width ?? 0,
                Height: first.VideoMeta?.Height ?? 0,
                Duration: first.VideoMeta?.Duration ?? 0,
                Codec: first.VideoMeta?.Codec,
                Uri: first.VideoMeta?.Uri);
        }
        catch (HttpRequestException ex)
        {
            LogWarn(ex, "CommitUploadInner HTTP error");
            return null;
        }
    }

    // ═══ Private Helpers ═══

    private static HttpClient CreateTosClient()
    {
        return new HttpClient(new HttpClientHandler())
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
    }

    private static string GenerateRandomS()
    {
        var sb = new StringBuilder(10);
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        for (int i = 0; i < 10; i++)
            sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }

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

    private static string Truncate(string? s, int max = 300)
        => s is null ? "(null)" : s.Length <= max ? s : s[..max] + "...";
}

// ═══ Models ═══

/// <summary>
/// VOD 引擎的平台参数（阶段 5.1）—— 抖音/头条的差异全在这里。
/// </summary>
public sealed record BdvodUploadSettings(
    string VodHost,
    string SpaceName,
    string? AppId = null,
    bool SendUserIdQuery = false,
    bool SendEnOid = false,
    int ChunkSizeBytes = 3 * 1024 * 1024);

/// <summary>ApplyUploadInner 返回的 TOS 上传凭证。</summary>
internal sealed record BdvodUploadCreds(
    string Auth,
    string StoreUri,
    string UploadHost,
    string SessionKey,
    string UploadId,
    Dictionary<string, string> UploadHeader
);

/// <summary>分片 CRC32 信息 — 用于 MergeFile。</summary>
internal sealed record BdvodChunkCrc(
    int PartNumber,
    string Crc32
);

/// <summary>上传完成结果 — CommitUploadInner 返回的视频信息。</summary>
public sealed record BdvodUploadResult(
    string VideoId,
    int Width,
    int Height,
    double Duration,
    string? Codec,
    string? Uri
);

// ═══ VOD 响应 DTO（抖音/头条共用；字段名 = 抓包原文） ═══

/// <summary>ApplyUploadInner API 响应。</summary>
public sealed class BdvodApplyUploadResponse
{
    [JsonPropertyName("ResponseMetadata")]
    public BdvodVodMetadata? ResponseMetadata { get; set; }

    [JsonPropertyName("Result")]
    public BdvodApplyUploadResult? Result { get; set; }
}

/// <summary>CommitUploadInner API 响应。</summary>
public sealed class BdvodCommitUploadResponse
{
    [JsonPropertyName("ResponseMetadata")]
    public BdvodVodMetadata? ResponseMetadata { get; set; }

    [JsonPropertyName("Result")]
    public BdvodCommitUploadResult? Result { get; set; }
}

/// <summary>VOD API 通用响应元数据（错误时填充 Error 字段）。</summary>
public sealed class BdvodVodMetadata
{
    [JsonPropertyName("Error")]
    public BdvodVodError? Error { get; set; }
}

/// <summary>VOD API 错误信息。</summary>
public sealed class BdvodVodError
{
    [JsonPropertyName("Code")]
    public string? Code { get; set; }

    [JsonPropertyName("Message")]
    public string? Message { get; set; }
}

/// <summary>ApplyUploadInner Result。</summary>
public sealed class BdvodApplyUploadResult
{
    [JsonPropertyName("InnerUploadAddress")]
    public BdvodInnerUploadAddress? InnerUploadAddress { get; set; }
}

/// <summary>内部上传地址 — 包含 TOS 上传节点信息。</summary>
public sealed class BdvodInnerUploadAddress
{
    [JsonPropertyName("UploadNodes")]
    public List<BdvodUploadNode>? UploadNodes { get; set; }
}

/// <summary>上传节点 — 包含域名、SessionKey、StoreInfos。</summary>
public sealed class BdvodUploadNode
{
    [JsonPropertyName("UploadHost")]
    public string? UploadHost { get; set; }

    [JsonPropertyName("SessionKey")]
    public string? SessionKey { get; set; }

    [JsonPropertyName("StoreInfos")]
    public List<BdvodVodStoreInfo>? StoreInfos { get; set; }

    [JsonPropertyName("UploadHeader")]
    public Dictionary<string, string>? UploadHeader { get; set; }
}

/// <summary>VOD StoreInfo — 包含 TOS OID / Auth / UploadID。</summary>
public sealed class BdvodVodStoreInfo
{
    [JsonPropertyName("StoreUri")]
    public string? StoreUri { get; set; }

    [JsonPropertyName("Auth")]
    public string? Auth { get; set; }

    [JsonPropertyName("UploadID")]
    public string? UploadId { get; set; }
}

/// <summary>CommitUploadInner Result。</summary>
public sealed class BdvodCommitUploadResult
{
    [JsonPropertyName("Results")]
    public List<BdvodUploadResultItem>? Results { get; set; }
}

/// <summary>上传结果单项 — 包含 Vid 和 VideoMeta。</summary>
public sealed class BdvodUploadResultItem
{
    [JsonPropertyName("Vid")]
    public string? Vid { get; set; }

    [JsonPropertyName("VideoMeta")]
    public BdvodVideoMeta? VideoMeta { get; set; }
}

/// <summary>视频元数据（CommitUploadInner 返回）。</summary>
public sealed class BdvodVideoMeta
{
    [JsonPropertyName("Width")]
    public int Width { get; set; }

    [JsonPropertyName("Height")]
    public int Height { get; set; }

    [JsonPropertyName("Duration")]
    public double Duration { get; set; }

    [JsonPropertyName("Codec")]
    public string? Codec { get; set; }

    [JsonPropertyName("Uri")]
    public string? Uri { get; set; }
}

// ═══ TOS Upload Responses ═══

/// <summary>TOS InitUpload 响应 — phase=init。</summary>
public sealed class TosInitUploadResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public TosInitData? Data { get; set; }
}

/// <summary>TOS InitUpload data。</summary>
public sealed class TosInitData
{
    [JsonPropertyName("uploadid")]
    public string? UploadId { get; set; }
}
