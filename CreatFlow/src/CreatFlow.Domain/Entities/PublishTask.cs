namespace CreatFlow.Domain.Entities;

/// <summary>
/// 发布任务 — 一次"提交发布"的聚合根。
///
/// 一个视频可以发布到多个平台（每个平台每个账号一条 x_publish_platforms 记录）。
/// 聚合根保证一致性：提交时要么所有平台记录都创建成功，要么全部回滚。
///
/// 对标 x_publish_tasks 表。
/// </summary>
public class PublishTask : Aggregate<PublishTaskId>
{
    // ── 基础信息 ──

    /// <summary>所属会员</summary>
    public Guid MemberId { get; private set; }

    /// <summary>本地视频文件路径</summary>
    public string VideoFilePath { get; private set; } = default!;

    /// <summary>原始文件名</summary>
    public string VideoFileName { get; private set; } = default!;

    /// <summary>文件大小（字节）</summary>
    public long VideoFileSize { get; private set; }

    /// <summary>SHA256（去重 + 完整性校验）</summary>
    public string VideoHash { get; private set; } = default!;

    /// <summary>视频时长（秒）</summary>
    public int? VideoDurationS { get; private set; }

    /// <summary>视频分辨率宽</summary>
    public int? VideoWidth { get; private set; }

    /// <summary>视频分辨率高</summary>
    public int? VideoHeight { get; private set; }

    // ── 通用发布参数（各平台可覆盖）──

    /// <summary>封面图本地路径</summary>
    public string? CoverFilePath { get; private set; }

    /// <summary>通用标题</summary>
    public string? CommonTitle { get; private set; }

    /// <summary>通用简介</summary>
    public string? CommonDesc { get; private set; }

    /// <summary>定时发布时间（null = 立即发布）</summary>
    public DateTime? ScheduledAt { get; private set; }

    // ── 聚合根状态 ──

    /// <summary>0=Pending 1=Processing 2=Completed 3=PartialFailed 4=Failed</summary>
    public PublishTaskStatus Status { get; private set; }

    // ── 子实体 ──

    private readonly List<PublishPlatform> _platforms = [];
    /// <summary>各平台发布记录（只读）</summary>
    public IReadOnlyList<PublishPlatform> Platforms => _platforms.AsReadOnly();

    // ── ctor ──

    private PublishTask() { }

    // ═══ Factory ═══

    public static PublishTask Create(
        PublishTaskId id,
        Guid memberId,
        string videoFilePath,
        string videoFileName,
        long videoFileSize,
        string videoHash,
        int? videoDurationS = null,
        int? videoWidth = null,
        int? videoHeight = null,
        string? coverFilePath = null,
        string? commonTitle = null,
        string? commonDesc = null,
        DateTime? scheduledAt = null)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(videoFilePath);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(videoFileName);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(videoHash);
        if (memberId == Guid.Empty)
            throw new DomainException("MemberId cannot be empty.");

        var now = Clock.Now;
        return new PublishTask
        {
            Id = id,
            MemberId = memberId,
            VideoFilePath = videoFilePath,
            VideoFileName = videoFileName,
            VideoFileSize = videoFileSize,
            VideoHash = videoHash,
            VideoDurationS = videoDurationS,
            VideoWidth = videoWidth,
            VideoHeight = videoHeight,
            CoverFilePath = coverFilePath,
            CommonTitle = commonTitle,
            CommonDesc = commonDesc,
            ScheduledAt = scheduledAt,
            Status = PublishTaskStatus.Pending,
            CreatedAt = now,
            LastModified = now,
        };
    }

    // ═══ Behaviors ═══

    /// <summary>
    /// 添加一个平台的发布记录。
    /// 只能在 Submit 前调用（Pending 状态允许修改）。
    /// </summary>
    public void AddPlatform(
        string platformKey,
        Guid? platformSessionId = null,
        string? title = null,
        string? description = null,
        string? coverFilePath = null,
        Guid? collectionId = null,
        string? extraDataJson = null)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(platformKey);

        // 不允许同一平台同一账号重复添加
        var duplicate = _platforms.Any(p =>
            p.PlatformKey == platformKey && p.PlatformSessionId == platformSessionId);
        if (duplicate)
            throw new DomainException(
                $"Platform '{platformKey}' with session '{platformSessionId}' already added.");

        var platform = PublishPlatform.Create(
            Id,
            PublishPlatformId.New(),
            platformKey,
            platformSessionId,
            title,
            description,
            coverFilePath,
            collectionId,
            extraDataJson);

        _platforms.Add(platform);
        LastModified = Clock.Now;
    }

    /// <summary>
    /// 移除一个平台的发布记录（只在 Pending 状态可操作）。
    /// </summary>
    public void RemovePlatform(PublishPlatformId platformId)
    {
        var platform = _platforms.FirstOrDefault(p => p.Id == platformId);
        if (platform is null)
            return;

        _platforms.Remove(platform);
        LastModified = Clock.Now;
    }

    /// <summary>
    /// 提交发布任务。
    ///
    /// 校验至少有一个平台记录 → 触发 PublishTaskSubmittedEvent。
    /// 提交后不再允许增删平台。
    /// </summary>
    public void Submit()
    {
        if (_platforms.Count == 0)
            throw new DomainException("Must add at least one platform before submitting.");

        Status = PublishTaskStatus.Processing;
        LastModified = Clock.Now;

        AddDomainEvent(new PublishTaskSubmittedEvent(Id, MemberId, _platforms.Count));
    }

    /// <summary>
    /// 根据子实体状态自动推断聚合根的整体状态。
    ///
    /// 逻辑：
    ///   - 全部 Published → Completed
    ///   - 全部 Failed → Failed
    ///   - 有 Published 也有 Failed → PartialFailed
    ///   - 其他 → Processing
    /// </summary>
    public void UpdateOverallStatus()
    {
        var published = _platforms.Count(p => p.Status == PublishPlatformStatus.Published);
        var failed = _platforms.Count(p => p.Status == PublishPlatformStatus.Failed);
        var total = _platforms.Count;

        if (published == total)
            Status = PublishTaskStatus.Completed;
        else if (failed == total)
            Status = PublishTaskStatus.Failed;
        else if (published > 0 || failed > 0)
            Status = PublishTaskStatus.PartialFailed;
        else
            Status = PublishTaskStatus.Processing;

        LastModified = Clock.Now;
    }

    /// <summary>更新视频元数据（从 FFmpeg 解析后）</summary>
    public void SetVideoMetadata(int durationS, int width, int height)
    {
        VideoDurationS = durationS;
        VideoWidth = width;
        VideoHeight = height;
        LastModified = Clock.Now;
    }
}

/// <summary>
/// 发布任务整体状态 —
///   Pending       = 0 — 等待提交
///   Processing    = 1 — 发布进行中
///   Completed     = 2 — 全部成功
///   PartialFailed = 3 — 部分成功
///   Failed        = 4 — 全部失败
/// </summary>
public enum PublishTaskStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    PartialFailed = 3,
    Failed = 4,
}
