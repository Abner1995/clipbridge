using CreatFlow.Media.Video;

namespace CreatFlow.NewApp.Services;

/// <summary>素材文件类型判定（合成页 / 文件夹批量共用；按扩展名 —— 选择器不做过滤，这里兜底）</summary>
public static class MediaFiles
{
    public static bool IsVideo(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".flv" or ".m4v" or ".webm";

    public static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif";

    public static bool IsMedia(string path) => IsVideo(path) || IsImage(path);

    public static bool IsAudio(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".wav" or ".m4a" or ".aac" or ".flac";

    /// <summary>转成合成素材条目（图片/视频）</summary>
    public static ComposeMediaItem ToItem(string path) =>
        new(path, IsVideo(path) ? MediaKind.Video : MediaKind.Image);
}
