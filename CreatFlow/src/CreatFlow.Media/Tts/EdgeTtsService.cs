using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security;

namespace CreatFlow.Media.Tts;

/// <summary>
/// Edge TTS 语音合成 — 逆向自微软 Edge「大声朗读」免费接口。
/// 参考 Python edge-tts 7.2.8（2024-10 后协议加了硬性校验，tts-vue-main 旧代码已失效；
/// 完整要点见 memory: edge-tts-reverse-engineered-api，2026-08 全链路实测通过）。
///
/// 协议要点:
///   WS 端点 .../edge/v1?TrustedClientToken=...&amp;ConnectionId=&lt;no-dash-uuid&gt;
///       &amp;Sec-MS-GEC=&lt;sha256 hex&gt;&amp;Sec-MS-GEC-Version=1-143.0.3650.75
///   每个请求: 发 Path:speech.config 文本帧(固定 JSON) → 发 Path:ssml 文本帧(带 X-RequestId)
///   响应: 文本帧 Path:turn.start/turn.end 标记边界; 二进制帧前 2 字节为头长度前缀,
///         跳过前缀后搜 "Path:audio\r\n", 其后为音频数据; 按 requestId 聚合
///
/// 工程决策:
///   - internal（外部一律走 ITtsProvider/EdgeTtsProvider，本类仅 Edge 实现细节）
///   - 单例连接 + SemaphoreSlim 串行化 + Pending 表按 requestId 分派
///   - speech.config 为固定字符串字面量(obfuscar 免疫, 不用 System.Text.Json DTO)
///   - 风险: 非公开接口, 随时可能加签名校验; 失效时兜底 Azure Speech SDK(memory: edge-tts-reverse-engineered-api)
/// </summary>
internal static class EdgeTtsService
{
    private const string BaseUrl =
        "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1" +
        "?TrustedClientToken=6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string Origin = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold";

    /// <summary>
    /// 服务端要求的浏览器版本（edge-tts 7.x 校验 Sec-MS-GEC，版本需匹配新式 Chrome/Edg 头）
    /// </summary>
    private const string ChromiumVersion = "143.0.3650.75";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" +
        " (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";

    /// <summary>输出格式: 24kHz 48kbps 单声道 mp3（Edge 返回无 ID3 头的裸帧，字节拼接即可）</summary>
    private const string OutputFormat = "audio-24khz-48kbitrate-mono-mp3";

    // ⚠️ 消息体一律用字符串拼接，禁止 string.Format：
    //    speech.config 的 JSON 含花括号 {"context":...}，模板带 {0} 会被解析成格式占位符 → FormatException
    private const string SpeechConfigHeader =
        "X-Timestamp:{ts}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n";

    /// <summary>发送串行化：同一连接上 config/ssml 必须按顺序发，且接收端按 requestId 分派</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object PendingLock = new();
    private static readonly Dictionary<string, PendingRequest> Pending = new();
    private static readonly object ConnLock = new();   // 保护 _ws/_receiveAlive（接收循环退出与复用判定的竞态）
    private static ClientWebSocket? _ws;
    private static bool _receiveAlive;                 // 接收循环存活标志：State 滞后于实际关闭，不能只信 State
    private static int _idleGeneration;

    private sealed class PendingRequest(TaskCompletionSource<(byte[] Audio, List<SubtitleItem> Subtitles)> tcs)
    {
        public TaskCompletionSource<(byte[] Audio, List<SubtitleItem> Subtitles)> Tcs { get; } = tcs;
        public MemoryStream Audio { get; } = new();
        public List<SubtitleItem> Sentences { get; } = new(); // 句级：权威文本 + 粗时间（含标点）
        public List<SubtitleItem> Words { get; } = new();     // 词级：精确时间（不含标点/空白）
    }

