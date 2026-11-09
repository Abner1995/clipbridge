namespace CreatFlow.Platforms.Toutiao;

using CreatFlow.Platforms.Core.Vod;

/// <summary>
/// 头条 VOD 的 SpaceName（接口说明书 §4.2.1）：
/// 横版视频 <c>middlevideo_toutiao</c>、竖版小视频 <c>short_video_toutiao</c>。
/// 两个空间决定后续 ApplyUploadInner / CommitUploadInner 的 bucket 与分片策略。
/// </summary>
public static class ToutiaoSpaceName
{
    public const string Horizontal = "middlevideo_toutiao";
    public const string Vertical = "short_video_toutiao";

    /// <summary>按视频方向选空间（竖版 = 高 &gt; 宽）</summary>
    public static string For(bool portrait) => portrait ? Vertical : Horizontal;

    /// <summary>按宽高选空间；未知（0）按横版处理（与发布默认形态一致）</summary>
    public static string ForSize(int width, int height) =>
        height > width ? Vertical : Horizontal;
}

/// <summary>
/// 头条视频上传 —— ByteDance VOD 通用引擎的**薄包装**（阶段 5.1）。
///
/// 与抖音的差异全部来自抓包（接口说明书 §4.2.2）：
///   · <c>SpaceName</c> = middlevideo_toutiao（横版）/ short_video_toutiao（竖版）；
///   · Query 带 <c>app_id=1231</c>、<c>user_id</c>、<c>EnOID=1</c>。
///
/// 签名（AWS SigV4 + 上一步 STS）与 TOS 分片协议复用 <see cref="BdvodUploadService"/>。
/// </summary>
public sealed class ToutiaoUploadService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<ToutiaoUploadService>? _logger;

    /// <summary>分片大小（默认 3 MB，与抖音同口径；头条长视频分片策略待真机标定）</summary>
    public int ChunkSize { get; set; } = 3 * 1024 * 1024;

    public ToutiaoUploadService(
        PlatformOptions options,
        ILogger<ToutiaoUploadService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 完整上传一个视频到头条 VOD。
    ///
    /// <paramref name="userId"/> = <c>get_user_info</c> 的 <c>user_id</c>
    /// （**不是** page 的 media.id；与图片 TOS 的 X-Storage-U 同一口径）。
    /// </summary>
    public async Task<ToutiaoUploadResult?> UploadAsync(
        string filePath,
        string stsAccessKey,
        string stsSecretKey,
        string stsSessionToken,
        string userId,
        bool portrait,
        string? spaceName = null,
        string? extension = null,
        Func<int, int, long, long, Task>? onChunkProgress = null,
        CancellationToken ct = default)
    {
        var engine = new BdvodUploadService(
            _options,
            new BdvodUploadSettings(
                VodHost: _options.ToutiaoVodHost,
                // 优先用凭证接口返回的 spaceName（竖版 auth_token 给 short_video_toutiao）；
                // 缺省按方向兜底
                SpaceName: string.IsNullOrWhiteSpace(spaceName)
                    ? ToutiaoSpaceName.For(portrait)
                    : spaceName,
                AppId: _options.ToutiaoAppId,
                SendUserIdQuery: true,
                SendEnOid: true,
                ChunkSizeBytes: ChunkSize),
            _logger);

        var result = await engine.UploadAsync(
            filePath, stsAccessKey, stsSecretKey, stsSessionToken, userId,
            extension, onChunkProgress, ct);

        return result is null
            ? null
            : new ToutiaoUploadResult(
                VideoId: result.VideoId,
                Width: result.Width,
                Height: result.Height,
                Duration: result.Duration,
                Codec: result.Codec,
                Uri: result.Uri);
    }
}

/// <summary>头条视频上传结果（CommitUploadInner 返回的 Vid / 元数据）。</summary>
public sealed record ToutiaoUploadResult(
    string VideoId,
    int Width,
    int Height,
    double Duration,
    string? Codec,
    string? Uri
);
