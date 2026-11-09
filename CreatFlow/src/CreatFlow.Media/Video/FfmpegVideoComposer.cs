using System.Diagnostics;
using System.Globalization;

namespace CreatFlow.Media.Video;

/// <summary>
/// FFmpeg 视频合成 — 图片轮播（Ken Burns 缩放动效 + 随机转场）+ 配音 + 字幕 → mp4。
/// 全部本地执行（用户素材不过服务器）；LGPL 构建约束 → 无 libx264，
/// 主编码器 h264_mf（Windows Media Foundation 硬件 H.264），失败自动兜底 mpeg4（软件）。
///
/// 依赖: runtime/ffmpeg/ffmpeg.exe + ffprobe.exe（scripts/download_ffmpeg.ps1 获取，不进 git）。
/// 进度回调在后台线程触发，调用方负责 Dispatcher.UIThread.Post 编组（对标 TtsViewModel）。
///
/// 契约说明: 静态类（house style，无 DI），签名与 IVideoComposer 一致；
/// 未来出现第二实现（如硬件加速管线）时按 IVideoComposer 改造为实例类。
/// </summary>
public static class FfmpegVideoComposer
{
    private static readonly string? BundledFfmpeg = FindBundledTool("ffmpeg.exe");
    private static readonly string? BundledFfprobe = FindBundledTool("ffprobe.exe");

    private static string FfmpegExe => BundledFfmpeg ?? "ffmpeg";   // PATH 兜底
    private static string FfprobeExe => BundledFfprobe ?? "ffprobe";