    /// <summary>
    /// 合成语音。长文本（&gt;400 字）自动按句读标点切片（≤300 字/片）顺序合成后拼接。
    /// </summary>
    /// <param name="text">配音文本</param>
    /// <param name="voice">音色 shortName（如 zh-CN-XiaoxiaoNeural），即 SSML voice name</param>
    /// <param name="style">说话风格（null/空 = 常规，不输出 express-as）</param>
    /// <param name="speed">语速倍率（1.0 = 原速，SSML rate = (speed-1)*100%）</param>
    /// <param name="pitch">音调倍率（1.0 = 原调，SSML pitch = (pitch-1)*50%）</param>
    /// <param name="onSliceProgress">切片进度回调（后台线程触发，调用方负责 UI 编组）</param>
    public static async Task<byte[]> SynthesizeAsync(
        string text,
        string voice,
        string? style,
        double speed,
        double pitch,
        Action<string>? onSliceProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("文本不能为空", nameof(text));

        var slices = SplitText(text);
        var parts = new List<byte[]>(slices.Count);
        for (var i = 0; i < slices.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            onSliceProgress?.Invoke(slices.Count == 1
                ? "正在合成语音..."
                : $"正在合成第 {i + 1}/{slices.Count} 段...");

            var ssml = BuildSsml(slices[i], voice, style, speed, pitch);
            var result = await SynthesizeSliceWithRetryAsync(ssml, withMetadata: false, ct).ConfigureAwait(false);
            parts.Add(result.Audio);
        }

        using var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    /// <summary>
    /// 合成语音并返回词级对齐字幕（供视频合成烧录）。
    /// 与 SynthesizeAsync 同参数，额外开启 sentenceBoundary+wordBoundary metadata；
    /// 字幕时间戳按切片偏移累加（每个请求的 metadata 从 0 开始）。
    /// metadata 解析失败不影响合成（字幕降级为空列表）。
    /// </summary>
    public static async Task<(byte[] Audio, List<SubtitleItem> Subtitles)> SynthesizeWithSubtitlesAsync(
        string text,
        string voice,
        string? style,
        double speed,
        double pitch,
        Action<string>? onSliceProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("文本不能为空", nameof(text));

        var slices = SplitText(text);
        var parts = new List<byte[]>(slices.Count);
        var subtitles = new List<SubtitleItem>();
        var sliceBaseMs = 0; // 跨片偏移累加：每片的 metadata Offset 从 0 开始

        for (var i = 0; i < slices.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            onSliceProgress?.Invoke(slices.Count == 1
                ? "正在合成语音..."
                : $"正在合成第 {i + 1}/{slices.Count} 段...");

            var ssml = BuildSsml(slices[i], voice, style, speed, pitch);
            var result = await SynthesizeSliceWithRetryAsync(ssml, withMetadata: true, ct).ConfigureAwait(false);
            parts.Add(result.Audio);

            // 本片字幕 + 基准偏移（上一片末尾 + ~300ms 尾音余量）
            foreach (var s in result.Subtitles)
                subtitles.Add(new SubtitleItem(s.Text, sliceBaseMs + s.StartMs, sliceBaseMs + s.EndMs));
            var lastEnd = result.Subtitles.Count > 0 ? result.Subtitles[^1].EndMs : 0;
            sliceBaseMs += lastEnd + 300;
        }

        using var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return (ms.ToArray(), subtitles);
    }

    /// <summary>
    /// 构建 SSML。注意：说话风格（mstts:express-as）已被微软在 Edge 免费端点禁用
    /// （2025 起逐步限制，实测 2026-08 返回 "SSML is invalid"，edge-tts 官方也已移除），
    /// 因此只输出 voice + prosody；风格需等 Azure Speech SDK 兜底方案（官方支持 SSML）。
    /// 文本必须 XML 转义，否则服务端报错或返回静音。
    /// </summary>
    internal static string BuildSsml(string text, string voice, string? style, double speed, double pitch)
    {
        var rate = (int)Math.Round((speed - 1) * 100);
        var pct = (int)Math.Round((pitch - 1) * 50);
        var escaped = SecurityElement.Escape(Sanitize(text));

        return
            "<speak xmlns=\"http://www.w3.org/2001/10/synthesis\" " +
            "xmlns:mstts=\"http://www.w3.org/2001/mstts\" " +
            "xmlns:emo=\"http://www.w3.org/2009/10/emotionml\" version=\"1.0\" xml:lang=\"en-US\">" +
            $"<voice name=\"{voice}\">" +
            $"<prosody rate=\"{rate}%\" pitch=\"{pct}%\">{escaped}</prosody>" +
            "</voice></speak>";
    }

    /// <summary>清洗控制字符（SSML 不允许）；换行统一为 \n</summary>
    private static string Sanitize(string text) =>
        Regex.Replace(text.Replace("\r\n", "\n"), @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "");

    /// <summary>
    /// 长文本切片。≤400 字不切；否则在 max 处回退到最近句读标点切，
    /// 无标点则硬切 max（vue 原版在此会切出 1 字符，是 bug，这里修正）。
    /// </summary>
    private static List<string> SplitText(string text, int max = 300)
    {
        if (text.Length <= 400) return [text];

        var delimiters = new[] { '，', '。', '？', '！', ',', '.', '?', '!', '；', ';', '\n' };
        var result = new List<string>();
        var buffer = new StringBuilder();
        var lastDelim = -1;

        foreach (var ch in text)
        {
            buffer.Append(ch);
            if (Array.IndexOf(delimiters, ch) >= 0) lastDelim = buffer.Length - 1;

            if (buffer.Length >= max)
            {
                var cut = lastDelim > 0 ? lastDelim + 1 : buffer.Length; // 无标点硬切
                result.Add(buffer.ToString(0, cut));
                buffer.Remove(0, cut);
                lastDelim = -1;
            }
        }
        if (buffer.Length > 0) result.Add(buffer.ToString());
        return result;
    }

    /// <summary>建立（或复用）连接。握手失败时去掉 UA 重试一次（保留 Origin）。</summary>
    private static async Task<ClientWebSocket> EnsureConnectedAsync(CancellationToken ct)
    {
        // 只信 State 会复用"正在关闭/已死"的连接（State 滞后于 TCP 关闭，接收循环退出才是权威）→ 发出去的请求没人接
        lock (ConnLock)
        {
            if (_ws is { State: WebSocketState.Open } && _receiveAlive) return _ws;
            _ws?.Dispose();
            _ws = null;
            _receiveAlive = false;
        }

        var ws = new ClientWebSocket();
        // 服务端要求 permessage-deflate 扩展（edge-tts 的 compress=15 对应）
        ws.Options.DangerousDeflateOptions = new WebSocketDeflateOptions
        {
            ClientMaxWindowBits = 15,
            ServerMaxWindowBits = 15,
        };
        ws.Options.SetRequestHeader("Origin", Origin);
        ws.Options.SetRequestHeader("User-Agent", UserAgent);
        try
        {
            await ConnectAsync(ws, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 部分 .NET 实现不允许改 UA（浏览器保留头），去掉后重试一次
            Log.Warning(ex, "[EdgeTts] 带 UA 握手失败，去掉 UA 重试");
            ws.Dispose();
            ws = new ClientWebSocket();
            ws.Options.DangerousDeflateOptions = new WebSocketDeflateOptions
            {
                ClientMaxWindowBits = 15,
                ServerMaxWindowBits = 15,
            };
            ws.Options.SetRequestHeader("Origin", Origin);
            await ConnectAsync(ws, ct).ConfigureAwait(false);
        }

        lock (ConnLock) { _ws = ws; _receiveAlive = true; }
        _ = ReceiveLoopAsync(ws); // 每连接一个接收循环，CancellationToken.None（循环自己收尾）
        return ws;
    }

    private static async Task ConnectAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // ConnectionId 必须是 no-dash UUID（RFC 4122 格式，含版本位）——纯随机 hex 会被拒绝
        var connId = Guid.NewGuid().ToString("N");

        // 2024-10 起服务端要求 Sec-MS-GEC 签名（基于 5 分钟窗口的 FILETIME + TrustedClientToken 的 SHA256），
        // 缺失/过期一律 403。tts-vue-main 是旧协议无需签名；edge-tts 7.x 已在用，算法见 GenerateSecMsGec。
        var url = BaseUrl +
                  "&ConnectionId=" + connId +
                  "&Sec-MS-GEC=" + GenerateSecMsGec() +
                  "&Sec-MS-GEC-Version=1-" + ChromiumVersion;

        // muid Cookie + 浏览器请求头（照 edge-tts 7.2.8 WSS_HEADERS）
        ws.Options.SetRequestHeader("Cookie", $"muid={RandomNumberGenerator.GetHexString(16, true)};"); // 32 位大写 hex
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");

        await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sec-MS-GEC 签名（与 edge-tts 7.x DRM.generate_sec_ms_gec 逐位一致，2026-08 实测比对）。
    ///
    /// 注意必须用 double 浮点路径（ticks *= 1e7 后值 ~1.34e17 超出 double 53 位精度，
    /// Python 的 float 同样丢精度，"F0" 格式化输出与 Python f"{ticks:.0f}" 一致）；
    /// 用 long 整数路径会在最后几位产生偏差 → token 不匹配 → 403。
    /// </summary>
    private static string GenerateSecMsGec()
    {
        double ticks = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 11644473600.0; // FILETIME epoch (1601)
        ticks -= ticks % 300.0;          // 向下取整到 5 分钟窗口
        ticks *= 10_000_000.0;           // 转 100ns 间隔（FILETIME 单位）

        var str = ticks.ToString("F0", CultureInfo.InvariantCulture) +
                  "6A5AA1D4EAFF4E9FB37E23D68491D6F4"; // 十进制整数 + TrustedClientToken
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(str))); // 大写 hex
    }

