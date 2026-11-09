namespace CreatFlow.Platforms.Bilibili;

/// <summary>
/// B站（哔哩哔哩创作中心）上传服务：封面上传 + 视频本体上传。
///
/// **封面上传**（2.5.5）：
/// <code>
/// POST https://member.bilibili.com/x/vu/web/cover/up
///   ?t={毫秒}&amp;x-bili-locale-json=…&amp;x-bili-device-req-json=…&amp;csrf={bili_jct}
/// Referer: https://member.bilibili.com/platform/upload/video/frame
/// Content-Type: multipart/form-data
///
/// 两个**纯文本** part：cover = base64 data URL、csrf = bili_jct
/// </code>
/// 响应：<c>{"code":0,"data":{"url":"https://archive.biliimg.com/bfs/archive/xxx.jpg"}}</c>。
/// 页面把同一张源图裁成两张后各传一次：先 4:3（1080×810 → cover43），后 16:9（1080×607 → cover）。
///
/// **视频本体**（2026-11-07 真账号实测修正）：**不是**直接走裸 UPOS，而是走
/// <c>/upload/multipart/*</c> 三段式（抓包 capture-09-44-23 idx 358/373/405）：
/// <code>
/// POST {member}/upload/multipart/new
///   {"profile":"ugcfx/bup","name":"house.mp4","size":9333449}
/// → data.uri + data.upload_token + **data.biz_id**（= add/v3 的 videos[].cid）+ filename
///
/// POST {member}/upload/multipart/part
///   {"uri":…,"upload_token":…,"part_number":N}
/// → data.reqs[] = 预签名 PUT URL（多个 CDN 候选）
///
/// PUT {presigned url}（分片二进制）→ 响应头 ETag（**带引号，原样回传**）
///
/// POST {member}/upload/multipart/complete
///   {"uri":…,"upload_token":…,"parts":[{"part_number":N,"etag":"\"…\""}],
///    "upload_params":{"biz_id":…,"profile":"ugcfx/bup"}}
/// </code>
///
/// ⚠️ 2026-11-07 踩过的坑：先用裸 UPOS（preupload → UPOS init/part/merge →
/// <c>archive/types/upload</c>）实现，前四步都能过，但**拿不到 cid** ——
/// <c>archive/types/upload</c> 对视频返回的是 <c>{"code":0,"message":"OK","ttl":1}</c> 空壳；
/// 视频的 cid 在 <c>multipart/new</c> 的 <c>biz_id</c> 里。封面帧 zip（svf/bup）与视频（ugcfx/bup）
/// 走的是两套入口，别拿帧抓包当视频抓包用。
/// </summary>
public sealed class BilibiliUploadService
{
    /// <summary>封面上传路径（抓包 <c>POST /x/vu/web/cover/up</c>）</summary>
    public const string CoverUploadPath = "/x/vu/web/cover/up";

    /// <summary>封面窗口的 Referer（抓包请求头原文）</summary>
    public const string CoverReferer = "https://member.bilibili.com/platform/upload/video/frame";

    /// <summary>页面每个 member 请求都带的 locale 参数（抓包原文）</summary>
    public const string LocaleJson =
        """{"c_locale":{"language":"zh","script":"Hans"},"always_translate":false}""";

    /// <summary>页面每个 member 请求都带的设备参数（抓包原文）</summary>
    public const string DeviceJson =
        """{"platform":"web","device":"pc","mobi_app":"web_cn","spmid":"0.0"}""";

    /// <summary>视频投稿的上传 profile（封面帧 zip 是 <c>svf/bup</c>）</summary>
    public const string VideoProfile = "ugcfx/bup";

    /// <summary>视频上传的 Referer（抓包原文）</summary>
    public const string VideoReferer = "https://member.bilibili.com/platform/upload/video/frame";

