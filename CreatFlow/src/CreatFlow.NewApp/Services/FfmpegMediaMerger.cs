using CreatFlow.Media;
using CreatFlow.Platforms.Download;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 音视频合并（分离流）—— 进程调用 runtime/ffmpeg 的 **LGPL 共享构建**，`-c copy` 不转码。
/// 与合成管线的 ffmpeg 用法一致：不静态链接 libav*，避免把 GPL/LGPL 义务带进主程序。
/// </summary>
public sealed class FfmpegMediaMerger : IMediaMerger
{
    public async Task MergeAsync(string videoPath, string audioPath, string outputPath, CancellationToken ct)
    {
        var (_, stderr, exitCode) = await FfmpegCli.RunAsync(new[]
        {
            "-hide_banner", "-y",
            "-i", videoPath,
            "-i", audioPath,
            "-map", "0:v:0",
            "-map", "1:a:0",
            "-c", "copy",
            "-shortest",
            outputPath,
        }, ct).ConfigureAwait(false);

        if (exitCode != 0 || !File.Exists(outputPath))
        {
            var detail = stderr.Length > 400 ? stderr[^400..] : stderr;
            throw new IOException($"ffmpeg 合并失败（退出码 {exitCode}）：{detail}");
        }
    }
}
