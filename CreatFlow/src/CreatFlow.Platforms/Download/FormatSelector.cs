namespace CreatFlow.Platforms.Download;

/// <summary>
/// 选流纯函数（--selftest 直接断言）。
///
/// 设计取舍：不实现 yt-dlp 的格式表达式 DSL —— 引擎只服务国内几个平台，
/// 五种策略覆盖「最高画质 / 指定上限 / 只要音频 / 体积优先」即可，少一层解析器少一类 bug。
/// </summary>
public static class FormatSelector
{
    public static FormatSelection Select(
        IReadOnlyList<MediaFormat> formats,
        FormatStrategy strategy,
        int? maxHeight = null)
    {
        // DRM 流不给用户「看起来能下但必然失败」的选项
        var usable = formats.Where(f => !f.HasDrm).ToList();
        if (usable.Count == 0)
            throw new DownloadException(DownloadErrorKind.DrmProtected, "没有可下载的媒体流（全部受 DRM 保护或为空）");

        return strategy switch
        {
            FormatStrategy.AudioOnly => AudioOnly(usable),
            FormatStrategy.Smallest => Smallest(usable),
            FormatStrategy.MaxHeight => BestQuality(ApplyHeightCap(usable, maxHeight)),
            FormatStrategy.BestQuality => BestQuality(usable),
            _ => BestMerged(usable),
        };
    }

    /// <summary>高度上限过滤：**高度未知的流保留**（宁可下载后再判，也别把唯一可用的流筛掉）。</summary>
    private static List<MediaFormat> ApplyHeightCap(List<MediaFormat> formats, int? maxHeight)
    {
        if (maxHeight is not > 0) return formats;
        var capped = formats.Where(f => f.IsAudioOnly || f.Height <= 0 || f.Height <= maxHeight.Value).ToList();
        // 全被筛掉时退回全集 —— 平台只给了高码率一种流时，宁可变通也不要「无法下载」
        return capped.Count > 0 ? capped : formats;
    }

    private static FormatSelection AudioOnly(List<MediaFormat> formats)
    {
        var audio = formats.Where(f => f.IsAudioOnly)
            .OrderByDescending(f => f.SizeBytes ?? 0)
            .ThenByDescending(f => f.Url.Length)
            .FirstOrDefault();
        if (audio is null)
            throw new DownloadException(DownloadErrorKind.Unsupported, "该作品没有独立音频流");
        return new FormatSelection(audio, null);
    }

    /// <summary>音视频合一优先（一次下载、不用 ffmpeg，失败面最小）</summary>
    private static FormatSelection BestMerged(List<MediaFormat> formats)
    {
        var merged = formats.Where(f => f.HasAudio && !f.IsAudioOnly).ToList();
        if (merged.Count > 0)
            return new FormatSelection(Best(merged), null);

        return BestQuality(formats);
    }

    private static FormatSelection BestQuality(List<MediaFormat> formats)
    {
        var videos = formats.Where(f => !f.IsAudioOnly).ToList();
        if (videos.Count == 0)
        {
            // 只有音频流的作品（播客类）按音频处理
            return AudioOnly(formats);
        }

        var video = Best(videos);
        if (video.HasAudio) return new FormatSelection(video, null);

        // 只认**纯音频流**做音轨：拿音视频合一的流当音频会得到「视频+视频」的错误合并
        var audio = formats.Where(f => f.IsAudioOnly)
            .OrderByDescending(f => f.SizeBytes ?? 0)
            .FirstOrDefault();

        // 平台只给了纯视频流时先下视频 —— 没有音频也比重试到死好，由调用方提示用户
        return new FormatSelection(video, audio);
    }

    private static FormatSelection Smallest(List<MediaFormat> formats)
    {
        // 体积未知时用分辨率兜底排序（低分辨率通常更小）
        var merged = formats.Where(f => f.HasAudio && !f.IsAudioOnly).ToList();
        if (merged.Count > 0)
            return new FormatSelection(merged
                .OrderBy(f => f.SizeBytes ?? long.MaxValue)
                .ThenBy(f => f.Height)
                .First(), null);

        var videos = formats.Where(f => !f.IsAudioOnly)
            .OrderBy(f => f.SizeBytes ?? long.MaxValue)
            .ThenBy(f => f.Height)
            .ToList();
        if (videos.Count == 0) return AudioOnly(formats);

        var video = videos[0];
        if (video.HasAudio) return new FormatSelection(video, null);

        var audio = formats.Where(f => f.IsAudioOnly)
            .OrderBy(f => f.SizeBytes ?? long.MaxValue)
            .FirstOrDefault();
        return new FormatSelection(video, audio);
    }

    /// <summary>
    /// 画质排序：高度 → 宽度 → 体积 → 直链优先（同画质下 Direct 免合并、失败面更小）。
    /// </summary>
    private static MediaFormat Best(IEnumerable<MediaFormat> formats) =>
        formats
            .OrderByDescending(f => f.Height)
            .ThenByDescending(f => f.Width)
            .ThenByDescending(f => f.SizeBytes ?? 0)
            .ThenBy(f => f.Kind == StreamKind.Direct ? 0 : 1)
            .First();

    /// <summary>估算体积（分离流要相加）—— 预览表与磁盘空间检查用</summary>
    public static long? EstimateBytes(FormatSelection selection)
    {
        if (selection.Video.SizeBytes is null) return null;
        if (selection.Audio?.SizeBytes is null) return selection.Video.SizeBytes;
        return selection.Video.SizeBytes + selection.Audio.SizeBytes;
    }
}