    /// <summary>视频上传三段式接口</summary>
    public const string MultipartNewPath = "/upload/multipart/new";
    /// <inheritdoc cref="MultipartNewPath"/>
    public const string MultipartPartPath = "/upload/multipart/part";
    /// <inheritdoc cref="MultipartNewPath"/>
    public const string MultipartCompletePath = "/upload/multipart/complete";

    /// <summary>没有从响应拿到 chunk_size 时的兜底</summary>
    private const long DefaultChunkSize = 10 * 1024 * 1024;

    /// <summary>请求体序列化：中文文件名按 UTF-8 原文（与页面同形，不转 \uXXXX）。</summary>
    private static readonly JsonWriterOptions BodyJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 本地保护性上限（**不是**平台测得的限制）：页面输出约 60~85 KB，
    /// 10 MB 只是防呆，避免把手机原图直接 base64 膨胀后发出去。
    /// </summary>
    private const long MaxCoverBytes = 10 * 1024 * 1024;

    private readonly PlatformOptions _options;

    public BilibiliUploadService(PlatformOptions options)
    {
        _options = options;
    }

    // ═══════════════════════════════════════════════════════════════
    //  封面上传
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 上传两张封面 → <c>add/v3</c> 的 <c>cover</c> / <c>cover43</c> URL。
    /// 上传顺序与页面一致：先 4:3、后 16:9。
    /// 文件缺失 / Cookie 缺 bili_jct / 响应 code≠0 → <see cref="BilibiliApiException"/>。
    /// </summary>
    public async Task<BilibiliCover> UploadCoverAsync(
        string cookie, string cover16x9Path, string cover43Path,
        CancellationToken ct = default)
    {
        var csrf = RequireCsrf(cookie);

        var dataUrl43 = BuildCoverDataUrl(await ReadCoverAsync(cover43Path, "4:3", ct));
        var dataUrl169 = BuildCoverDataUrl(await ReadCoverAsync(cover16x9Path, "16:9", ct));

        using var client = new BilibiliApiClient(cookie, _options);

        // 页面实测：先传 4:3（响应 URL 进 cover43），再传 16:9（响应 URL 进 cover）
        var url43 = await UploadOneAsync(client, csrf, dataUrl43, ct);
        var url169 = await UploadOneAsync(client, csrf, dataUrl169, ct);

        return new BilibiliCover(CoverUrl: url169, Cover43Url: url43);
    }

    private async Task<string> UploadOneAsync(
        BilibiliApiClient client, string csrf, string dataUrl, CancellationToken ct)
    {
        var url = BuildCoverUploadUrl(csrf, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            _options.BilibiliHost);
        using var form = BuildCoverForm(dataUrl, csrf);
        var body = await client.PostMultipartAsync(url, form, CoverReferer, ct);
        return ParseCoverUrl(body);
    }

