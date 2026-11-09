namespace CreatFlow.Media.Video;

/// <summary>
/// 视频合成器抽象 — 图片轮播 + 配音 + 字幕 → mp4。
/// 当前唯一实现 FfmpegVideoComposer（静态类，进程内执行 ffmpeg）；
/// 未来多实现（如硬件加速管线）时按此契约实例化。
/// </summary>
public interface IVideoComposer
{
    /// <summary>合成视频，返回输出路径。进度回调 (stage, 0-100)，后台线程触发。</summary>
    Task<string> ComposeAsync(
        ComposeRequest request,
        Action<string, int>? onProgress = null,
        CancellationToken ct = default);
}