    private static string? FindBundledTool(string exe)
    {
        var candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "ffmpeg", exe);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    /// <summary>探测媒体时长（秒）。ffprobe 优先，缺失时解析 ffmpeg -i 的 stderr。</summary>
    public static async Task<double> ProbeDurationAsync(string mediaPath, CancellationToken ct = default)
    {
        if (BundledFfprobe != null)
        {
            var args = new List<string>
            {
                "-v", "error", "-show_entries", "format=duration", "-of", "json", mediaPath,
            };
            var (stdout, stderr, exitCode) = await RunAsync(FfprobeExe, args, null, ct).ConfigureAwait(false);
            if (exitCode == 0)
            {
                using var doc = JsonDocument.Parse(stdout);
                if (doc.RootElement.TryGetProperty("format", out var fmt) &&
                    fmt.TryGetProperty("duration", out var dur))
                {
                    // ffprobe 的 duration 是字符串（"2.304000"）；先查 ValueKind 再取值
                    // （TryGetDouble 对 String 类型会抛异常，不是返回 false）
                    if (dur.ValueKind == JsonValueKind.Number && dur.TryGetDouble(out var seconds)) return seconds;
                    if (dur.ValueKind == JsonValueKind.String &&
                        double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                        return seconds;
                }
            }
            Log.Warning("[Ffmpeg] ffprobe 失败(exit={Exit})，改用 ffmpeg -i 解析: {Err}",
                exitCode, stderr[..Math.Min(stderr.Length, 300)]);
        }

        // 兜底: ffmpeg -i file 的 stderr 输出 "Duration: 00:00:12.34, ..."
        var args2 = new[] { "-hide_banner", "-i", mediaPath };
        var (_, stderr2, _) = await RunAsync(FfmpegExe, args2, null, ct).ConfigureAwait(false);
        var m = Regex.Match(stderr2, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!m.Success) throw new IOException("媒体时长探测失败: " + stderr2[..Math.Min(stderr2.Length, 200)]);
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
               int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
               double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>合成视频：图片/视频混剪 + 配音 + 可选字幕/标题 → mp4。返回输出路径。</summary>
    public static async Task<string> ComposeAsync(
        ComposeRequest request,
        Action<string, int>? onProgress = null,
        CancellationToken ct = default)
    {
        // ── 校验 ──
        if (request.Items == null || request.Items.Count == 0)
            throw new ArgumentException("至少需要一个素材", nameof(request));
        foreach (var item in request.Items)
            if (!File.Exists(item.Path)) throw new ArgumentException($"素材不存在: {item.Path}", nameof(request));
        if (request.Shots == null && !File.Exists(request.AudioPath))
            throw new ArgumentException($"配音文件不存在: {request.AudioPath}", nameof(request));
        if (request.Shots is { Count: > 0 } && request.Shots.Count != request.Items.Count)
            throw new ArgumentException($"分镜数量({request.Shots.Count})与素材数量({request.Items.Count})不一致", nameof(request));
        var hasBgm = !string.IsNullOrWhiteSpace(request.BgmPath);
        if (hasBgm && !File.Exists(request.BgmPath!))
            throw new ArgumentException($"背景音乐不存在: {request.BgmPath}", nameof(request));

        var outDir = Path.GetDirectoryName(request.OutputPath);
        if (string.IsNullOrEmpty(outDir)) throw new ArgumentException("输出路径无效", nameof(request));
        Directory.CreateDirectory(outDir);

        onProgress?.Invoke("probing", 2);
        double audioDur;
        if (request.Shots == null)
        {
            audioDur = await ProbeDurationAsync(request.AudioPath, ct).ConfigureAwait(false);
            if (audioDur <= 0) throw new IOException("音频时长探测失败");
        }
        else
        {
            audioDur = 0; // 分镜模式在时长分配分支内确定（逐镜 concat 后 probe）
        }
        var w = request.Width % 2 == 0 ? request.Width : request.Width - 1;   // 编码器要求偶数
        var h = request.Height % 2 == 0 ? request.Height : request.Height - 1;

        var workDir = Path.Combine(Path.GetTempPath(), "CreatFlow", "ffmpeg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            // ── 时长分配 ──
            //   整段模式：图片固定 3s；视频完整展示（10s/20s 素材不截断 — D7）
            //   分镜模式：图片镜 = 该句配音时长（自动伸缩）；视频镜 = max(视频完整时长, 句时长)（句长则定格）
            const double imageDurSec = 3.0;
            var n = request.Items.Count;
            int[] frames;
            double[]? perShotTpad = null;
            double tpadSeconds = 0;
            bool padAudio;
            string audioPath = request.AudioPath;
            IReadOnlyList<SubtitleItem>? subsForAss = request.Subtitles;

            if (request.Shots != null)
            {
                // ══ 分镜模式 ══
                for (var i = 0; i < n; i++)
                    if (!File.Exists(request.Shots[i].AudioPath))
                        throw new ArgumentException($"分镜 {i + 1} 配音文件不存在: {request.Shots[i].AudioPath}", nameof(request));

                onProgress?.Invoke("probing", 3);
                // ① 逐镜时长：图片镜 = 句音频时长；视频镜 = max(视频完整, 句时长)
                frames = new int[n];
                perShotTpad = new double[n];
                for (var i = 0; i < n; i++)
                {
                    var shotSec = await ProbeDurationAsync(request.Shots[i].AudioPath, ct).ConfigureAwait(false);
                    if (shotSec <= 0) throw new IOException($"分镜 {i + 1} 音频时长探测失败");
                    if (request.Items[i].Kind == MediaKind.Image)
                        frames[i] = Math.Max(1, (int)Math.Round(shotSec * 25.0));
                    else
                    {
                        var videoSec = await ProbeDurationAsync(request.Items[i].Path, ct).ConfigureAwait(false);
                        var frameSec = Math.Max(videoSec, shotSec);
                        frames[i] = Math.Max(1, (int)Math.Round(frameSec * 25.0));
                        perShotTpad[i] = Math.Max(0, shotSec - videoSec);
                    }
                }

                // ② 音频拼接：每镜先补齐/裁齐到该镜画面时长，再 concat 成 merged.wav。
                //    ⚠️ 不能直接 concat 原始 mp3：视频镜句短于素材时（3 秒素材 / 2 秒配音）画面多出 1 秒，
                //    直接拼接的音频不会补这段 → 配音逐镜越走越早、片尾留一大段静音
                //    （2026-10-22 狗狗单元实测：20 短句镜片尾静音 16 秒）。
                audioPath = Path.Combine(workDir, "merged.wav");
                var audioGraph = new StringBuilder();
                for (var i = 0; i < n; i++)
                {
                    var shotDur = (frames[i] / 25.0).ToString("0.###", CultureInfo.InvariantCulture);
                    if (i > 0) audioGraph.Append(';');
                    audioGraph.Append(CultureInfo.InvariantCulture,
                        $"[{i}:a]atrim=0:{shotDur},asetpts=PTS-STARTPTS,aresample=44100,aformat=channel_layouts=mono," +
                        $"apad=whole_dur={shotDur}[a{i}]");
                }
                audioGraph.Append(';');
                for (var i = 0; i < n; i++) audioGraph.Append(CultureInfo.InvariantCulture, $"[a{i}]");
                audioGraph.Append(CultureInfo.InvariantCulture, $"concat=n={n}:v=0:a=1[outa]");
                var concatArgs = new List<string> { "-hide_banner", "-y" };
                foreach (var s in request.Shots) { concatArgs.Add("-i"); concatArgs.Add(s.AudioPath); }
                concatArgs.AddRange(["-filter_complex", audioGraph.ToString(), "-map", "[outa]", "-c:a", "pcm_s16le", audioPath]);
                var (_, concatErr, concatCode) = await RunAsync(FfmpegExe, [.. concatArgs], workDir, ct).ConfigureAwait(false);
                if (concatCode != 0 || !File.Exists(audioPath))
                    throw new IOException("配音拼接失败: " + concatErr[..Math.Min(concatErr.Length, 300)]);

                // ③ 字幕平移合并（每镜偏移 = 前镜画面时长 —— 与 ② 补齐后的音频严格一致）
                if (request.Shots.Any(s => s.Subtitles is { Count: > 0 }))
                {
                    var merged = new List<SubtitleItem>();
                    var offsetMs = 0;
                    for (var i = 0; i < n; i++)
                    {
                        if (request.Shots[i].Subtitles != null)
                            foreach (var s in request.Shots[i].Subtitles)
                                merged.Add(new SubtitleItem(s.Text, s.StartMs + offsetMs, s.EndMs + offsetMs));
                        offsetMs += (int)Math.Round(frames[i] / 25.0 * 1000);
                    }
                    subsForAss = merged;
                }

                // ④ 拼接后总时长；画面 > 音频 → 静音补齐（25fps 取整的零星残差）
                audioDur = await ProbeDurationAsync(audioPath, ct).ConfigureAwait(false);
                var shotMediaFrames = frames.Sum();
                var shotAudioFrames = (int)Math.Round(audioDur * 25.0);
                padAudio = shotMediaFrames > shotAudioFrames;
            }
            else
            {
                // ══ 整段模式（v1 行为，仅视频改为完整展示） ══
                frames = new int[n];
                for (var i = 0; i < n; i++)
                {
                    var item = request.Items[i];
                    var clipSec = item.Kind == MediaKind.Image
                        ? imageDurSec
                        : await ProbeDurationAsync(item.Path, ct).ConfigureAwait(false); // 完整展示，不截断
                    frames[i] = Math.Max(1, (int)Math.Round(clipSec * 25.0));
                }

                //   音频长于素材 → 末素材吸收余量（图片自然补时/视频定格最后一帧）
                //   素材长于音频 → 音频补静音（-af apad + -shortest 截到视频长）
                var mediaFrames = frames.Sum();
                var audioFrames = (int)Math.Round(audioDur * 25.0);
                var diff = audioFrames - mediaFrames;
                if (diff > 0)
                {
                    if (request.Items[^1].Kind == MediaKind.Image)
                        frames[^1] += diff;          // 最后图片吸收余量（画面停在图上）
                    else
                        tpadSeconds = diff / 25.0;   // 最后视频定格最后一帧
                }
                padAudio = diff < 0;                 // 素材比配音长 → 静音补齐
            }

            // 转场：相邻素材间 xfade 随机过渡（2026-10-11 用户反馈「固定几个特效会审美疲劳」）。
            // 重叠帧摊进图片素材补回来，保证成片总时长与配音严格对齐（纯视频素材没有可拉长的段落 → 不启用）
            var overlapFrames = SpreadTransitionOverlap(frames, [.. request.Items.Select(i => i.Kind)]);

            // 进度基准：画面/音频较长者（视频完整展示时音频先结束，按画面算进度）；
            // 有转场时把重叠扣回来 —— 成片真实时长 = Σ帧 −（n−1)×重叠
            var progressDurSec = Math.Max(
                frames.Sum() - (request.Items.Count - 1) * overlapFrames,
                (int)Math.Round(audioDur * 25.0)) / 25.0;

            var subsAss = Path.Combine(workDir, "subs.ass");
            var hasSubtitles = subsForAss is { Count: > 0 };
            if (hasSubtitles) File.WriteAllText(subsAss, BuildAss(subsForAss!, audioDur), new UTF8Encoding(false));

            var titleTxt = Path.Combine(workDir, "title.txt");
            var hasTitle = !string.IsNullOrWhiteSpace(request.TitleText);
            if (hasTitle) File.WriteAllText(titleTxt, request.TitleText!, new UTF8Encoding(false));

            // 水印（2026-10-21）：文字走 textfile（中文/标点不转义），Random 在 C# 侧先落成具体角
            var watermark = request.Watermark;
            var hasWatermark = watermark is { IsActive: true };
            var watermarkPos = hasWatermark
                ? ResolveWatermarkPosition(watermark!.Position)
                : WatermarkPosition.TopRight;
            if (hasWatermark)
                File.WriteAllText(Path.Combine(workDir, "watermark.txt"), watermark!.EffectiveText,
                    new UTF8Encoding(false));

            // ── filter_complex（视频图 + 可选 BGM 音频混音段） ──
            var graph = BuildFilterGraph(request.Items, frames, perShotTpad, w, h, tpadSeconds,
                hasSubtitles, hasTitle, overlapFrames, hasWatermark, watermarkPos);
            if (hasBgm)
            {
                // ⚠️ amix 必须 normalize=0：默认 normalize=1 会把各路音量除以输入数（配音被压低一半）
                // ⚠️ 全程铺满：duration=longest + 配音 apad 无限补 → BGM 响到视频结束（-shortest 截断）
                //    duration=first 会让 BGM 截断到配音长（配音 15s → BGM 只有 15s，错误行为）
                // ⚠️ BGM 结尾淡出用 afade：amix 的 dropout_transition 会给"结束的输入"淡出
                //    （配音先结束 → 语音末尾变小，不可接受），且两路都无限时不触发
                // ⚠️ apad 必须在 filter_complex 内：-af apad 是简单滤镜，与复杂滤镜图不能作用于同一流
                var vol = Math.Clamp(request.BgmVolume, 0.0, 1.0).ToString("0.###", CultureInfo.InvariantCulture);
                var fadeStart = Math.Max(0, progressDurSec - 2.0)
                    .ToString("0.###", CultureInfo.InvariantCulture);
                graph += $";[{n}:a]apad[a0];" +        // 前置分号：视频链 [vout] 结束后另起音频链
                         $"[{n + 1}:a]volume={vol},afade=t=out:st={fadeStart}:d=2[a1];" +
                         $"[a0][a1]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[outa]";
            }

            // ── 编码（h264_mf → 失败兜底 mpeg4） ──
            var args = new List<string>
            {
                "-hide_banner", "-y", "-nostats", "-progress", "pipe:1",
            };
            for (var i = 0; i < n; i++) { args.Add("-i"); args.Add(request.Items[i].Path); }
            args.Add("-i"); args.Add(audioPath);
            if (hasBgm)
            {
                // BGM 无限循环输入（比配音短也不怕），amix duration=first 截断到配音长
                args.Add("-stream_loop"); args.Add("-1");
                args.Add("-i"); args.Add(request.BgmPath!);
            }
            args.Add("-filter_complex"); args.Add(graph);
            args.Add("-map"); args.Add("[vout]");
            if (hasBgm)
            {
                args.Add("-map"); args.Add("[outa]"); // amix 混音输出
            }
            else
            {
                args.Add("-map"); args.Add($"{n}:a");
            }
            if (padAudio && !hasBgm) { args.Add("-af"); args.Add("apad"); } // BGM 时 apad 已在 filter_complex 内
            args.Add("-c:a"); args.Add("aac"); args.Add("-b:a"); args.Add("128k"); args.Add("-ar"); args.Add("44100");
            args.Add("-shortest"); args.Add("-movflags"); args.Add("+faststart");

            var encoders = new[] { "h264_mf", "mpeg4" };
            string? failureDetail = null;
            for (var attempt = 0; attempt < encoders.Length; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var encoder = encoders[attempt];
                var attemptArgs = new List<string>(args);
                if (encoder == "h264_mf")
                {
                    attemptArgs.AddRange(new[] { "-c:v", "h264_mf", "-b:v", "5M", "-maxrate", "7M", "-bufsize", "10M" });
                }
                else
                {
                    attemptArgs.AddRange(new[] { "-c:v", "mpeg4", "-q:v", "5" });
                }
                attemptArgs.Add(request.OutputPath);

                onProgress?.Invoke("encoding", 5);
                var (stdout, stderr, exitCode, canceled) =
                    await RunEncodeAsync(attemptArgs, workDir, progressDurSec, onProgress, ct).ConfigureAwait(false);

                if (exitCode == 0)
                {
                    onProgress?.Invoke("done", 100);
                    return request.OutputPath;
                }
                failureDetail = stderr;
                var encoderFailed = encoder == "h264_mf" &&
                    (stderr.Contains("h264_mf", StringComparison.OrdinalIgnoreCase) ||
                     stderr.Contains("MediaFoundation", StringComparison.OrdinalIgnoreCase) ||
                     stderr.Contains("Unsupported", StringComparison.OrdinalIgnoreCase) ||
                     stderr.Contains("Failed to create encoder", StringComparison.OrdinalIgnoreCase));
                Log.Warning("[Ffmpeg] {Encoder} 编码失败(exit={Exit})，encoderFailed={Flag}: {ErrTail}",
                    encoder, exitCode, encoderFailed, stderr[..Math.Min(stderr.Length, 500)]);
                if (!encoderFailed) break; // 非编码器问题，不重试
            }
            throw new IOException("视频合成失败: " + (failureDetail ?? "未知错误"));
        }
        catch
        {
            // 失败/取消 → 删除残缺输出
            try { if (File.Exists(request.OutputPath)) File.Delete(request.OutputPath); } catch { }
            throw;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* 清理失败不影响结果 */ }
        }
    }

    /// <summary>抽取视频封面（首帧附近）</summary>
    public static async Task<string> ExtractCoverAsync(string videoPath, string coverJpgPath, CancellationToken ct = default)
    {
        if (!File.Exists(videoPath)) throw new ArgumentException($"视频不存在: {videoPath}", nameof(videoPath));
        var args = new[] { "-hide_banner", "-y", "-ss", "0.1", "-i", videoPath, "-frames:v", "1", "-q:v", "3", coverJpgPath };
        var (_, stderr, exitCode) = await RunAsync(FfmpegExe, args, null, ct).ConfigureAwait(false);
        if (exitCode != 0 || !File.Exists(coverJpgPath))
            throw new IOException("封面抽取失败: " + stderr[..Math.Min(stderr.Length, 300)]);
        return coverJpgPath;
    }

    /// <summary>
    /// 从**源素材**抽一张无水印封面（水印开启时 cover.jpg 保持干净）：与合成首帧同样的 cover 裁剪，
    /// 只抽单帧、不做二次转码。首素材是图片且随机动效起始缩放 &gt; 1 时取景会比成片首帧略宽
    /// （可接受；像素级一致要在滤镜图里 split 一路干净帧，复杂度高一档）。
    /// </summary>
    public static async Task<string> ExtractCleanCoverAsync(
        string mediaPath, MediaKind kind, int width, int height, string coverJpgPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(mediaPath)) throw new ArgumentException($"素材不存在: {mediaPath}", nameof(mediaPath));
        var w = width % 2 == 0 ? width : width - 1;
        var h = height % 2 == 0 ? height : height - 1;
        var vf = $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1";
        var args = kind == MediaKind.Video
            ? new[] { "-hide_banner", "-y", "-ss", "0.1", "-i", mediaPath, "-vf", vf, "-frames:v", "1", "-q:v", "3", coverJpgPath }
            : new[] { "-hide_banner", "-y", "-i", mediaPath, "-vf", vf, "-frames:v", "1", "-q:v", "3", coverJpgPath };
        var (_, stderr, exitCode) = await RunAsync(FfmpegExe, args, null, ct).ConfigureAwait(false);
        if (exitCode != 0 || !File.Exists(coverJpgPath))
            throw new IOException("封面抽取失败: " + stderr[..Math.Min(stderr.Length, 300)]);
        return coverJpgPath;
    }