    // ═══════════════════════════════════════════════════════════════
    //  视频本体上传
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 上传视频本体：<c>multipart/new</c> → 逐分片 <c>multipart/part</c> + 预签名 PUT
    /// → <c>multipart/complete</c>。
    /// 返回 <see cref="BilibiliUploadedVideo"/>（<c>Filename</c> = 响应里的 filename，
    /// 就是 <c>add/v3</c> 的 <c>videos[].filename</c>；<c>Cid</c> = 响应里的 biz_id）。
    /// </summary>
    public async Task<BilibiliUploadedVideo> UploadVideoAsync(
        string cookie, string videoPath,
        Action<long, long>? onBytes = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookie))
            throw new BilibiliApiException("未找到B站登录凭证，请重新登录", -101);

        var info = new FileInfo(videoPath);
        if (!info.Exists)
            throw new BilibiliApiException($"视频文件不存在：{videoPath}", -1);
        if (info.Length <= 0)
            throw new BilibiliApiException($"视频文件是空文件：{videoPath}", -1);

        using var client = new BilibiliApiClient(cookie, _options);

        var newBody = await client.PostJsonAsync(
            BuildMemberUrl(_options.BilibiliHost, MultipartNewPath, _options.TimeoutSeconds),
            BuildMultipartNewBody(info.Name, info.Length),
            VideoReferer, ct);
        var session = ParseMultipartNew(newBody);

        using var cdn = CreateCdnClient();
        var parts = await UploadPartsAsync(
            client, cdn, session, videoPath, info.Length, onBytes, ct);

        var completeBody = await client.PostJsonAsync(
            BuildMemberUrl(_options.BilibiliHost, MultipartCompletePath, _options.TimeoutSeconds),
            BuildMultipartCompleteBody(session, parts),
            VideoReferer, ct);
        ParseMultipartComplete(completeBody);

        return new BilibiliUploadedVideo(session.Filename, session.BizId);
    }

    private static HttpClient CreateCdnClient()
    {
        var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(600),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36");
        return http;
    }

    private async Task<List<BilibiliMultipartPart>> UploadPartsAsync(
        BilibiliApiClient client, HttpClient cdn, BilibiliMultipartSession session,
        string videoPath, long total, Action<long, long>? onBytes, CancellationToken ct)
    {
        var chunkSize = session.ChunkSize > 0 ? session.ChunkSize : DefaultChunkSize;
        var chunks = (int)((total + chunkSize - 1) / chunkSize);
        var concurrency = Math.Clamp(session.Threads, 1, 8);
        var rounds = Math.Clamp(session.Retry, 1, 3);
        var parts = new BilibiliMultipartPart[chunks];
        var sent = 0L;

        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = Enumerable.Range(1, chunks).Select(async partNumber =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var start = (partNumber - 1) * chunkSize;
                var size = (int)Math.Min(chunkSize, total - start);
                var buffer = new byte[size];
                await using (var fs = new FileStream(
                    videoPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 81920, useAsync: true))
                {
                    fs.Position = start;
                    await fs.ReadExactlyAsync(buffer, ct);
                }

                // 每次 part 请求都会给出多个 CDN 候选 URL；失败换下一个 / 下一轮
                var partJson = await client.PostJsonAsync(
                    BuildMemberUrl(_options.BilibiliHost, MultipartPartPath, _options.TimeoutSeconds),
                    BuildMultipartPartBody(session, partNumber),
                    VideoReferer, ct);
                var urls = ParsePresignedUrls(partJson, $"multipart/part {partNumber}");

                string? etag = null;
                Exception? lastError = null;
                for (var round = 0; round < rounds && etag is null; round++)
                {
                    foreach (var url in urls)
                    {
                        try
                        {
                            using var content = new ByteArrayContent(buffer);
                            content.Headers.ContentType =
                                new System.Net.Http.Headers.MediaTypeHeaderValue(
                                    "application/octet-stream");
                            using var req = new HttpRequestMessage(HttpMethod.Put, url)
                            {
                                Content = content,
                            };
                            using var resp = await cdn.SendAsync(req, ct);
                            var body = Encoding.UTF8.GetString(
                                await resp.Content.ReadAsByteArrayAsync(ct));
                            if (!resp.IsSuccessStatusCode)
                            {
                                lastError = new BilibiliApiException(
                                    $"B站分片 {partNumber}/{chunks} 上传 HTTP "
                                    + $"{(int)resp.StatusCode}：{Snippet(body)}",
                                    (int)resp.StatusCode, body);
                                continue;
                            }

                            // S3 预签名 PUT：ETag 在响应头里，**带引号**，complete 时要原样回传
                            etag = resp.Headers.ETag?.Tag;
                            if (string.IsNullOrWhiteSpace(etag))
                                etag = "\"" + Convert.ToHexString(MD5.HashData(buffer))
                                    .ToLowerInvariant() + "\"";
                            break;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                        }
                    }
                }

                if (etag is null)
                {
                    throw lastError as BilibiliApiException
                        ?? new BilibiliApiException(
                            $"B站分片 {partNumber}/{chunks} 上传失败：{lastError?.Message}", -1);
                }

                parts[partNumber - 1] = new BilibiliMultipartPart(partNumber, etag);
                var done = Interlocked.Add(ref sent, size);
                onBytes?.Invoke(done, total);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return [.. parts];
    }

    // ═══ 纯函数（自检直接验 wire 形体） ═══

    /// <summary>member 域 URL：<c>{host}{path}</c>（调用方各自拼 query）。</summary>
    public static string BuildMemberUrl(string? host, string path, int timeoutSeconds = 0)
    {
        var baseHost = string.IsNullOrWhiteSpace(host)
            ? "https://member.bilibili.com"
            : host.TrimEnd('/');
        return baseHost + path;
    }

    /// <summary>
    /// <c>multipart/new</c> 请求体：<c>{"profile":"ugcfx/bup","name":…,"size":…}</c>
    /// （抓包 idx 358；<c>name</c> 是**原始文件名带扩展名**）。
    /// </summary>
    public static string BuildMultipartNewBody(string fileName, long sizeBytes)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, BodyJsonOptions))
        {
            w.WriteStartObject();
            w.WriteString("profile", VideoProfile);
            w.WriteString("name", fileName);
            w.WriteNumber("size", sizeBytes);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary><c>multipart/part</c> 请求体：<c>{"uri":…,"upload_token":…,"part_number":N}</c></summary>
    public static string BuildMultipartPartBody(BilibiliMultipartSession session, int partNumber)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, BodyJsonOptions))
        {
            w.WriteStartObject();
            w.WriteString("uri", session.Uri);
            w.WriteString("upload_token", session.UploadToken);
            w.WriteNumber("part_number", partNumber);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// <c>multipart/complete</c> 请求体：parts 的 etag **带引号原样回传**
    /// （抓包：<c>"etag":"\"0fd06fa9…\""</c>），upload_params 带 biz_id + profile。
    /// </summary>
    public static string BuildMultipartCompleteBody(
        BilibiliMultipartSession session, IReadOnlyList<BilibiliMultipartPart> parts)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, BodyJsonOptions))
        {
            w.WriteStartObject();
            w.WriteString("uri", session.Uri);
            w.WriteString("upload_token", session.UploadToken);
            w.WriteStartArray("parts");
            foreach (var p in parts)
            {
                w.WriteStartObject();
                w.WriteNumber("part_number", p.PartNumber);
                w.WriteString("etag", p.ETag);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("upload_params");
            w.WriteNumber("biz_id", session.BizId);
            w.WriteString("profile", VideoProfile);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// 解析 <c>multipart/new</c> 响应：<c>data.uri / upload_token / biz_id / filename</c>
    /// + 分片参数。<c>biz_id</c> 就是 <c>add/v3</c> 的 <c>videos[].cid</c>。
    /// </summary>
    public static BilibiliMultipartSession ParseMultipartNew(string bodyText)
    {
        var root = ParseJsonObject(bodyText, "multipart/new");
        RequireCodeZero(root, bodyText, "上传初始化");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new BilibiliApiException(
                $"B站上传初始化响应里没有 data：{Snippet(bodyText)}", -1, bodyText);

        var uri = GetString(data, "uri")
            ?? throw new BilibiliApiException(
                $"B站上传初始化响应里没有 data.uri：{Snippet(bodyText)}", -1, bodyText);
        var token = GetString(data, "upload_token")
            ?? throw new BilibiliApiException(
                $"B站上传初始化响应里没有 data.upload_token：{Snippet(bodyText)}", -1, bodyText);
        var filename = GetString(data, "filename")
            ?? throw new BilibiliApiException(
                $"B站上传初始化响应里没有 data.filename：{Snippet(bodyText)}", -1, bodyText);
        var bizId = GetLong(data, "biz_id") ?? 0;
        if (bizId <= 0)
            throw new BilibiliApiException(
                $"B站上传初始化响应里没有有效的 data.biz_id（= 稿件 cid）：{Snippet(bodyText)}",
                -1, bodyText);

        return new BilibiliMultipartSession(
            Uri: uri,
            UploadToken: token,
            BizId: bizId,
            Filename: filename,
            ChunkSize: (int)(GetLong(data, "chunk_size") ?? DefaultChunkSize),
            Threads: (int)(GetLong(data, "threads") ?? 1),
            Retry: (int)(GetLong(data, "retry") ?? 3),
            RetryDelay: (int)(GetLong(data, "retry_delay") ?? 0));
    }

    /// <summary>解析 <c>multipart/part</c> 响应 → 预签名 PUT URL 列表（按 CDN 优先级）。</summary>
    public static List<string> ParsePresignedUrls(string bodyText, string what)
    {
        var root = ParseJsonObject(bodyText, what);
        RequireCodeZero(root, bodyText, what);

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("reqs", out var reqs) || reqs.ValueKind != JsonValueKind.Array)
        {
            throw new BilibiliApiException(
                $"B站 {what} 响应里没有 data.reqs：{Snippet(bodyText)}", -1, bodyText);
        }

        var urls = new List<string>();
        foreach (var item in reqs.EnumerateArray())
        {
            if (GetString(item, "url") is { Length: > 0 } url) urls.Add(url);
        }
        if (urls.Count == 0)
            throw new BilibiliApiException(
                $"B站 {what} 响应里没有可用的预签名 URL：{Snippet(bodyText)}", -1, bodyText);
        return urls;
    }

    /// <summary>校验 <c>multipart/complete</c> 响应（只要求 code=0；成功时 data.etag 可忽略）。</summary>
    public static void ParseMultipartComplete(string bodyText)
        => RequireCodeZero(bodyText, "multipart/complete");

    private static void RequireCodeZero(string bodyText, string what)
        => RequireCodeZero(ParseJsonObject(bodyText, what), bodyText, what);

    private static void RequireCodeZero(JsonElement root, string bodyText, string what)
    {
        var code = GetInt(root, "code") ?? -1;
        if (code == 0) return;
        var message = GetString(root, "message") ?? "?";
        var hint = code == -101 ? "（B站登录已过期，请打开 B站账号窗口重新登录）" : "";
        throw new BilibiliApiException(
            $"B站{what}失败：code={code}，message={message}{hint}", code, bodyText);
    }

    // ═══ 封面 URL / data URL / 编码 ═══

    /// <summary>
    /// 组装封面上传的 multipart 体：<c>cover</c> = base64 data URL 文本、<c>csrf</c> = bili_jct。
    /// 两个 part 都是**纯文本字段**（浏览器 FormData 追加字符串的形态）：
    /// 没有 filename、没有 Content-Type —— 与抓包原文一致。
    /// </summary>
    public static MultipartFormDataContent BuildCoverForm(string coverDataUrl, string csrf)
    {
        var form = new MultipartFormDataContent
        {
            { TextPart(coverDataUrl), "cover" },
            { TextPart(csrf), "csrf" },
        };
        return form;
    }

    private static ByteArrayContent TextPart(string value)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
        // 浏览器 FormData 的文本字段不带 Content-Type；StringContent 默认会加
        // text/plain; charset=utf-8，这里显式清掉，保持与抓包同形
        content.Headers.ContentType = null;
        return content;
    }

    /// <summary>
    /// 组装封面请求 URL（抓包参数顺序：t → x-bili-locale-json → x-bili-device-req-json → csrf）。
    /// 参数序列化按 axios 习惯：<c>:</c> <c>,</c> <c>$</c> <c>[</c> <c>]</c> 不转义
    /// （抓包里 <c>%7B%22c_locale%22:%7B…</c> 就是这种编码）。
    /// </summary>
    public static string BuildCoverUploadUrl(
        string csrf, long timestampMs, string? host = null)
    {
        var baseHost = string.IsNullOrWhiteSpace(host)
            ? "https://member.bilibili.com"
            : host.TrimEnd('/');
        return $"{baseHost}{CoverUploadPath}?t={timestampMs}"
               + $"&x-bili-locale-json={AxiosEscape(LocaleJson)}"
               + $"&x-bili-device-req-json={AxiosEscape(DeviceJson)}"
               + $"&csrf={AxiosEscape(csrf)}";
    }

    /// <summary>axios 的参数编码：encodeURIComponent 后把 <c>:</c> <c>,</c> <c>$</c> <c>[</c> <c>]</c> 还原。
    /// 话题 / 标签推荐（<see cref="Topics.BilibiliTopicApi"/>）的 locale/device 参数同款编码。</summary>
    internal static string AxiosEscape(string value) => Uri.EscapeDataString(value)
        .Replace("%3A", ":", StringComparison.OrdinalIgnoreCase)
        .Replace("%2C", ",", StringComparison.OrdinalIgnoreCase)
        .Replace("%24", "$", StringComparison.OrdinalIgnoreCase)
        .Replace("%5B", "[", StringComparison.OrdinalIgnoreCase)
        .Replace("%5D", "]", StringComparison.OrdinalIgnoreCase)
        .Replace("%20", "+", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 封面字节 → <c>data:image/{png|jpeg|…};base64,…</c>（抓包原文就是这种形态）。
    /// 只认 JPEG / PNG / GIF / BMP / WebP 文件头；认不出 → 报错（不把任意文件发上去）。
    /// </summary>
    public static string BuildCoverDataUrl(ReadOnlySpan<byte> bytes)
    {
        var mime = DetectImageMime(bytes)
            ?? throw new BilibiliApiException(
                "封面图不是可识别的图片（JPEG / PNG / GIF / BMP / WebP），无法上传", -1);
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>读文件头判断图片 MIME；认不出 → null。</summary>
    public static string? DetectImageMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        if (bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F')
            return "image/gif";
        if (bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M')
            return "image/bmp";
        if (bytes.Length >= 12
            && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return "image/webp";
        return null;
    }

    /// <summary>
    /// 解析封面上传响应 → <c>data.url</c>。
    /// <c>code≠0</c> → 报错（带平台 message；-101 追加登录过期引导）；
    /// 响应不是 JSON / 没有 data.url → 报错并附原始响应（不猜字段、不静默）。
    /// </summary>
    public static string ParseCoverUrl(string bodyText)
    {
        var root = ParseJsonObject(bodyText, "封面上传");
        RequireCodeZero(root, bodyText, "封面上传");

        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("url", out var urlEl)
            || urlEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(urlEl.GetString()))
        {
            throw new BilibiliApiException(
                $"B站封面上传：响应里没有 data.url，原文：{Snippet(bodyText)}", -1, bodyText);
        }

        return urlEl.GetString()!;
    }

    /// <summary>从 Cookie 串里取 bili_jct（csrf）。缺了 → 明确引导重新登录。</summary>
    public static string RequireCsrf(string cookie)
    {
        var csrf = ExtractCookieValue(cookie, "bili_jct");
        if (string.IsNullOrWhiteSpace(csrf))
        {
            throw new BilibiliApiException(
                "B站 Cookie 里没有 bili_jct（登录未完成或会话已过期），"
                + "请打开 B站账号窗口重新登录后再试", -1);
        }
        return csrf;
    }

    /// <summary>从 Cookie 串里取一个值（同名取第一个；B站 cookie 名大小写敏感）。</summary>
    public static string? ExtractCookieValue(string? cookie, string name)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return null;
        foreach (var item in cookie.Split(';'))
        {
            var trimmed = item.Trim();
            var eq = trimmed.IndexOf('=');
            if (eq > 0 && trimmed[..eq] == name)
                return trimmed[(eq + 1)..];
        }
        return null;
    }

    private static async Task<byte[]> ReadCoverAsync(
        string path, string ratio, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new BilibiliApiException($"B站封面（{ratio}）文件不存在：{path}", -1);

        var info = new FileInfo(path);
        if (info.Length <= 0)
            throw new BilibiliApiException($"B站封面（{ratio}）是空文件：{path}", -1);
        if (info.Length > MaxCoverBytes)
        {
            throw new BilibiliApiException(
                $"B站封面（{ratio}）{info.Length / 1024.0 / 1024.0:0.#} MB，"
                + $"超过本地保护上限 {MaxCoverBytes / 1024 / 1024} MB", -1);
        }

        return await File.ReadAllBytesAsync(path, ct);
    }

    // ═══ JSON 小工具 ═══

    private static JsonDocument ParseJson(string bodyText, string what)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            throw new BilibiliApiException($"B站 {what}：响应为空", -1, bodyText);
        try
        {
            return JsonDocument.Parse(bodyText);
        }
        catch (JsonException ex)
        {
            throw new BilibiliApiException(
                $"B站 {what}：响应不是 JSON（{ex.Message}），原文：{Snippet(bodyText)}",
                -1, bodyText);
        }
    }

    private static JsonElement ParseJsonObject(string bodyText, string what)
    {
        using var doc = ParseJson(bodyText, what);
        var root = doc.RootElement.Clone();   // 与 doc 解耦，using 释放后仍可用
        if (root.ValueKind != JsonValueKind.Object)
            throw new BilibiliApiException(
                $"B站 {what}：响应结构认不出（根节点 {root.ValueKind}），原文：{Snippet(bodyText)}",
                -1, bodyText);
        return root;
    }

    private static int? GetInt(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var el)
        && el.ValueKind == JsonValueKind.Number
        && el.TryGetInt32(out var value)
            ? value
            : null;

    private static long? GetLong(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var number))
            return number;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var el)
        && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string Snippet(string text) =>
        text.Length > 300 ? text[..300] + "…" : text;
}

