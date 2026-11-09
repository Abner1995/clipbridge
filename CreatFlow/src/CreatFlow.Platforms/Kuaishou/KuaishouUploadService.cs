using System.Collections.Concurrent;
using CreatFlow.Platforms.Kuaishou.Requests;
using Serilog;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手上传服务 — CDN 分片上传管线。
///
/// 对标 Python fangai/kuaishou/upload.py 的 KuaishouUploader。
///
/// 流程：
///   1. preInfo        → token + CDN endpoints + fileId
///   2. CDN resume     → 检查已上传的分片
///   3. CDN fragment   → 并发上传分片（5MB/片，3 并发，endpoint 故障转移）
///   4. CDN complete   → 通知 CDN 合并分片
///   5. notifyFinish   → 通知 cp.kuaishou.com，获取视频元数据
/// </summary>
public sealed class KuaishouUploadService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<KuaishouUploadService>? _logger;

    public int ChunkSize { get; set; } = 5 * 1024 * 1024;  // 5 MB
    public int Concurrency { get; set; } = 3;
    public int MaxRetries { get; set; } = 3;
    private static readonly HashSet<int> FailoverStatusCodes = [403, 408, 409, 421, 425, 429, 499];

    public KuaishouUploadService(PlatformOptions options, ILogger<KuaishouUploadService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 完整上传一个视频文件到快手。
    /// </summary>
    /// <param name="cookie">Cookie 字符串</param>
    /// <param name="filePath">本地视频文件路径</param>
    /// <param name="onProgress">进度回调 (current, total, bytes, totalBytes)</param>
    /// <returns>上传结果（fileId, mediaId, coverKey 等），失败返回 null</returns>
    public async Task<KuaishouUploadResult?> UploadAsync(
        string cookie, string filePath,
        Func<int, int, long, long, Task>? onProgress = null,
        string? apiPh = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);

        var fileSize = new FileInfo(filePath).Length;
        var fileName = Path.GetFileName(filePath);
        // 发给平台的 fileName 必须是 ASCII —— 中文名会让 upload/finish 回 500002（见 WireFileName）
        var wireFileName = WireFileName(fileName);
        var mimeType = GuessMimeType(fileName);
        var totalChunks = (int)Math.Ceiling((double)fileSize / ChunkSize);

        using var apiClient = new KuaishouApiClient(cookie, _options, apiPh,
            logger: _logger as ILogger<PlatformHttpClient>);

        // Step 1: preInfo
        Log.Debug("[KuaishouUpload Step 1/5] preInfo...");
        var preData = await apiClient.PostAsync<KuaishouPreInfoResponse>(
            "/rest/cp/works/v2/video/pc/upload/pre", new { uploadType = 1 }, ct);
        if (preData?.Token is null || preData.EndPoints is not { Count: > 0 })
        {
            Log.Error("[KuaishouUpload] preInfo failed: token or endpoints empty");
            return null;
        }
        var token = preData.Token;
        var fileId = preData.FileId;
        var endpoints = preData.EndPoints!;
        Log.Debug("[KuaishouUpload] preInfo ok: fileId={FileId}, endpoints={Count}, token={TokenStart}...",
            fileId, endpoints.Count, token[..Math.Min(30, token.Length)]);

        // Step 2-4: CDN upload (raw HTTP, not signed)
        _logger?.LogInformation("[KuaishouUpload Step 2-4] CDN upload {Chunks} chunks...", totalChunks);

        using var cdnClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        cdnClient.DefaultRequestHeaders.Add("User-Agent",
            _options.Environment?.UserAgent ?? _options.UserAgent);
        cdnClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        cdnClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");

        var workingEndpoint = endpoints[0];

        // Check resume
        var finishedChunks = await CheckResumeAsync(cdnClient, workingEndpoint, token, endpoints, ct);
        _logger?.LogInformation("Resume: {Finished}/{Total} chunks done", finishedChunks.Count, totalChunks);

        if (finishedChunks.Count < totalChunks)
        {
            // Upload remaining chunks
            await UploadChunksAsync(cdnClient, filePath, fileSize, totalChunks,
                finishedChunks, token, endpoints, workingEndpoint, onProgress, ct);
        }

        // Complete
        _logger?.LogInformation("[KuaishouUpload Step 4/5] Complete...");
        await CompleteAsync(cdnClient, workingEndpoint, token, totalChunks, endpoints, ct);

        // Step 5: notifyUploadFinish
        Log.Debug("[KuaishouUpload Step 5/5] notifyFinish...");
        await Task.Delay(3000, ct); // Wait for CDN sync

        var notifyBody = new { token, fileName = wireFileName, fileType = mimeType, fileLength = fileSize };
        Log.Debug("[KuaishouUpload] notifyFinish body: token={TokenStart}..., fileName={File}, wireName={Wire}, fileType={Type}, fileLength={Len}, apiPh={ApiPh}",
            token[..Math.Min(40, token.Length)], fileName, wireFileName, mimeType, fileSize, apiPh ?? "MISSING");
        // Log full body JSON for comparison with Python
        var fullBody = JsonSerializer.Serialize(notifyBody);
        Log.Debug("[KuaishouUpload] notifyFinish full body (before ApiPh): {Body}", fullBody);

        KuaishouApiException? last500002 = null;
        KuaishouFinishResponse? finishData = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                finishData = await apiClient.PostAsync<KuaishouFinishResponse>(
                    "/rest/cp/works/v2/video/pc/upload/finish", notifyBody, ct);
                Log.Debug("[KuaishouUpload] notifyFinish OK attempt {Attempt}: fileId={Fid}, mediaId={Mid}, coverKey={Ck}, {W}x{H}, dur={Dur}ms, photoIdStr={Pid}",
                    attempt + 1, finishData?.FileId, finishData?.MediaId, finishData?.CoverKey,
                    finishData?.Width, finishData?.Height, finishData?.VideoDuration, finishData?.PhotoIdStr);
                last500002 = null;
                break;
            }
            catch (KuaishouApiException ex) when (ex.ResultCode == 500002)
            {
                last500002 = ex;
                Log.Debug("[KuaishouUpload] notifyFinish 500002 attempt {Attempt}/4: {Msg}, response={Resp}",
                    attempt + 1, ex.Message, ex.ResponseBody?[..Math.Min(500, ex.ResponseBody.Length)] ?? "null");
                if (attempt < 3)
                {
                    var wait = 5 * (attempt + 1);
                    await Task.Delay(wait * 1000, ct);
                }
            }
        }
        if (finishData == null && last500002 is not null)
            throw last500002;

        Log.Debug("[KuaishouUpload] Upload complete: fileId={Fid}, mediaId={Mid}, coverKey={Ck}",
            finishData?.FileId, finishData?.MediaId, finishData?.CoverKey);
        return new KuaishouUploadResult(
            FileId: finishData?.FileId ?? fileId,
            MediaId: finishData?.MediaId ?? "",
            CoverKey: finishData?.CoverKey ?? "",
            VideoDuration: finishData?.VideoDuration ?? 0,
            Width: finishData?.Width ?? 0,
            Height: finishData?.Height ?? 0,
            PhotoIdStr: finishData?.PhotoIdStr ?? "");
    }

    /// <summary>自动提取封面帧。</summary>
    public async Task<List<string>?> ExtractCoverAsync(string cookie, long fileId, CancellationToken ct = default)
    {
        using var client = new KuaishouApiClient(cookie, _options,
            logger: _logger as ILogger<PlatformHttpClient>);
        var data = await client.PostAsync<KuaishouCoverResponse>(
            "/rest/cp/works/v4/video/pc/upload/cover/extract/query", new { fileId }, ct);
        return data?.List;
    }

    // ═══ CDN Upload ═══

    private async Task<HashSet<int>> CheckResumeAsync(
        HttpClient client, string endpoint, string token, List<string> endpoints, CancellationToken ct)
    {
        var finished = new HashSet<int>();
        var currentEp = endpoint;

        for (int retry = 0; retry < MaxRetries; retry++)
        {
            try
            {
                var url = $"https://{currentEp}/api/upload/resume?upload_token={Uri.EscapeDataString(token)}";
                var resp = await client.GetAsync(url, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                Log.Debug("[Kuaishou CDN] resume response: HTTP {Status}, body={Body}",
                    (int)resp.StatusCode, body[..Math.Min(300, body.Length)]);

                if (!resp.IsSuccessStatusCode)
                {
                    if (ShouldFailover((int)resp.StatusCode))
                    {
                        currentEp = Failover(currentEp, endpoints);
                        continue;
                    }
                    _logger?.LogWarning("Resume HTTP {Code}: {Body}", (int)resp.StatusCode, body[..Math.Min(200, body.Length)]);
                    return finished;
                }

                var data = JsonSerializer.Deserialize<JsonElement>(body);
                var inner = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var d) ? d : data;
                if (inner.TryGetProperty("existed", out var existed) && existed.GetBoolean()) return finished;

                if (inner.TryGetProperty("fragment_list", out var fl))
                {
                    foreach (var item in fl.EnumerateArray())
                    {
                        var fragObj = item;
                        if (fragObj.ValueKind == JsonValueKind.Object && fragObj.TryGetProperty("id", out var fid))
                            finished.Add(fid.GetInt32());
                        else if (fragObj.ValueKind == JsonValueKind.Number)
                            finished.Add(fragObj.GetInt32());
                    }
                }
                return finished;
            }
            // 用户暂停/撤回时 ct 已取消：**必须原样抛 OCE**。以前这里把它当普通失败吞掉，
            // 后面的分片上传又在所有 endpoint 上都撞到取消 → 最终抛出
            // "Chunk X failed on all endpoints" 这种普通 Exception，队列把它当失败
            // 而不是「用户主动停止」（2026-10-22 上传队列「删除选中」实测）。
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (retry < MaxRetries - 1 && ShouldFailover(0))
                {
                    currentEp = Failover(currentEp, endpoints);
                    await Task.Delay(1000 * (retry + 1), ct);
                    continue;
                }
                _logger?.LogWarning(ex, "Resume check failed");
                return finished;
            }
        }
        return finished;
    }

    private async Task UploadChunksAsync(
        HttpClient client, string filePath, long fileSize, int totalChunks,
        HashSet<int> finished, string token, List<string> endpoints,
        string workingEndpoint,
        Func<int, int, long, long, Task>? onProgress,
        CancellationToken ct)
    {
        var pending = Enumerable.Range(0, totalChunks).Where(i => !finished.Contains(i)).ToList();
        var uploadedBytes = finished.Sum(i => (long)Math.Min(ChunkSize, fileSize - i * (long)ChunkSize));
        var currentChunk = finished.Count;

        // Process in batches of 32 (matches SDK)
        const int batchSize = 32;
        for (int batchStart = 0; batchStart < pending.Count; batchStart += batchSize)
        {
            var batch = pending.Skip(batchStart).Take(batchSize).ToList();
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Concurrency, batch.Count), CancellationToken = ct };
            var ep = new ConcurrentBag<string>();

            await Parallel.ForEachAsync(batch, options, async (idx, innerCt) =>
            {
                var start = idx * (long)ChunkSize;
                var end = Math.Min(start + ChunkSize, fileSize);
                var data = new byte[end - start];
                await using (var fs = File.OpenRead(filePath))
                {
                    fs.Seek(start, SeekOrigin.Begin);
                    await fs.ReadExactlyAsync(data, innerCt);
                }

                var newEp = await UploadChunkWithFailoverAsync(
                    client, token, data, idx, start, end - 1, fileSize,
                    endpoints, workingEndpoint, innerCt);
                if (!string.IsNullOrEmpty(newEp)) ep.Add(newEp);

                Interlocked.Increment(ref currentChunk);
                Interlocked.Add(ref uploadedBytes, data.Length);
                if (onProgress is not null)
                    await onProgress(currentChunk, totalChunks, uploadedBytes, fileSize);
            });

            if (!ep.IsEmpty && ep.TryPeek(out var latestEp))
                workingEndpoint = latestEp;
        }
    }

    private async Task<string?> UploadChunkWithFailoverAsync(
        HttpClient client, string token, byte[] data, int fragmentId,
        long rangeStart, long rangeEnd, long totalSize,
        List<string> endpoints, string workingEp, CancellationToken ct)
    {
        var startIdx = Math.Max(0, endpoints.IndexOf(workingEp));

        for (int offset = 0; offset < endpoints.Count; offset++)
        {
            var endpoint = endpoints[(startIdx + offset) % endpoints.Count];

            for (int retry = 0; retry < MaxRetries; retry++)
            {
                try
                {
                    var url = $"https://{endpoint}/api/upload/fragment?upload_token={Uri.EscapeDataString(token)}&fragment_id={fragmentId}";
                    var content = new ByteArrayContent(data);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {rangeStart}-{rangeEnd}/{totalSize}");

                    var resp = await client.PostAsync(url, content, ct);
                    if ((int)resp.StatusCode is >= 200 and < 300 or 206) return endpoint;

                    if (ShouldFailover((int)resp.StatusCode)) break;
                    if (retry < MaxRetries - 1) await Task.Delay(1000 * (retry + 1), ct);
                }
                // 用户暂停/撤回：原样抛 OCE，别当「分片失败」重试（见 CheckResumeAsync 同款注释）。
                // 只认「我们的 ct 取消了」—— HttpClient 自身超时抛的 TaskCanceledException
                // 不算用户取消，继续走下面的故障转移/重试。
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception) when (retry < MaxRetries - 1 && ShouldFailover(0))
                {
                    break; // Failover
                }
                catch (Exception) when (retry < MaxRetries - 1)
                {
                    await Task.Delay(1000 * (retry + 1), ct);
                }
            }
        }
        ct.ThrowIfCancellationRequested();   // 取消优先于「所有 endpoint 都失败」
        throw new Exception($"Chunk {fragmentId} failed on all endpoints");
    }

    private async Task CompleteAsync(
        HttpClient client, string endpoint, string token, int fragmentCount,
        List<string> endpoints, CancellationToken ct)
    {
        var currentEp = endpoint;

        for (int retry = 0; retry < MaxRetries; retry++)
        {
            try
            {
                var url = $"https://{currentEp}/api/upload/complete?upload_token={Uri.EscapeDataString(token)}&fragment_count={fragmentCount}";
                var resp = await client.PostAsync(url, new ByteArrayContent([]), ct);
                var statusCode = (int)resp.StatusCode;
                var body = await resp.Content.ReadAsStringAsync(ct);
                Log.Debug("[Kuaishou CDN] complete response: HTTP {Status}, body={Body}",
                    statusCode, body[..Math.Min(500, body.Length)]);

                if (statusCode is >= 200 and < 300) return;

                if (ShouldFailover(statusCode))
                {
                    currentEp = Failover(currentEp, endpoints);
                    continue;
                }
                throw new Exception($"Complete HTTP {statusCode}: {body[..Math.Min(200, body.Length)]}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户暂停/撤回：别当普通失败重试
            }
            catch (Exception ex) when (ex is not HttpRequestException && retry < MaxRetries - 1)
            {
                if (ShouldFailover(0))
                {
                    currentEp = Failover(currentEp, endpoints);
                    await Task.Delay(1000 * (retry + 1), ct);
                    continue;
                }
                throw;
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new Exception("Complete failed on all endpoints");
    }

    // ═══ Helpers ═══

    private static bool ShouldFailover(int status) =>
        status == 0 || (status >= 500 && status < 600) || FailoverStatusCodes.Contains(status);

    private static string Failover(string current, List<string> endpoints)
    {
        var idx = endpoints.IndexOf(current);
        return endpoints[(idx + 1) % endpoints.Count];
    }

    private static string GuessMimeType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            ".flv" => "video/x-flv",
            ".webm" => "video/webm",
            _ => "video/mp4",
        };

    /// <summary>
    /// 发给平台的 <c>fileName</c> 必须是 ASCII 安全名。
    ///
    /// 2026-11-09 实测：同一个视频、同一个账号，中文文件名
    /// （<c>橘猫为什么大多是公的.mp4</c>）让 <c>upload/finish</c> 连续两次 ×4 重试全部回
    /// <c>result=500002，请稍后重试</c>；把文件复制成 <c>cat_video.mp4</c> 后第一次就通过。
    /// 前面 pre / CDN 分片 / complete 都正常 —— 只有带文件名的 finish 会踩。
    ///
    /// 真实标题走 <c>caption</c>，与这个文件名无关；这里只需要一个稳定、ASCII 的
    /// 上传文件名。保留扩展名（原扩展名不是 ASCII 时回落 <c>.mp4</c>），
    /// 其余非 <c>[A-Za-z0-9._-]</c> 字符一律替换成 <c>_</c>，空名回落 <c>video.mp4</c>。
    /// </summary>
    public static string WireFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        if (name.Length == 0) return "video.mp4";

        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);

        static string Sanitize(string s, string fallback)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
                sb.Append(ch is >= ' ' and <= '~' && (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
                    ? ch
                    : '_');
            var v = sb.ToString().Trim('_', '.', ' ');
            return v.Length > 0 ? v : fallback;
        }

        var safeStem = Sanitize(stem, "video");
        var safeExt = ext.Length > 1 ? Sanitize(ext[1..], "mp4") : "mp4";
        return safeStem + "." + safeExt;
    }
}

/// <summary>上传结果。</summary>
public sealed record KuaishouUploadResult(
    long FileId, string MediaId, string CoverKey,
    int VideoDuration, int Width, int Height,
    string PhotoIdStr);