    /// <summary>
    /// 生成一张示例占位图（「合成队列」新建模板用）：纯色底 + 居中提示文字。
    /// drawtext 走 textfile（与标题叠加同款）——中文、空格、标点都不需要转义。
    /// 失败往外抛，由调用方决定降级（模板少一张示例图不影响文件夹本身可用）。
    /// </summary>
    public static async Task<string> GenerateSampleImageAsync(
        string outputPath, int width, int height, string text, string bgColorHex, CancellationToken ct = default)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "CreatFlow", "sampleimg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            File.WriteAllText(Path.Combine(workDir, "tip.txt"), text, new UTF8Encoding(false));
            var color = bgColorHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? bgColorHex : "0x" + bgColorHex;
            var args = new[]
            {
                "-hide_banner", "-y",
                "-f", "lavfi", "-i", $"color=c={color}:s={width}x{height}:d=1",
                "-frames:v", "1",
                "-vf", "drawtext=font='Microsoft YaHei':textfile=tip.txt:fontsize=56:fontcolor=0x5A6472:x=(w-text_w)/2:y=(h-text_h)/2",
                "-q:v", "3", outputPath,
            };
            var (_, stderr, exitCode) = await RunAsync(FfmpegExe, args, workDir, ct).ConfigureAwait(false);
            if (exitCode != 0 || !File.Exists(outputPath))
                throw new IOException("示例图生成失败: " + stderr[..Math.Min(stderr.Length, 300)]);
            return outputPath;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    // ═══════════════════════ 内部实现 ═══════════════════════