/// <summary>
/// <c>multipart/new</c> 响应 → 上传会话。
/// <c>BizId</c> 是稿件 cid（<c>add/v3.videos[].cid</c>）；
/// <c>Filename</c> 是平台生成的投稿文件名（不含扩展名）。
/// </summary>
public sealed record BilibiliMultipartSession(
    string Uri,
    string UploadToken,
    long BizId,
    string Filename,
    int ChunkSize,
    int Threads,
    int Retry,
    int RetryDelay);

/// <summary>一个分片的 part_number（从 1 起）与 S3 响应头里的 ETag（**含引号**）。</summary>
public sealed record BilibiliMultipartPart(int PartNumber, string ETag);

/// <summary>
/// 视频上传结果。<c>Filename</c> = <c>multipart/new</c> 返回的 filename
/// （<c>add/v3</c> 的 <c>videos[].filename</c> 用的就是它）；<c>Cid</c> = 同一响应里的 biz_id。
/// </summary>
public sealed record BilibiliUploadedVideo(string Filename, long Cid);

/// <summary>
/// 封面上传结果。<c>CoverUrl</c> = 16:9（add/v3 的 cover / 个人空间封面），
/// <c>Cover43Url</c> = 4:3（add/v3 的 cover43 / 首页推荐封面）。
/// </summary>
public sealed record BilibiliCover(string CoverUrl, string Cover43Url)
{
    /// <summary>落到 <c>add/v3</c> 的两个封面字段（顺序与命名固定，别对调）。</summary>
    public (string Cover, string Cover43) ToAddV3CoverFields() => (CoverUrl, Cover43Url);
}

/// <summary>B站 API 错误。</summary>
public sealed class BilibiliApiException : Exception
{
    public int Code { get; }
    public string? ResponseBody { get; }

    public BilibiliApiException(string message, int code, string? responseBody = null)
        : base(message)
    {
        Code = code;
        ResponseBody = responseBody;
    }
}
