namespace CreatFlow.NewApp.Services;

/// <summary>统一封面来源（2.5.7）。</summary>
public enum PublishCoverSource
{
    /// <summary>没有可用封面（没选图，抽帧也失败）—— 调用方按平台默认封面继续</summary>
    None = 0,

    /// <summary>用户在发布页选的封面图</summary>
    Custom = 1,

    /// <summary>自动从视频抽的一帧（发布质量，不是 320px 卡片缩略图）</summary>
    FirstFrame = 2,
}

/// <summary>统一封面解析结果。</summary>
/// <param name="Path">封面图路径；<see cref="PublishCoverSource.None"/> 时为 null</param>
/// <param name="Source">来源（决定日志文案和平台侧取值规则）</param>
public sealed record PublishCover(string? Path, PublishCoverSource Source)
{
    /// <summary>
    /// 有没有真正可上传的文件 —— 路径非空**且确实存在**。
    /// 上传前再核一次：抽帧是缓存资产，极端情况下（用户清缓存/磁盘错误）可能已经不在。
    /// </summary>
    public bool HasFile => Exists(Path);

    internal static bool Exists(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
        catch { return false; }
    }
}

/// <summary>
/// 统一封面解析（进度表 2.5.7）—— **六个平台同一条规则**：
/// <code>
/// 用户选的封面图（文件存在）            → 用它
/// 否则自动从视频抽帧                   → 抽出的帧（发布质量）
///    · 填了「封面帧」→ 按该时间点抽（0 = 第 0 秒）
///    · 留空         → 0.5 秒（0 秒处不少视频是黑场，与卡片缩略图同口径）
/// 抽帧失败（缺 ffmpeg / 视频读不了）     → None（平台默认封面继续，不阻断）
/// </code>
///
/// 背景：视频号此前就有「首帧兜底」（<c>ResolveWechatCoverAsync</c>，用的是 320px 卡片缩略图），
/// 头条 / B站 的封面是**平台必填**，抖音 / 快手 / 小红书虽然平台会自己取首帧，但那是
/// **视频第 0 秒**（黑场常见）、且各平台取的不一定是同一帧。2.5.7 把这条规则抽成共用工具：
/// 一次抽帧、四个平台用同一张图，用户没选封面也不会出现「一块黑」或「头条直接发不出去」。
///
/// ⚠️ 抽帧失败对抖音/快手/小红书是**非致命**的（平台默认封面照发）；视频号另在管线里退回
/// 1×1 占位图（封面必填，空则 300801）。头条 / B站 的必填校验在阶段 5 / 4 接入时套同一份。
/// </summary>
public static class PublishCoverResolver
{
    /// <summary>
    /// 纯函数：在「用户封面」与「抽出的首帧」之间做选择（优先级 = 用户封面 &gt; 首帧 &gt; 无）。
    /// 存在性由调用方传入，便于自检不碰磁盘。
    /// </summary>
    public static PublishCover Choose(
        string? customCoverPath, bool customExists,
        string? firstFramePath, bool firstFrameExists)
    {
        if (customExists && !string.IsNullOrWhiteSpace(customCoverPath))
            return new PublishCover(customCoverPath, PublishCoverSource.Custom);

        if (firstFrameExists && !string.IsNullOrWhiteSpace(firstFramePath))
            return new PublishCover(firstFramePath, PublishCoverSource.FirstFrame);

        return new PublishCover(null, PublishCoverSource.None);
    }

    /// <summary>
    /// 解析封面：用户封面 → 自动抽帧 → 无。
    ///
    /// <paramref name="coverFrameMs"/> 是发布页的「封面帧」（毫秒）：null = 没填（抽 0.5 秒），
    /// 0 = 明确第 0 秒。抽帧失败只打警告不抛 —— 除用户主动取消
    /// （<see cref="OperationCanceledException"/> 会让队列把这条标成「已取消」而不是「发布失败」）。
    /// </summary>
    public static async Task<PublishCover> ResolveAsync(
        string videoPath, string? customCoverPath, long? coverFrameMs,
        VideoMetaProbe? probe, CancellationToken ct = default)
    {
        if (PublishCover.Exists(customCoverPath))
            return new PublishCover(customCoverPath, PublishCoverSource.Custom);

        if (probe is null)
        {
            // 生产三处装配（发布页 / 上传队列 / 自动发布提交器）都带了 MetaProbe；
            // 只有自检或极端装配才会走到这里 —— 按「无封面」继续，不阻断发布。
            Log.Debug("[Publish] 没有元数据探测器，跳过自动抽封面帧：{Video}", videoPath);
            return new PublishCover(null, PublishCoverSource.None);
        }

        string? firstFrame = null;
        try
        {
            firstFrame = await probe.ExtractCoverFrameAsync(videoPath, coverFrameMs, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 用户暂停/删除选中：取消不是「抽帧失败」
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 自动抽封面帧失败（按无封面继续）：{Video}", videoPath);
        }

        var cover = Choose(null, false, firstFrame, PublishCover.Exists(firstFrame));
        if (cover.Source == PublishCoverSource.None)
            Log.Warning("[Publish] 没有封面图且抽帧失败，按平台默认封面继续：{Video}", videoPath);
        return cover;
    }
}