    /// <summary>
    /// 构建 filter_complex：图片走随机动效（zoompan 特效池，同片相邻不重样），视频走 cover+fps 统一（不动效），
    /// 统一 trim；overlapFrames &gt; 0 时相邻素材间 xfade 随机转场（同片接缝不重样），否则 concat 硬切；
    /// 末尾可选全局 tpad 定格（整段模式），再叠加字幕/标题。
    /// perShotTpad 非空（分镜模式）：逐镜定格 —— 视频镜句长超视频时 tpad 补时到句长。
    /// 每段末尾 format/settb 统一：xfade 要求两路输入像素格式与时基一致（AVTB 是社区配方里
    /// 对付 "inputs must have the same timebase" 的稳妥写法；无转场时多这一步也无副作用）。
    /// </summary>
    private static string BuildFilterGraph(
        IReadOnlyList<ComposeMediaItem> items, int[] frames,
        IReadOnlyList<double>? perShotTpad,
        int w, int h, double tpadSeconds, bool hasSubtitles, bool hasTitle, int overlapFrames,
        bool hasWatermark = false, WatermarkPosition watermarkPosition = WatermarkPosition.TopRight)
    {
        var sb = new StringBuilder();
        var scaleW = w * 2;
        var scaleH = h * 2;
        var seed = Random.Shared.Next(); // 每次合成重新洗牌（动效 + 转场两套各自随机）
        var motions = PickMotionIndexes(items.Count(i => i.Kind == MediaKind.Image), seed);
        var motionsCursor = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var d = frames[i];
            var dur = d / 25.0;
            if (items[i].Kind == MediaKind.Image)
            {
                // 图片：2× 超采样 + cover-crop + 随机 zoompan 动效（32 选 1，同片相邻不重样）
                var eff = Effects[motions[motionsCursor++]];
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{i}:v]scale={scaleW}:{scaleH}:force_original_aspect_ratio=increase," +
                    $"crop={scaleW}:{scaleH}," +
                    $"zoompan=z='{eff.Zoom}':x='{eff.X}':y='{eff.Y}':d={d}:s={w}x{h}:fps=25," +
                    $"trim=duration={dur:0.###},format=yuv420p,settb=AVTB,setpts=PTS-STARTPTS,setsar=1[v{i}];");
            }
            else
            {
                // 视频：cover 裁剪 + 帧率统一 + 截取片段。不动效（素材自带运动，叠 zoompan 双重运动且性能差数倍）；
                // 原声不映射即忽略；软字幕轨由 -map 显式指定自然丢弃
                var vf = $"[{i}:v]scale={w}:{h}:force_original_aspect_ratio=increase," +
                    $"crop={w}:{h},fps=25," +
                    $"trim=duration={dur.ToString("0.###", CultureInfo.InvariantCulture)}";
                if (perShotTpad is { Count: > 0 } && perShotTpad[i] > 0)
                    vf += $",tpad=stop_mode=clone:stop_duration={perShotTpad[i].ToString("0.###", CultureInfo.InvariantCulture)}";
                vf += ",format=yuv420p,settb=AVTB,setpts=PTS-STARTPTS,setsar=1";
                sb.Append(vf + $"[v{i}];");
            }
        }

        if (overlapFrames > 0)
        {
            // xfade 链：offset = 上一段累计时长 − 重叠（过渡起点）。时长账：素材帧已由
            // SpreadTransitionOverlap 补过重叠帧 → 成片总长不受影响。
            // ⚠️ 最后一段**不封口**（无输出标签、无分号）：公共尾巴（tpad/字幕/format）必须逗号
            // 接在同一条链上 —— 写成独立 "[x]" 节点再接 ",format=…" 是「标签后直接逗号」，
            // ffmpeg 报 "No such filter: ''"（临时探针真跑抓到过一次）
            var oSec = overlapFrames / 25.0;
            var transitions = PickTransitions(items.Count - 1, seed);
            var acc = frames[0] / 25.0;
            var prev = "v0";
            for (var i = 1; i < items.Count; i++)
            {
                var tail = i == items.Count - 1 ? "" : $"[x{i}];";
                sb.Append(CultureInfo.InvariantCulture,
                    $"[{prev}][v{i}]xfade=transition={transitions[i - 1]}" +
                    $":duration={oSec.ToString("0.###", CultureInfo.InvariantCulture)}" +
                    $":offset={(acc - oSec).ToString("0.###", CultureInfo.InvariantCulture)}{tail}");
                acc += frames[i] / 25.0 - oSec;
                if (i < items.Count - 1) prev = $"x{i}";
            }
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"[v0]");
            for (var i = 1; i < items.Count; i++) sb.Append(CultureInfo.InvariantCulture, $"[v{i}]");
            sb.Append(CultureInfo.InvariantCulture, $"concat=n={items.Count}:v=1:a=0");
        }
        if (tpadSeconds > 0)
            sb.Append(CultureInfo.InvariantCulture, $",tpad=stop_mode=clone:stop_duration={tpadSeconds:0.###}");
        if (hasSubtitles) sb.Append(",subtitles=subs.ass");
        if (hasTitle) sb.Append(",drawtext=font='Microsoft YaHei':textfile=title.txt:fontsize=72:fontcolor=white:borderw=4:bordercolor=black@0.6:x=(w-text_w)/2:y=h*0.12:enable='lt(t,3)'");
        // 水印放最后：字幕/标题之后，全片显示（任意位置都在最上层；角落位置本身已避开字幕带）
        if (hasWatermark) sb.Append(',').Append(BuildWatermarkFilter(w, h, watermarkPosition));
        sb.Append(",format=yuv420p[vout]");
        return sb.ToString();
    }

    /// <summary>
    /// 水印 drawtext 片段（不含前导逗号）。字号/边距按 min(w,h) 算（1080 → 40px / 48px），
    /// 横竖屏一致；白字 55% + 细黑描边，浅色画面上也能看清。
    /// 位置表达式用 drawtext 的 text_w/text_h（与标题同一套变量，已在捆绑 ffmpeg 上验证）。
    /// </summary>
    public static string BuildWatermarkFilter(int w, int h, WatermarkPosition position)
    {
        var size = Math.Max(12, (int)Math.Round(Math.Min(w, h) * 0.037));
        var margin = Math.Max(12, (int)Math.Round(Math.Min(w, h) * 0.044));
        var (x, y) = position switch
        {
            WatermarkPosition.TopLeft => ($"{margin}", $"{margin}"),
            WatermarkPosition.BottomLeft => ($"{margin}", $"h-text_h-{margin}"),
            WatermarkPosition.BottomRight => ($"w-text_w-{margin}", $"h-text_h-{margin}"),
            _ => ($"w-text_w-{margin}", $"{margin}"),   // TopRight（Random 已在上游解析成具体角）
        };
        // expansion=none：水印文字里的 %{...} 不当 drawtext 表达式展开（textfile 只负责免转义）
        return $"drawtext=font='Microsoft YaHei':textfile=watermark.txt:expansion=none:fontsize={size}" +
               $":fontcolor=white@0.55:borderw=2:bordercolor=black@0.3:x={x}:y={y}";
    }

    /// <summary>Random → 四角之一（其余原样返回）。每次合成重新随机，批量里每条各自换角。</summary>
    public static WatermarkPosition ResolveWatermarkPosition(WatermarkPosition position, Random rng)
        => position == WatermarkPosition.Random ? (WatermarkPosition)rng.Next(4) : position;

    public static WatermarkPosition ResolveWatermarkPosition(WatermarkPosition position)
        => ResolveWatermarkPosition(position, Random.Shared);

    /// <summary>
    /// 图片随机动效池（zoompan 单滤镜表达式，零额外滤镜/零性能损失）——程序化组合生成：
    /// 8 方向摇移 ×（匀速 / 推近 / 拉远）+ 四角定点推近 + 中心推拉两档 = 32 种。
    /// 2026-10-11 用户反馈「固定几个特效会审美疲劳」：手写 8 条扩成 32 条组合，
    /// 同一条视频里相邻素材不重样（<see cref="PickMotionIndexes"/>）、每次合成重新洗牌。
    /// zoompan 表达式变量（务必与 ffmpeg zoompan.c 的 var_names 一致）：
    /// zoom=当前缩放、on=输出帧号、duration=每输入帧的输出帧数（⚠️ 不是 d，d 只是参数名）、
    /// iw/ih=输入尺寸。缩放一律用 on/duration 的绝对式（不用 zoom+步长的累加式）——
    /// 累加式在长镜头里会提前顶到上限、后半段静止（末图要吸收配音余量，可能很长）。
    /// </summary>
    private static readonly (string Zoom, string X, string Y)[] Effects = BuildEffects();

    private static (string Zoom, string X, string Y)[] BuildEffects()
    {
        var list = new List<(string Zoom, string X, string Y)>();

        // 中心推近 / 拉远（两档幅度）
        const string cx = "iw/2-(iw/zoom/2)";
        const string cy = "ih/2-(ih/zoom/2)";
        list.Add(("1+0.10*on/duration", cx, cy));
        list.Add(("1+0.16*on/duration", cx, cy));
        list.Add(("1.12-0.12*on/duration", cx, cy));
        list.Add(("1.16-0.16*on/duration", cx, cy));

        // 八方向摇移（锚点 = 裁剪窗口左上角在图宽/高里的比例，起点 → 终点）；每方向三档：
        // 匀速 / 伴随推近 / 伴随拉远
        (double X0, double Y0, double X1, double Y1)[] dirs =
        [
            (0, 0.5, 1, 0.5),     // 右移
            (1, 0.5, 0, 0.5),     // 左移
            (0.5, 0, 0.5, 1),     // 下移
            (0.5, 1, 0.5, 0),     // 上移
            (0, 0, 1, 1),         // 右下
            (1, 0, 0, 1),         // 左下
            (0, 1, 1, 0),         // 右上
            (1, 1, 0, 0),         // 左上
        ];
        foreach (var (x0, y0, x1, y1) in dirs)
        {
            var px = PanAnchor("iw", x0, x1);
            var py = PanAnchor("ih", y0, y1);
            list.Add(("1.15", px, py));                        // 恒焦摇移
            list.Add(("1.02+0.14*on/duration", px, py));       // 摇移 + 推近
            list.Add(("1.16-0.14*on/duration", px, py));       // 摇移 + 拉远
        }

        // 四角定点推近（往角落推，中心逐步让位）
        foreach (var (fx, fy) in new[] { (0.06, 0.06), (0.94, 0.06), (0.06, 0.94), (0.94, 0.94) })
            list.Add(("1+0.12*on/duration", $"(iw-iw/zoom)*{Num(fx)}", $"(ih-ih/zoom)*{Num(fy)}"));

        return [.. list];

        // 摇移锚点插值：p(t) = p0 + (p1−p0)·t（0→1 铺满整镜），乘可平移余量 (iw−iw/zoom)
        static string PanAnchor(string coord, double p0, double p1) =>
            $"({coord}-{coord}/zoom)*({Num(p0)}+{Num(p1 - p0)}*on/duration)";
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>动效池大小（--selftest 断言「够多」）</summary>
    public static int MotionEffectCount => Effects.Length;

    /// <summary>转场池大小（--selftest 断言「够多」）</summary>
    public static int TransitionCount => Transitions.Length;

    /// <summary>
    /// 相邻素材之间的随机转场池（ffmpeg xfade 的 transition 名，2026-10-11 已逐一在本机
    /// 捆绑 ffmpeg 上验证可用）。数量以「多」为准：同一条视频里每处接缝都不同
    /// （<see cref="PickTransitions"/>），每次合成重新洗牌。
    /// </summary>
    private static readonly string[] Transitions =
    [
        "fade", "fadeblack", "fadewhite", "fadegrays", "dissolve", "pixelize",
        "wipeleft", "wiperight", "wipeup", "wipedown",
        "wipetl", "wipetr", "wipebl", "wipebr",
        "slideleft", "slideright", "slideup", "slidedown",
        "smoothleft", "smoothright", "smoothup", "smoothdown",
        "circleopen", "circleclose", "circlecrop", "radial",
        "vertopen", "horzopen", "diagtl", "diagbr", "squeezeh", "squeezev", "zoomin",
    ];

    /// <summary>
    /// 给一段视频里的 count 张图各分配一个动效下标：整轮（池子大小）内两两不同，跨轮边界也防撞，
    /// seed 决定顺序。纯函数，--selftest 直接断言「相邻不重样」。
    /// </summary>
    public static IReadOnlyList<int> PickMotionIndexes(int count, int seed)
    {
        var rng = new Random(seed);
        var order = Array.Empty<int>();
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            var slot = i % Effects.Length;
            if (slot == 0)
            {
                order = [.. Enumerable.Range(0, Effects.Length)];
                Shuffle(order, rng);
                if (i > 0 && order[0] == result[i - 1]) (order[0], order[1]) = (order[1], order[0]);
            }
            result[i] = order[slot];
        }
        return result;
    }

    /// <summary>
    /// 给 junctions 处接缝各选一个转场：同样整轮内不重样、跨轮防撞
    /// （接缝数 ≤ 池子大小时全片零重复）。纯函数，--selftest 直接断言。
    /// </summary>
    public static IReadOnlyList<string> PickTransitions(int junctions, int seed)
    {
        var rng = new Random(seed);
        string[] order = [];
        var result = new string[junctions];
        for (var i = 0; i < junctions; i++)
        {
            var slot = i % Transitions.Length;
            if (slot == 0)
            {
                order = [.. Transitions];
                Shuffle(order, rng);
                if (i > 0 && order[0] == result[i - 1]) (order[0], order[1]) = (order[1], order[0]);
            }
            result[i] = order[slot];
        }
        return result;
    }

    private static void Shuffle<T>(T[] array, Random rng)
    {
        for (var i = array.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }
    }

    /// <summary>转场重叠帧数（0.4s @25fps —— xfade 的重叠时长，与图内 fps=25 对齐）</summary>
    private const int TransitionOverlapFrames = 10;

    /// <summary>
    /// 把转场重叠帧摊进图片素材：xfade 会让时间轴缩短 (n−1)×重叠帧，不补回来
    /// 成片会比配音短（末尾 -shortest 提前掐掉）。视频素材**不能**拉长（trim 超过源长度只会更短），
    /// 所以只摊给图片；一张图片都没有（纯视频混剪）→ 返回 0 = 这条不启用转场、保持硬切。
    /// 太短的段落（&lt; 2×重叠 = 0.8s）也放弃转场 —— 叠不下。纯函数（就地改 frames），--selftest 直接断言。
    /// </summary>
    public static int SpreadTransitionOverlap(int[] frames, IReadOnlyList<MediaKind> kinds)
    {
        if (frames.Length < 2 || frames.Length != kinds.Count) return 0;
        var images = Enumerable.Range(0, frames.Length).Where(i => kinds[i] == MediaKind.Image).ToList();
        if (images.Count == 0) return 0;
        if (frames.Any(f => f < TransitionOverlapFrames * 2)) return 0;

        var total = (frames.Length - 1) * TransitionOverlapFrames;
        var share = total / images.Count;
        foreach (var i in images) frames[i] += share;
        frames[images[^1]] += total - share * images.Count;   // 除不尽的余数给最后一张图
        return TransitionOverlapFrames;
    }

    /// <summary>生成 ASS 字幕：中文自动换行（16 字/行，最多 3 行）、白字描边半透明底、句间 −50ms 防闪烁</summary>
    private static string BuildAss(IReadOnlyList<SubtitleItem> subtitles, double audioDurSec)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine(CultureInfo.InvariantCulture, $"PlayResX: 1080");
        sb.AppendLine("PlayResY: 1920");
        sb.AppendLine("WrapStyle: 2");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine("Style: Default,Microsoft YaHei,58,&H00FFFFFF,&H00FFFFFF,&H00000000,&H96000000,0,0,0,0,100,100,0,0,1,3,1,2,60,60,120,1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        for (var i = 0; i < subtitles.Count; i++)
        {
            var s = subtitles[i];
            var endMs = i + 1 < subtitles.Count
                ? Math.Max(s.EndMs, subtitles[i + 1].StartMs - 50) // 下一句前 50ms 结束，防闪烁
                : Math.Max(s.EndMs, (int)(audioDurSec * 1000));
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Dialogue: 0,{FormatAssTime(s.StartMs)},{FormatAssTime(endMs)},Default,,0,0,0,,{WrapAssText(s.Text)}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// C# 端确定性换行：16 字/行（(1080−120)/58≈16.5），最多 3 行，超 48 字截断加省略号。
    /// 逐行转义：\N 是 ASS 换行符，必须先切片再转义，否则 \N 被转成字面量反斜杠（历史 bug）。
    /// 逗号不转义：Text 是 Dialogue 第 10 字段（第 9 个逗号后全是文本），字段内逗号原样显示。
    /// </summary>
    private static string WrapAssText(string text)
    {
        const int lineChars = 16;
        const int maxLines = 3;
        var lines = new List<string>();
        var remaining = text;
        while (remaining.Length > 0 && lines.Count < maxLines)
        {
            var take = Math.Min(lineChars, remaining.Length);
            lines.Add(EscapeAss(remaining[..take]));
            remaining = remaining[take..];
        }
        var joined = string.Join("\\N", lines);
        return remaining.Length > 0 ? joined + "…" : joined;
    }

    private static string EscapeAss(string text) =>
        text.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");

    private static string FormatAssTime(int ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
    }

    /// <summary>跑普通进程（ffprobe/抽帧/兜底探测），返回 (stdout, stderr, exitCode)</summary>
    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunAsync(
        string exe, IEnumerable<string> args, string? workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir ?? AppDomain.CurrentDomain.BaseDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new IOException($"无法启动进程: {exe}");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        using var killReg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }

        return (await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false), proc.ExitCode);
    }

    /// <summary>跑编码进程：-progress pipe:1 进度解析 + 取消 Kill(true) + 30 分钟超时。</summary>
    private static async Task<(string Stdout, string Stderr, int ExitCode, bool Canceled)> RunEncodeAsync(
        List<string> args, string workDir, double audioDurSec, Action<string, int>? onProgress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));

        var psi = new ProcessStartInfo
        {
            FileName = FfmpegExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new IOException($"无法启动 ffmpeg: {FfmpegExe}");

        using var killReg = timeout.Token.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
        var progressTask = ReadProgressAsync(proc.StandardOutput, audioDurSec, onProgress, timeout.Token);

        bool canceled;
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            canceled = false;
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            throw new TaskCanceledException("视频合成已取消", innerException: null, timeout.Token);
        }
        finally
        {
            try { killReg.Dispose(); } catch { }
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        try { await progressTask.ConfigureAwait(false); } catch { }
        return (string.Empty, stderr, proc.ExitCode, canceled);
    }

    /// <summary>
    /// 解析 ffmpeg -progress pipe:1 输出（key=value 行，progress=end 结束）。
    /// ⚠️ out_time_ms 实为微秒（ffmpeg 命名怪癖）；进度映射 5%~99%。
    /// </summary>
    private static async Task ReadProgressAsync(
        StreamReader stdout, double audioDurSec, Action<string, int>? onProgress, CancellationToken ct)
    {
        string? line;
        while ((line = await stdout.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..];
            if (key is "out_time_us" or "out_time_ms" && long.TryParse(value, out var us))
            {
                var pct = (int)Math.Clamp(us / 1_000_000.0 / audioDurSec * 95.0 + 5.0, 0, 99);
                onProgress?.Invoke("encoding", pct);
            }
            else if (key == "progress" && value == "end")
            {
                return;
            }
        }
    }
}