    /// <summary>单切片转换：Gate 内发 config+ssml，Gate 外等结果（20s 超时）。</summary>
    private static async Task<(byte[] Audio, List<SubtitleItem> Subtitles)> SynthesizeSliceAsync(
        string ssml, bool withMetadata, CancellationToken ct)
    {
        // 请求开始 → 推迟空闲关闭：上个请求遗留的 10s 倒计时若到期会关掉正在服务本请求的连接
        // （对齐 vue 的 clearTimeout 行为；否则"转换完成 → 等几秒 → 再转换"必踩竞态）
        Interlocked.Increment(ref _idleGeneration);

        // X-RequestId 必须是 no-dash UUID（服务端校验格式，不符直接 ProtocolError 关闭连接）
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<(byte[] Audio, List<SubtitleItem> Subtitles)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(tcs);

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ws = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            lock (PendingLock) Pending[requestId] = pending;

            var ts = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);
            // wordBoundary 也开：句级文本（含标点、权威）+ 词级时间（精确）→ 组装逐行字幕
            var metadataOptions = withMetadata
                ? "{\"sentenceBoundaryEnabled\":\"true\",\"wordBoundaryEnabled\":\"true\"}"
                : "{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}";
            var config = SpeechConfigHeader.Replace("{ts}", ts) +
                "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":" + metadataOptions +
                ",\"outputFormat\":\"" + OutputFormat + "\"}}}}";
            await ws.SendAsync(Encoding.UTF8.GetBytes(config), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

            var msg = "X-Timestamp:" + ts + "\r\nX-RequestId:" + requestId +
                      "\r\nContent-Type:application/ssml+xml\r\nPath:ssml\r\n\r\n" + ssml;
            await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20)); // 单请求超时
        using (timeout.Token.Register(() =>
        {
            lock (PendingLock)
                if (Pending.Remove(requestId))
                    tcs.TrySetException(new TimeoutException("Edge TTS 请求超时"));
        }))
        {
            var result = await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            ScheduleIdleClose();
            return result;
        }
    }

    /// <summary>
    /// 切片级重试：5 次，指数退避 2s×attempt（2/4/6/8s，总 ~20s）。
    /// 服务端间歇性拒绝/限流是免费端点的常态（实测故障窗口可达 30s+），
    /// 3 次×1s 档扛不住分钟级故障；取消直接抛。
    /// </summary>
    private static async Task<(byte[] Audio, List<SubtitleItem> Subtitles)> SynthesizeSliceWithRetryAsync(
        string ssml, bool withMetadata, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                return await SynthesizeSliceAsync(ssml, withMetadata, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                Log.Warning(ex, "[EdgeTts] 第 {Attempt} 次尝试失败", attempt);
            }
            if (attempt < 5) await Task.Delay(2000 * attempt, ct).ConfigureAwait(false);
        }
        throw new IOException("Edge TTS 转换失败（已重试 5 次）—— Edge 免费配音服务偶发不可用，请稍等 1-2 分钟重试", last);
    }

    /// <summary>接收循环：按 EndOfMessage 聚合完整帧，分派文本/二进制。</summary>
    private static async Task ReceiveLoopAsync(ClientWebSocket ws)
    {
        var buffer = new byte[65536];
        Exception? failure = null;
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None)
                                    .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var data = ms.ToArray();
                if (result.MessageType == WebSocketMessageType.Text)
                    HandleTextFrame(Encoding.UTF8.GetString(data));
                else
                    HandleBinaryFrame(data);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // 服务端在 turn.end 后会主动断开（TCP RST / 10054 是常态，非错误）——
            // 不打堆栈不设 failure（请求已由 turn.end 完成，这里只是收尾）
            if (ex is WebSocketException { WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely } ||
                ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
            {
                Log.Debug("[EdgeTts] 接收循环退出（服务端 turn.end 后主动断开，正常）");
            }
            else
            {
                failure = ex;
                Log.Debug(ex, "[EdgeTts] 接收循环退出（连接被服务端关闭）");
            }
        }
        finally
        {
            // 连接关闭 → 所有未完成请求失败（服务端主动关也不会挂住 UI）
            List<PendingRequest> failed;
            lock (PendingLock) { failed = [.. Pending.Values]; Pending.Clear(); }
            if (failed.Count > 0)
                Log.Warning("[EdgeTts] 连接关闭，{Count} 个请求未完成", failed.Count);
            foreach (var p in failed)
                p.Tcs.TrySetException(failure ?? new IOException("Edge TTS 连接已关闭"));

            try { ws.Dispose(); } catch { }
            lock (ConnLock)
                if (ReferenceEquals(_ws, ws)) { _ws = null; _receiveAlive = false; } // 下次请求惰性重连
        }
    }

    private static void HandleTextFrame(string text)
    {
        var id = ExtractRequestId(text);
        if (id == null) return;

        if (text.Contains("Path:audio.metadata", StringComparison.Ordinal))
        {
            var jsonStart = text.IndexOf('{');
            if (jsonStart >= 0) ParseMetadataAndAppend(text[jsonStart..], id);
            return;
        }

        if (text.Contains("Path:turn.end", StringComparison.Ordinal))
        {
            PendingRequest? p = null;
            lock (PendingLock)
            {
                if (Pending.Remove(id, out p))
                {
                    // 顺带清掉指向同一 pending 的残留 entry（Path:response 用 streamId 注册过双 ID；
                    // 只移 requestId 会让 streamId 残留，空闲关连接时误报"请求未完成"）
                    foreach (var key in Pending.Where(kv => ReferenceEquals(kv.Value, p)).Select(kv => kv.Key).ToList())
                        Pending.Remove(key);
                }
            }
            if (p != null)
            {
                // 音频在帧缓冲里；字幕在最后组装（词/句都在，此时数据齐了）
                p.Tcs.TrySetResult((p.Audio.ToArray(), SubtitleLineBuilder.Build(p.Sentences, p.Words)));
            }
        }
        else if (text.Contains("Path:response", StringComparison.Ordinal))
        {
            // 正常响应：服务端返回音频元数据（含 streamId）。注意服务端行为不统一 ——
            // 有时二进制帧头用 X-RequestId（小写），有时用 streamId（大写）；
            // 把 streamId 也注册进 Pending，两种 ID 都能命中缓冲（见 HandleBinaryFrame）。
            lock (PendingLock)
            {
                if (Pending.TryGetValue(id, out var p))
                {
                    var m = Regex.Match(text, @"""streamId"":""(?<sid>[0-9A-Fa-f]+)""");
                    if (m.Success)
                        Pending.TryAdd(m.Groups["sid"].Value.ToLowerInvariant(), p);
                }
            }
        }
        // Path:turn.start 无需处理 — 音频缓冲在发送时已建
    }

    /// <summary>二进制帧：前 2 字节为头长度前缀（uint16 BE），跳过后再搜 "Path:audio\r\n"。</summary>
    private static void HandleBinaryFrame(byte[] data)
    {
        var marker = "Path:audio\r\n"u8;
        var span = data.AsSpan(2);
        var idx = span.IndexOf(marker);
        if (idx < 0) return;

        var id = ExtractRequestId(Encoding.UTF8.GetString(data, 2, idx));
        if (id == null) return;

        var start = 2 + idx + marker.Length;
        lock (PendingLock)
        {
            if (Pending.TryGetValue(id, out var p))
                p.Audio.Write(data, start, data.Length - start);
            else
                Log.Warning("[EdgeTts] 音频帧 ID 未匹配(服务端可能用了 streamId): {Id}", id);
        }
    }

    private static string? ExtractRequestId(string text)
    {
        // 大小写不敏感：服务端可能用大写 streamId（如 89FFEF57...）而非小写 requestId
        var m = Regex.Match(text, @"X-RequestId:(?<id>[0-9a-fA-F]+)");
        return m.Success ? m.Groups["id"].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// 解析 audio.metadata 帧，追加到对应请求的 Sentences/Words 列表。
    /// JSON: {"Metadata":[{"Type":"SentenceBoundary"|"WordBoundary",
    ///        "Data":{"Offset":1000000,"Duration":3375000,"text":{"Text":"...","Length":2}}}]}
    /// Offset/Duration 单位 100ns。句文本含标点（权威），词文本不含标点/空白。
    /// 解析失败只记日志（metadata 是增强，不影响合成）。
    /// </summary>
    private static void ParseMetadataAndAppend(string json, string requestId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Metadata", out var meta)) return;

            PendingRequest? pending;
            lock (PendingLock)
                if (!Pending.TryGetValue(requestId, out pending!)) return;

            foreach (var item in meta.EnumerateArray())
            {
                if (!item.TryGetProperty("Type", out var type)) continue;
                var kind = type.GetString();
                if (kind != "SentenceBoundary" && kind != "WordBoundary") continue;
                if (!item.TryGetProperty("Data", out var data)) continue;
                if (!data.TryGetProperty("Offset", out var offsetEl) ||
                    !data.TryGetProperty("Duration", out var durEl)) continue;
                if (!data.TryGetProperty("text", out var textEl) ||
                    !textEl.TryGetProperty("Text", out var textProp)) continue;

                var startMs = (int)(offsetEl.GetInt64() / 10_000);           // 100ns → ms
                var endMs = (int)((offsetEl.GetInt64() + durEl.GetInt64()) / 10_000);
                var text = textProp.GetString() ?? "";
                if (text.Length == 0 || endMs <= startMs) continue;

                lock (PendingLock)
                {
                    var item2 = new SubtitleItem(text, startMs, endMs);
                    if (kind == "SentenceBoundary") pending.Sentences.Add(item2);
                    else pending.Words.Add(item2);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EdgeTts] metadata 解析失败（字幕降级）: {Json}", json[..Math.Min(json.Length, 200)]);
        }
    }

    /// <summary>空闲 10s 后主动断开（代数守卫：期间有新请求则取消关闭）。</summary>
    private static async void ScheduleIdleClose()
    {
        var gen = Interlocked.Increment(ref _idleGeneration);
        await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (Volatile.Read(ref _idleGeneration) != gen) return;

        ClientWebSocket? ws;
        lock (ConnLock) { ws = _ws; }
        if (ws is { State: WebSocketState.Open })
        {
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "idle", CancellationToken.None)
                          .ConfigureAwait(false);
            }
            catch { /* 接收循环的 finally 负责清理 */ }
        }
    }

    /// <summary>应用退出时关闭连接（可选，由 MainWindow.OnClosed 调用）。</summary>
    public static void Shutdown()
    {
        ClientWebSocket? ws;
        lock (ConnLock) { ws = _ws; _ws = null; _receiveAlive = false; }
        if (ws == null) return;
        try { ws.Dispose(); } catch { }
    }
}
