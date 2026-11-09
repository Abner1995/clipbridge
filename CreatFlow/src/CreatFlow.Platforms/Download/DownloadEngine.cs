namespace CreatFlow.Platforms.Download;

/// <summary>音视频合并（分离流才需要）。实现放 App/Media 层，引擎本身不引用 ffmpeg。</summary>
public interface IMediaMerger
{
    /// <summary>无损合并（ffmpeg -c copy）；失败抛异常，由调用方翻译成 Failed。</summary>
    Task MergeAsync(string videoPath, string audioPath, string outputPath, CancellationToken ct);
}

/// <summary>
/// 下载引擎 —— 解析与下载分离（docs/视频下载引擎.md 第 3 节）。
/// 队列层只调两个方法：<see cref="ExtractAsync"/>（预览）与 <see cref="DownloadAsync"/>（落地）。
/// </summary>
public sealed class DownloadEngine(ExtractorRegistry registry, IMediaMerger? merger = null)
{
    public ExtractorRegistry Registry { get; } = registry;
    public IMediaMerger? Merger { get; } = merger;

    /// <summary>按主机路由到平台 extractor；不支持时抛 Unsupported。</summary>
    public IVideoExtractor GetExtractor(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new DownloadException(DownloadErrorKind.Unsupported, $"不是合法链接：{url}");
        return Registry.Get(uri);
    }

    public Task<VideoInfo> ExtractAsync(string url, ExtractContext context, CancellationToken ct) =>
        GetExtractor(url).ExtractAsync(url, context, ct);

    /// <summary>
    /// 下载一条已解析的作品。
    ///
    /// 落盘策略：
    ///  · 最终路径由模板决定（同名 = 同一作品，天然幂等）；
    ///  · 下载先写 `{根}/.downloads/{id}-{guid}/`，成功后再 move —— 用户看不到半成品；
    ///  · 目标文件已存在且非空 → 直接返回（重复下载不覆盖）。
    /// </summary>
    public async Task<DownloadOutcome> DownloadAsync(
        VideoInfo info,
        MediaFormat video,
        MediaFormat? audio,
        OutputPathOptions pathOptions,
        string? resolution,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        if (video.Kind != StreamKind.Direct || (audio is not null && audio.Kind != StreamKind.Direct))
            throw new DownloadException(DownloadErrorKind.Unsupported,
                "当前版本只支持直链下载（HLS/DASH 下载器尚未接入）");

        var started = DateTime.UtcNow;
        var finalPath = OutputPathResolver.Resolve(
            pathOptions.RootDirectory, pathOptions.Template, info, Clock.Now,
            video.Container, resolution ?? video.ResolutionText);

        // 幂等：已下载过就直接返回（不重新拉一份）
        if (File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
        {
            var existingSize = new FileInfo(finalPath).Length;
            return new DownloadOutcome(finalPath, existingSize,
                await ComputeSha256Async(finalPath, ct).ConfigureAwait(false),
                DateTime.UtcNow - started);
        }

        var root = Path.GetFullPath(pathOptions.RootDirectory);
        EnsureFreeSpace(root, FormatSelector.EstimateBytes(new FormatSelection(video, audio)));

        // 临时目录**按作品固定**（不是每次随机）：失败 / 取消后重试能接着续传已下载的 .part；
        // 成功后删掉，崩溃留下的孤儿由队列启动时按 7 天清理。
        var tempDir = Path.Combine(root, ".downloads",
            $"{OutputPathResolver.SanitizeSegment(info.Platform)}-{OutputPathResolver.SanitizeSegment(info.VideoId)}");
        Directory.CreateDirectory(tempDir);

        var completed = false;
        try
        {
            var videoTemp = Path.Combine(tempDir, $"video.{OutputPathResolver.NormalizeExtension(video.Container)}");
            await new HttpFileDownloader()
                .DownloadAsync(video, videoTemp, Staged(progress, audio is null ? "下载中" : "下载视频"), ct)
                .ConfigureAwait(false);

            var payload = videoTemp;
            if (audio is not null)
            {
                if (Merger is null)
                    throw new DownloadException(DownloadErrorKind.Unsupported,
                        "该作品需要合并音视频，但当前环境没有可用的 ffmpeg");

                var audioTemp = Path.Combine(tempDir, $"audio.{OutputPathResolver.NormalizeExtension(audio.Container)}");
                await new HttpFileDownloader()
                    .DownloadAsync(audio, audioTemp, Staged(progress, "下载音频"), ct)
                    .ConfigureAwait(false);

                progress?.Report(new DownloadProgress(0, null, 0, null, "合并中"));
                var merged = Path.Combine(tempDir, "merged.mp4");
                await Merger.MergeAsync(videoTemp, audioTemp, merged, ct).ConfigureAwait(false);
                payload = merged;
            }

            var dir = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 跨盘 move 会退化成复制 —— 上面已查过空间
            File.Move(payload, finalPath, overwrite: true);

            var size = new FileInfo(finalPath).Length;
            var sha = await ComputeSha256Async(finalPath, ct).ConfigureAwait(false);
            progress?.Report(new DownloadProgress(size, size, 0, TimeSpan.Zero, "完成"));
            completed = true;
            return new DownloadOutcome(finalPath, size, sha, DateTime.UtcNow - started);
        }
        finally
        {
            // 只在成功时清理：失败 / 取消留着 .part，重试才能续传
            if (completed)
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch { /* 删不掉不影响任务结果，留给启动清理 */ }
            }
        }
    }

    /// <summary>下载前剩余空间检查（预估体积 × 1.2；预估未知则跳过）。</summary>
    private static void EnsureFreeSpace(string root, long? estimated)
    {
        if (estimated is not > 0) return;
        try
        {
            var pathRoot = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(pathRoot)) return;
            var drive = new DriveInfo(pathRoot);
            if (!drive.IsReady) return;
            var required = (long)(estimated.Value * 1.2);
            if (drive.AvailableFreeSpace < required)
                throw new DownloadException(DownloadErrorKind.DiskFull,
                    $"磁盘空间不足：需要约 {required / 1024 / 1024}MB，" +
                    $"可用 {drive.AvailableFreeSpace / 1024 / 1024}MB");
        }
        catch (DownloadException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // 网络盘/映射盘取不到空间信息时不拦 —— 让真正的写入失败去报错
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static IProgress<DownloadProgress>? Staged(IProgress<DownloadProgress>? inner, string stage) =>
        inner is null ? null : new StagedProgress(inner, stage);

    private sealed class StagedProgress(IProgress<DownloadProgress> inner, string stage)
        : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => inner.Report(value with { Stage = stage });
    }
}

/// <summary>内置提取器集合（DI 里按平台注册；冒烟/自检也可直接 new）。</summary>
public static class DownloadExtractors
{
    public static ExtractorRegistry CreateDefault() => new(
    [
        new Extractors.DouyinDownloadExtractor(),
        new Extractors.KuaishouDownloadExtractor(),
        new Extractors.XiaohongshuDownloadExtractor(),
    ]);
}
