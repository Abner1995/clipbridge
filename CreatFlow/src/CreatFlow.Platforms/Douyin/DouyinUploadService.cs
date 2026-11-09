namespace CreatFlow.Platforms.Douyin;

using CreatFlow.Platforms.Core.Vod;

/// <summary>
/// 抖音视频上传 —— ByteDance VOD 通用引擎的**薄包装**（阶段 5.1）。
///
/// 5 阶段管线（ApplyUploadInner → TOS 分片 → MergeFile → CommitUploadInner）已抽到
/// <see cref="BdvodUploadService"/>；抖音与头条共用。平台差异只有三个：
///   · <c>SpaceName</c> = <c>aweme</c>；
///   · Query 不带 <c>app_id</c> / <c>user_id</c> / <c>EnOID</c>（头条全带，见接口说明书 §4.2.2）；
///   · 分片 3 MB（与 upload.js 一致）。
///
/// 对外 API 与字段（<see cref="DouyinUploadResult"/>）保持不变 —— 发布管线不用改。
/// </summary>
public sealed class DouyinUploadService
{
    private readonly BdvodUploadService _engine;

    public DouyinUploadService(
        PlatformOptions options,
        ILogger<DouyinUploadService>? logger = null)
    {
        _engine = new BdvodUploadService(
            options,
            new BdvodUploadSettings(
                VodHost: options.DouyinVodHost,
                SpaceName: options.DouyinSpaceName),
            logger);
    }

    /// <summary>默认分片大小 3MB（与 upload.js 一致）</summary>
    public int ChunkSize
    {
        get => _engine.ChunkSize;
        set => _engine.ChunkSize = value;
    }

    /// <summary>
    /// 完整上传一个视频到抖音 VOD。
    ///
    /// 参数:
    ///   filePath      — 本地视频文件路径
    ///   stsAccessKey  — STS AccessKeyId
    ///   stsSecretKey  — STS SecretAccessKey
    ///   stsSessionToken — STS SessionToken
    ///   userId        — 用户 ID（X-Storage-U header 用）
    ///   extension     — 视频扩展名（mp4 / mov 等）
    ///   onChunkProgress — 分片上传进度回调 (currentChunk, totalChunks, uploadedBytes, totalBytes)
    ///
    /// 返回 video_id + 视频元数据，失败返回 null（取消照常上抛）。
    /// </summary>
    public async Task<DouyinUploadResult?> UploadAsync(
        string filePath,
        string stsAccessKey,
        string stsSecretKey,
        string stsSessionToken,
        string userId,
        string? extension = null,
        Func<int, int, long, long, Task>? onChunkProgress = null,
        CancellationToken ct = default)
    {
        var result = await _engine.UploadAsync(
            filePath, stsAccessKey, stsSecretKey, stsSessionToken, userId,
            extension, onChunkProgress, ct);

        return result is null
            ? null
            : new DouyinUploadResult(
                VideoId: result.VideoId,
                Width: result.Width,
                Height: result.Height,
                Duration: result.Duration,
                Codec: result.Codec,
                Uri: result.Uri);
    }
}

/// <summary>
/// 上传完成结果 — CommitUploadInner 返回的视频信息（抖音对外类型，保持原字段名）。
/// </summary>
public sealed record DouyinUploadResult(
    string VideoId,
    int Width,
    int Height,
    double Duration,
    string? Codec,
    string? Uri
);
