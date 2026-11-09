using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 视频元数据探测 —— ffprobe 读时长/宽高、ffmpeg 抽首帧当缩略图。
///
/// 这是设计文档 §4.1「全自动」层的一部分：用户看不到，导入时后台跑，卡片拿它显示
/// `03:24 · 48.2 MB`。老版没有本地探测（时长靠 VideoItem 上的字符串，不选视频就
/// 拿不到 —— 这正是小红书 ≥10 秒规则只能做软提示的原因，见缺陷 L12）。
///
/// 三重缓存（设计文档 §3.2）：内存 → 磁盘 JSON（按 路径+大小+修改时间 指纹）→ 实探。
/// 缩略图落在 <c>thumbs/{sha1}.jpg</c>，随元数据缓存一起被 LRU 淘汰。
/// </summary>
public sealed class VideoMetaProbe
{
    /// <summary>缩略图宽度（卡片显示尺寸的 2 倍，够视网膜屏；再大只是浪费磁盘）</summary>
    private const int ThumbWidth = 320;

    /// <summary>
    /// 发布封面帧的宽度上限：1080p 片源原样、4K 缩到 1920。
    /// 封面要过平台审核并参与列表展示，320px 卡片缩略图太糊；但也没必要把 4K 原帧
    /// 整张传上去（每平台一次上传，体积就是时间）。
    /// </summary>
    private const int CoverMaxWidth = 1920;

    /// <summary>
    /// 封面帧缓存桶数量上限。封面不进元数据 LRU（<see cref="CachedMeta"/> 只记缩略图），
    /// 超出按最后写入时间删最旧的 —— 否则 thumbs/ 会随导入视频数无限长。
    /// </summary>
    private const int CoverCacheLimit = 200;

    /// <summary>封面帧缓存版本 —— 改抽帧参数（宽度/质量/时间点口径）必须 bump，否则旧文件让改动「看不见」</summary>
    private const string CoverFrameVersion = "v1";

    /// <summary>
    /// 「封面帧」留空时的抽帧时间点（毫秒）。视频第 0 秒常见黑场/纯色，
    /// 半秒处才有辨识度 —— 与卡片缩略图同口径（<see cref="ExtractThumbAsync"/> 的 <c>-ss 0.5</c>）。
    /// </summary>
    public const long DefaultCoverFrameMs = 500;

    /// <summary>ffprobe 单次超时 —— 探测是后台非关键路径，卡住不能拖着导入流程</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly string _cacheFile;
    private readonly string _thumbDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CachedMeta> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public VideoMetaProbe(string? cacheRoot = null)
    {
        var root = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CreatFlow");
        _cacheFile = Path.Combine(root, "video-meta-cache.json");
        _thumbDir = Path.Combine(root, "thumbs");
    }

    /// <summary>
    /// 探测一条视频。命中缓存直接返回（毫秒级）；未命中才起 ffprobe/ffmpeg。
    /// 探测失败返回 null —— 调用方按「信息未知」继续，**不要**因此拦住用户。
    /// </summary>
    public async Task<VideoMeta?> ProbeAsync(string videoPath, CancellationToken ct = default)
    {
        FileInfo fi;
        try
        {
            fi = new FileInfo(videoPath);
            if (!fi.Exists) return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Probe] 读文件信息失败 {Path}", videoPath);
            return null;
        }

