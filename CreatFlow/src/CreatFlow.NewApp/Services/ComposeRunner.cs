namespace CreatFlow.NewApp.Services;

/// <summary>一条合成任务的完整输入快照（合成页单条 / 合成队列任务共用 —— 入队后与页面状态解耦）</summary>
public sealed record ComposeJob
{
    /// <summary>素材（顺序即播放顺序；分镜模式下句数必须 = 素材数）</summary>
    public required IReadOnlyList<ComposeMediaItem> Materials { get; init; }

    /// <summary>输出根目录（每条在 <c>根目录/标题/</c> 落成发布单元）</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>发布标题（发布单元文件夹名与 params「标题」；空 = 时间戳兜底）</summary>
    public string PublishTitle { get; init; } = "";

    /// <summary>画面叠加标题（可选；与发布标题是两回事）</summary>
    public string? OverlayTitle { get; init; }

    /// <summary>文本配音文案（UseShots 时一行一句）；本地音频模式下可空。
    /// 角色库的合成都存**套模板后的最终文案** —— 模板只在页面/批量层套一次，runner 只认现成文本</summary>
    public string? Text { get; init; }

    /// <summary>角色名（角色库；**仅供入队搬运与队列行展示** —— runner 不读它，
    /// ToJob 也刻意不回填，别当漏写"补齐"）</summary>
    public string? RoleName { get; init; }

    /// <summary>自动发布目标账号快照（**runner 不读** —— 它归队列消费者的自动发布段；
    /// 随任务一路搬运，见 <c>ComposeQueueViewModel</c> 的 <c>AutoPublishAsync</c>）</summary>
    public IReadOnlyList<AutoPublishTarget> AutoPublishTargets { get; init; } = [];

    /// <summary>AI 分镜：文案按行拆句（句数 = 素材数），逐句配音</summary>
    public bool UseShots { get; init; }

    /// <summary>本地音频文件（与 UseShots 互斥）；给定时不走 TTS</summary>
    public string? AudioPath { get; init; }

    /// <summary>配音引擎名（TtsProviderFactory 里的 Name；未知回退默认引擎）</summary>
    public string? ProviderName { get; init; }

    /// <summary>音色（Kokoro = 引擎内 sid；未知/空回退引擎音色表第一个）</summary>
    public string? VoiceId { get; init; }

    public double Speed { get; init; } = 1.0;
    public double Pitch { get; init; } = 1.0;

    public string? BgmPath { get; init; }
    public double BgmVolume { get; init; } = 0.25;
    public bool IsWide { get; init; }

    /// <summary>视频水印（账号设置页配置；**入队即冻结** —— 排队期间改设置不影响已排队任务）</summary>
    public WatermarkSpec Watermark { get; init; } = WatermarkSpec.Default;

    /// <summary>定时发布（写进发布单元 params 的「发布时间」；null = 立即）</summary>
    public DateTime? ScheduledAt { get; init; }
}

/// <summary>一条合成任务跑完的产物定位</summary>
public sealed record ComposeOutcome(string OutputPath, string UnitDir);

/// <summary>
/// 合成执行器 —— 自合成页 ComposeAsync 抽出的核心：配音（分镜逐句 / 文本整段 / 本地文件）
/// → ffmpeg 合成 → 落发布单元（params.txt + cover.jpg）。
/// 合成页与合成队列共用同一条路径；任务只认 <see cref="ComposeJob"/> 快照，与页面状态无关。
/// </summary>
public static class ComposeRunner
{
    /// <param name="onProgress">阶段消息 + 进度百分比（配音阶段为 0）</param>
    public static async Task<ComposeOutcome> RunAsync(ComposeJob job,
        Action<string, int>? onProgress, CancellationToken ct)
    {
        string? tempAudioPath = null;
        string? shotsTempDir = null;
        try
        {
            // ── 1. 配音 ──
            string audioPath;
            IReadOnlyList<SubtitleItem>? subtitles = null;
            List<ComposeShot>? shots = null;

            if (job.UseShots)
            {
                // 分镜模式：文案按行拆句（每行 = 一镜），逐句配音
                var provider = ResolveProvider(job);
                var voice = ResolveVoiceId(provider, job.VoiceId);
                var lines = SplitLines(job.Text);
                var tmpDir = Path.Combine(Path.GetTempPath(), "CreatFlow", $"shots_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tmpDir);
                shotsTempDir = tmpDir;
                shots = [];
                for (var i = 0; i < lines.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    onProgress?.Invoke($"正在合成配音 {i + 1}/{lines.Length} …", 0);
                    var result = await provider.SynthesizeAsync(
                        new TtsRequest(lines[i], voice, null, job.Speed, job.Pitch, IncludeSubtitles: true),
                        msg => onProgress?.Invoke(msg, 0), ct);
                    var shotMp3 = Path.Combine(tmpDir, $"shot{i}.mp3");
                    await File.WriteAllBytesAsync(shotMp3, result.Audio, ct);
                    shots.Add(new ComposeShot(job.Materials[i], shotMp3, result.Subtitles));
                }
                audioPath = "";   // 分镜模式占位（合成器用 Shots 逐镜音频）
            }
            else if (!string.IsNullOrEmpty(job.AudioPath))
            {
                audioPath = job.AudioPath;
                onProgress?.Invoke("正在探测音频…", 0);
            }
            else
            {
                var provider = ResolveProvider(job);
                var voice = ResolveVoiceId(provider, job.VoiceId);
                onProgress?.Invoke("正在合成配音…", 0);
                var result = await provider.SynthesizeAsync(
                    new TtsRequest(job.Text ?? "", voice, null, job.Speed, job.Pitch, IncludeSubtitles: true),
                    msg => onProgress?.Invoke(msg, 0), ct);
                tempAudioPath = Path.Combine(Path.GetTempPath(), "CreatFlow", $"compose_{Guid.NewGuid():N}.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(tempAudioPath)!);
                await File.WriteAllBytesAsync(tempAudioPath, result.Audio, ct);
                audioPath = tempAudioPath;
                subtitles = result.Subtitles;
            }
            ct.ThrowIfCancellationRequested();

            // ── 2. 输出：发布单元目录 <输出目录>/<标题>/<标题>.mp4 ──
            var unitName = ComposeUnitWriter.ToSafeName(job.PublishTitle, $"合成视频{DateTime.Now:yyyyMMddHHmmss}");
            var unitTitle = string.IsNullOrWhiteSpace(job.PublishTitle) ? unitName : job.PublishTitle.Trim();
            var unitDir = Path.Combine(job.OutputDirectory, unitName);
            Directory.CreateDirectory(unitDir);
            var outputPath = Path.Combine(unitDir, unitName + ".mp4");
            var videoWidth = job.IsWide ? 1920 : 1080;
            var videoHeight = job.IsWide ? 1080 : 1920;

            // ── 3. 合成 ──
            var request = new ComposeRequest(
                [.. job.Materials], audioPath, outputPath,
                Width: videoWidth,
                Height: videoHeight,
                Subtitles: shots is null ? subtitles : null,
                TitleText: string.IsNullOrWhiteSpace(job.OverlayTitle) ? null : job.OverlayTitle.Trim(),
                BgmPath: job.BgmPath,
                BgmVolume: job.BgmVolume,
                Shots: shots,
                Watermark: job.Watermark);

            await FfmpegVideoComposer.ComposeAsync(request, (stage, pct) => onProgress?.Invoke(
                stage == "encoding" ? $"正在合成视频… {pct}%" : pct == 100 ? "合成完成" : "正在准备…", pct), ct);

            // ── 3b. 干净封面（水印开启时不能从成片抽 —— 成片已经带水印）──
            // 从首素材按同样的 cover 裁剪抽单帧；失败回落 4 的成片抽帧（封面会带水印，记日志）。
            // 已有 cover.* 不动（用户换过的封面比任何自动结果值钱）。
            if (job.Watermark.IsActive && ComposeUnitWriter.FindCover(unitDir) is null)
            {
                var cleanCover = Path.Combine(unitDir, ComposeUnitWriter.CoverFileName);
                try
                {
                    await FfmpegVideoComposer.ExtractCleanCoverAsync(
                        job.Materials[0].Path, job.Materials[0].Kind, videoWidth, videoHeight,
                        cleanCover, ct);
                }
                catch (Exception ex)
                {
                    // 失败/取消都清掉半截封面 —— 否则 WriteAsync 的 FindCover 会把它当成品、
                    // 跳过回退抽帧，发布单元里留一个坏封面
                    try { if (File.Exists(cleanCover)) File.Delete(cleanCover); } catch { }
                    if (ex is OperationCanceledException) throw;
                    Log.Warning(ex, "[Compose] 无水印封面抽取失败，回退成片抽帧（封面会带水印）{Dir}", unitDir);
                }
            }

            // ── 4. 发布单元元数据（params.txt + cover.jpg）── 失败不翻车（导入端会兜底抽帧）
            try
            {
                await ComposeUnitWriter.WriteAsync(unitDir, outputPath, unitTitle, job.ScheduledAt, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "[Compose] 发布单元元数据写入失败 {Dir}", unitDir);
            }

            return new ComposeOutcome(outputPath, unitDir);
        }
        finally
        {
            // 清理临时配音与分镜目录
            if (tempAudioPath != null)
            {
                try { File.Delete(tempAudioPath); } catch { }
            }
            if (shotsTempDir != null)
            {
                try { Directory.Delete(shotsTempDir, recursive: true); } catch { }
            }
        }
    }

    /// <summary>解析配音引擎（未知名称回退默认）—— Kokoro 未就绪直接抛人话</summary>
    private static ITtsProvider ResolveProvider(ComposeJob job)
    {
        var provider = TtsProviderFactory.Create(job.ProviderName);
        if (TtsEngines.IsKokoro(provider) && !KokoroModels.IsReady)
            throw new InvalidOperationException("Kokoro 本地模型未下载：请到「AI 设置」页下载模型后再合成");
        return provider;
    }

    /// <summary>解析音色 sid（对不上就回落引擎音色表第一个）</summary>
    private static string ResolveVoiceId(ITtsProvider provider, string? voiceId)
    {
        if (!string.IsNullOrEmpty(voiceId) && provider.Voices.Any(v => v.Id == voiceId))
            return voiceId;
        return provider.Voices.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException("没有可用的配音音色：请在「AI 设置」页检查 Kokoro 模型");
    }

    private static string[] SplitLines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