        var key = CacheKey(videoPath, fi);
        CachedMeta? hit = null;
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            if (_cache.TryGetValue(key, out var cached)
                && (cached.ThumbPath is null || File.Exists(cached.ThumbPath)))
            {
                hit = cached;
            }
        }
        finally { _gate.Release(); }

        if (hit is not null)
        {
            if (hit.HasAudio is not null) return hit.ToMeta(videoPath, fi.Length);

            // 音轨是后加的字段：老缓存条目只补探这一步（一次 ffprobe，几十毫秒），
            // 不让用户等整轮重探。探不出来就按未知继续（下次再试）。
            var audio = await ProbeHasAudioAsync(videoPath, ct);
            if (audio is null) return hit.ToMeta(videoPath, fi.Length);

            await _gate.WaitAsync(ct);
            try
            {
                hit.HasAudio = audio;
                await SaveAsync(ct);
            }
            finally { _gate.Release(); }
            return hit.ToMeta(videoPath, fi.Length);
        }

        var probed = await ProbeCoreAsync(videoPath, fi, ct);
        if (probed is null) return null;

        await _gate.WaitAsync(ct);
        try
        {
            _cache[key] = CachedMeta.From(probed);
            await SaveAsync(ct);
        }
        finally { _gate.Release(); }

        return probed;
    }

    /// <summary>批量探测（导入一个文件夹后跑）—— 串行，避免同时起 N 个 ffprobe 抢 CPU。</summary>
    public async Task ProbeAllAsync(IEnumerable<string> paths,
        Action<string, VideoMeta?>? onEach = null, CancellationToken ct = default)
    {
        foreach (var p in paths)
        {
            if (ct.IsCancellationRequested) return;
            VideoMeta? meta = null;
            try { meta = await ProbeAsync(p, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Warning(ex, "[Probe] 探测失败 {Path}", p); }
            onEach?.Invoke(p, meta);
        }
    }

    // ═══ 实探 ═══

    private async Task<VideoMeta?> ProbeCoreAsync(string videoPath, FileInfo fi, CancellationToken ct)
    {
        double duration = 0;
        int width = 0, height = 0;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            var (stdout, stderr, code) = await FfmpegCli.RunProbeAsync(
            [
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "format=duration:stream=width,height,duration",
                "-print_format", "json",
                videoPath,
            ], cts.Token);

            if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;

                if (root.TryGetProperty("streams", out var streams)
                    && streams.ValueKind == JsonValueKind.Array
                    && streams.GetArrayLength() > 0)
                {
                    var s = streams[0];
                    width = ReadInt(s, "width");
                    height = ReadInt(s, "height");
                    // 流级 duration 可能缺（部分 mp4），缺了退到 format.duration
                    duration = ReadDouble(s, "duration");
                }
                if (duration <= 0 && root.TryGetProperty("format", out var fmt))
                    duration = ReadDouble(fmt, "duration");
            }
            else if (code != 0)
            {
                Log.Warning("[Probe] ffprobe 退出码 {Code}: {Err}", code,
                    stderr.Length > 200 ? stderr[..200] : stderr);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warning("[Probe] ffprobe 超时 {Path}", videoPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Probe] ffprobe 异常 {Path}（{Hint}）", videoPath,
                FfmpegCli.IsBundled ? "已加载绿色版" : "未找到 runtime/ffmpeg，依赖 PATH");
        }

        var thumb = await ExtractThumbAsync(videoPath, fi, ct);
        var hasAudio = await ProbeHasAudioAsync(videoPath, ct);
        return new VideoMeta(videoPath, fi.Length, duration, width, height, thumb)
        {
            HasAudio = hasAudio,
        };
    }

    /// <summary>
    /// 有没有音轨。返回 <c>null</c> = 探测失败（读不了 / ffprobe 起不来）—— 按未知处理，不影响导入。
    /// 单独起一次 ffprobe（<c>-select_streams a:0</c>）：与视频流探测解耦，
    /// 不改动上面 <c>streams[0]</c> 的既有语义（音轨在前/纯音频异常文件都不会串）。
    /// </summary>
    private static async Task<bool?> ProbeHasAudioAsync(string videoPath, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            var (stdout, _, code) = await FfmpegCli.RunProbeAsync(
            [
                "-v", "error",
                "-select_streams", "a:0",
                "-show_entries", "stream=codec_type",
                "-print_format", "json",
                videoPath,
            ], cts.Token);

            if (code != 0 || string.IsNullOrWhiteSpace(stdout)) return null;

            using var doc = JsonDocument.Parse(stdout);
            return doc.RootElement.TryGetProperty("streams", out var streams)
                && streams.ValueKind == JsonValueKind.Array
                && streams.GetArrayLength() > 0;
        }
        catch (OperationCanceledException)
        {
            return null;   // 超时 / 用户取消 —— 音轨只是提示用，不抛
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Probe] 音轨探测失败 {Path}", videoPath);
            return null;
        }
    }

    /// <summary>
    /// 抽首帧当缩略图。失败返回 null —— 卡片退化成纯色块，不阻断任何流程。
    /// <c>-ss 0.5</c> 而非 0：不少视频第一帧是黑场/纯色，半秒处的画面才有辨识度。
    /// </summary>
    private async Task<string?> ExtractThumbAsync(string videoPath, FileInfo fi, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_thumbDir);
            var thumbPath = Path.Combine(_thumbDir, ThumbName(videoPath, fi));
            if (File.Exists(thumbPath) && new FileInfo(thumbPath).Length > 0) return thumbPath;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            var (_, stderr, code) = await FfmpegCli.RunAsync(
            [
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", "0.5", "-i", videoPath,
                "-frames:v", "1",
                "-vf", $"scale={ThumbWidth}:-2",
                "-q:v", "4",
                thumbPath,
            ], cts.Token);

            if (code == 0 && File.Exists(thumbPath) && new FileInfo(thumbPath).Length > 0)
                return thumbPath;

            Log.Debug("[Probe] 抽首帧失败 {Path}: {Err}", videoPath,
                stderr.Length > 200 ? stderr[..200] : stderr);
            try { if (File.Exists(thumbPath)) File.Delete(thumbPath); } catch { }
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Probe] 抽首帧异常 {Path}", videoPath);
            return null;
        }
    }

    // ═══ 发布封面帧（2.5.7） ═══

    /// <summary>
    /// 抽一帧当**发布封面**（不是 320px 卡片缩略图 —— 按 <see cref="CoverMaxWidth"/> 出图）。
    ///
    /// <paramref name="frameMs"/>：null = 没填（<see cref="DefaultCoverFrameMs"/> 0.5 秒），
    /// 0 = 明确第 0 秒，正值 = 用户指定的时间点。
    ///
    /// 结果按 [视频指纹 + 时间点 + 版本] 缓存在 <c>thumbs/cover_{sha1}.jpg</c>：
    /// 同一条视频的多平台发布只抽一次；调用方**不要删**（缓存资产）。
    /// 指定时间点取不到帧（超出时长 / seek 失败）会退回默认 0.5 秒再试一次 ——
    /// 宁可封面是半秒那帧，也不要「必填封面」的平台整条发不出去。
    /// 失败返回 null（缺 ffmpeg / 视频读不了），永不抛（除用户取消）。
    /// </summary>
    public async Task<string?> ExtractCoverFrameAsync(
        string videoPath, long? frameMs = null, CancellationToken ct = default)
    {
        FileInfo fi;
        try
        {
            fi = new FileInfo(videoPath);
            if (!fi.Exists) return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Probe] 抽封面帧读文件信息失败 {Path}", videoPath);
            return null;
        }

        var requested = frameMs ?? DefaultCoverFrameMs;
        var path = await ExtractCoverFrameCoreAsync(videoPath, fi, requested, ct);
        if (path is null && requested != DefaultCoverFrameMs)
        {
            Log.Warning(
                "[Probe] 封面帧 {Sec:0.###}s 取不到，退回默认 {Default:0.###}s：{Path}",
                requested / 1000.0, DefaultCoverFrameMs / 1000.0, videoPath);
            path = await ExtractCoverFrameCoreAsync(videoPath, fi, DefaultCoverFrameMs, ct);
        }
        return path;
    }

    private async Task<string?> ExtractCoverFrameCoreAsync(
        string videoPath, FileInfo fi, long frameMs, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_thumbDir);
            var path = Path.Combine(_thumbDir, CoverName(videoPath, fi, frameMs));
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            // 先写唯一临时名再原子改名：同一视频的两个平台并发抽帧时，谁先完成谁落地，
            // 不会出现「一个 ffmpeg 正写到一半、另一个把半张图当成品」
            var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp.jpg";
            try
            {
                var seconds = (frameMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
                var (_, stderr, code) = await FfmpegCli.RunAsync(
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    // -ss 在 -i 之前：现代 ffmpeg 会先 seek 再精确解码，长视频不用从头读
                    "-ss", seconds, "-i", videoPath,
                    "-frames:v", "1",
                    // 上限 1920、小片源不放大；-2 保证高度是偶数（JPEG 编码要求）
                    "-vf", $"scale='min({CoverMaxWidth},iw)':-2",
                    "-q:v", "3",
                    temp,
                ], cts.Token);

                if (code == 0 && File.Exists(temp) && new FileInfo(temp).Length > 0)
                {
                    try { File.Move(temp, path, overwrite: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 并发实例已落地同一文件 / 目标正被读取 —— 下面统一核最终文件
                    }
                }
                else
                {
                    Log.Debug("[Probe] 抽封面帧失败 {Path} @{Sec}s: {Err}", videoPath, seconds,
                        stderr.Length > 200 ? stderr[..200] : stderr);
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            }

            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                PruneCoverCache();
                return path;
            }
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 用户暂停/取消 —— 不是「抽帧失败」，别吞成 null 让队列当成发完了
        }
        catch (OperationCanceledException)
        {
            Log.Warning("[Probe] 抽封面帧超时 {Path}", videoPath);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Probe] 抽封面帧异常 {Path}", videoPath);
            return null;
        }
    }

    /// <summary>封面帧缓存名 —— 视频指纹 + 时间点 + 版本号（与缩略图分前缀，互不覆盖）</summary>
    private static string CoverName(string path, FileInfo fi, long frameMs)
    {
        var raw = $"{CacheKey(path, fi)}|cover|{frameMs}|{CoverFrameVersion}";
        return "cover_" + Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(raw))) + ".jpg";
    }

    /// <summary>封面帧缓存超上限就删最旧的（枚举 200 个文件，抽帧时才跑，成本可忽略）</summary>
    private void PruneCoverCache()
    {
        try
        {
            var stale = new DirectoryInfo(_thumbDir)
                .GetFiles("cover_*.jpg")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(CoverCacheLimit)
                .ToList();
            foreach (var f in stale)
            {
                try { f.Delete(); } catch { /* 被占用 —— 下次再删 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Probe] 封面帧缓存清理失败（不影响本次抽帧）");
        }
    }

    // ═══ 缓存 ═══

    /// <summary>
    /// 指纹 = 路径 + 文件大小 + 最后写入时间。文件被重新导出（同名不同内容）时
    /// 大小/时间必变，缓存自动失效 —— 不用哈希整个文件（几十 MB 太贵）。
    /// </summary>
    private static string CacheKey(string path, FileInfo fi) =>
        $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";

    private static string ThumbName(string path, FileInfo fi)
    {
        var raw = CacheKey(path, fi);
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(raw))) + ".jpg";
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_cacheFile)) return;
            var json = await File.ReadAllTextAsync(_cacheFile, ct);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, CachedMeta>>(json, JsonOpts);
            if (loaded is null) return;
            foreach (var (k, v) in loaded) _cache[k] = v;
            Log.Debug("[Probe] 元数据缓存载入 {Count} 条", _cache.Count);
        }
        catch (Exception ex)
        {
            // 缓存坏了不该影响功能 —— 丢掉重建
            Log.Warning(ex, "[Probe] 元数据缓存读取失败，按空缓存继续");
            _cache.Clear();
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            // LRU 上限 500（设计文档 §3.2）—— 超了按最后访问时间淘汰，
            // 同时删掉对应缩略图，避免 thumbs 目录无限增长
            if (_cache.Count > 500)
            {
                var drop = _cache.OrderBy(kv => kv.Value.At).Take(_cache.Count - 500).ToList();
                foreach (var (k, v) in drop)
                {
                    _cache.Remove(k);
                    if (v.ThumbPath is not null)
                        try { if (File.Exists(v.ThumbPath)) File.Delete(v.ThumbPath); } catch { }
                }
            }

            var dir = Path.GetDirectoryName(_cacheFile);
            if (dir is not null) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_cacheFile,
                JsonSerializer.Serialize(_cache, JsonOpts), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Probe] 元数据缓存写入失败（不影响本次探测结果）");
        }
    }

    private static int ReadInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static double ReadDouble(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d
            : el.TryGetProperty(name, out var n) && n.TryGetDouble(out var d2) ? d2 : 0;

    /// <summary>磁盘缓存条目（只存探测出来的事实，路径由 key 携带）</summary>
    private sealed class CachedMeta
    {
        public long Size { get; set; }
        public double Duration { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? ThumbPath { get; set; }
        /// <summary>是否有音轨；null = 老缓存（加该字段之前写的）或探测失败</summary>
        public bool? HasAudio { get; set; }
        /// <summary>最后访问时间（LRU 淘汰依据）</summary>
        public DateTime At { get; set; }

        public static CachedMeta From(VideoMeta m) => new()
        {
            Size = m.SizeBytes,
            Duration = m.DurationS,
            Width = m.Width,
            Height = m.Height,
            ThumbPath = m.ThumbPath,
            HasAudio = m.HasAudio,
            At = DateTime.UtcNow,
        };

        public VideoMeta ToMeta(string path, long size)
        {
            At = DateTime.UtcNow;
            return new VideoMeta(path, size, Duration, Width, Height, ThumbPath)
            {
                HasAudio = HasAudio,
            };
        }
    }
}
