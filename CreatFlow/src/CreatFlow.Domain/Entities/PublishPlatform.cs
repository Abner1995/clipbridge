namespace CreatFlow.Domain.Entities;

/// <summary>
/// 平台发布记录 — PublishTask 聚合的子实体。
///
/// 每个平台 × 每个账号生成一条记录，独立追踪发布进度。
/// 不是聚合根，只能通过 PublishTask 聚合根访问。
///
/// 对标 x_publish_platforms 表。
/// </summary>
public class PublishPlatform : Aggregate<PublishPlatformId>
{
    // ── 关联 ──

    /// <summary>所属发布任务（关联 x_publish_tasks.id）</summary>
    public PublishTaskId PublishTaskId { get; private set; } = default!;

    /// <summary>平台标识（douyin / kuaishou / xiaohongshu / wechatvideo）</summary>
    public string PlatformKey { get; private set; } = default!;

    /// <summary>用哪个平台账号发布（关联 x_platform_sessions.id）</summary>
    public Guid? PlatformSessionId { get; private set; }

    // ── 平台级覆盖字段（不为 NULL 时覆盖 PublishTask 的通用值）──

    /// <summary>平台侧标题</summary>
    public string? Title { get; private set; }

    /// <summary>平台侧简介</summary>
    public string? Description { get; private set; }

    /// <summary>封面图本地路径</summary>
    public string? CoverFilePath { get; private set; }

    /// <summary>封面上传后 CDN URL</summary>
    public string? CoverUrl { get; private set; }

    /// <summary>关联的合集 ID（关联 x_collections.id，平台可用时填）</summary>
    public Guid? CollectionId { get; private set; }

    /// <summary>平台特有参数 JSON — visibility、declaration、location、tags、save_permission 等</summary>
    public string? ExtraDataJson { get; private set; }

    // ── 发布进度 ──

    /// <summary>发布状态：0=Pending 1=Uploading 2=Transcoding 3=Publishing 4=Published 5=Failed</summary>
    public PublishPlatformStatus Status { get; private set; }

    /// <summary>进度百分比 0-100</summary>
    public int ProgressPct { get; private set; }

    /// <summary>失败原因（status = Failed 时填充）</summary>
    public string? ErrorMsg { get; private set; }

    /// <summary>重试次数</summary>
    public int RetryCount { get; private set; }

    /// <summary>
    /// 是否已从上传队列中隐藏。
    /// Published 状态的记录被用户从队列中移除时设为 true（软删除），
    /// 记录仍保留在 DB 中供视频管理页面查看。
    /// </summary>
    public bool HiddenInQueue { get; private set; }

    // ── 结果 ──

    /// <summary>平台返回的视频 ID</summary>
    public string? PlatformVideoId { get; private set; }

    /// <summary>平台返回的视频链接</summary>
    public string? PlatformVideoUrl { get; private set; }

    /// <summary>实际发布时间</summary>
    public DateTime? PublishedAt { get; private set; }

    // ── ctor ──

    private PublishPlatform() { }

    // ═══ Factory ═══

    public static PublishPlatform Create(
        PublishTaskId publishTaskId,
        PublishPlatformId id,
        string platformKey,
        Guid? platformSessionId = null,
        string? title = null,
        string? description = null,
        string? coverFilePath = null,
        Guid? collectionId = null,
        string? extraDataJson = null)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(platformKey);

        var now = Clock.Now;
        return new PublishPlatform
        {
            Id = id,
            PublishTaskId = publishTaskId,
            PlatformKey = platformKey,
            PlatformSessionId = platformSessionId,
            Title = title,
            Description = description,
            CoverFilePath = coverFilePath,
            CollectionId = collectionId,
            ExtraDataJson = extraDataJson,
            Status = PublishPlatformStatus.Pending,
            ProgressPct = 0,
            RetryCount = 0,
            CreatedAt = now,
            LastModified = now,
        };
    }

    // ═══ Behaviors ═══

    /// <summary>开始上传视频</summary>
    public void StartUpload()
    {
        Status = PublishPlatformStatus.Uploading;
        ProgressPct = 0;
        LastModified = Clock.Now;
    }

    /// <summary>更新上传进度</summary>
    public void UpdateProgress(int pct)
    {
        ProgressPct = Math.Clamp(pct, 0, 100);
        LastModified = Clock.Now;
    }

    /// <summary>进入转码等待</summary>
    public void StartTranscoding()
    {
        Status = PublishPlatformStatus.Transcoding;
        ProgressPct = 60;
        LastModified = Clock.Now;
    }

    /// <summary>进入发布提交</summary>
    public void StartPublishing()
    {
        Status = PublishPlatformStatus.Publishing;
        ProgressPct = 80;
        LastModified = Clock.Now;
    }

    /// <summary>发布成功，记录平台返回的视频 ID</summary>
    public void MarkPublished(string platformVideoId, string? platformVideoUrl = null)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(platformVideoId);

        PlatformVideoId = platformVideoId;
        PlatformVideoUrl = platformVideoUrl;
        Status = PublishPlatformStatus.Published;
        ProgressPct = 100;
        PublishedAt = Clock.Now;
        ErrorMsg = null;
        LastModified = Clock.Now;
    }

    /// <summary>发布失败，记录错误原因（可重试）</summary>
    public void MarkFailed(string errorMsg)
    {
        ErrorMsg = errorMsg;
        Status = PublishPlatformStatus.Failed;
        LastModified = Clock.Now;
    }

    /// <summary>重试：重置为 Pending 状态</summary>
    public void Retry()
    {
        Status = PublishPlatformStatus.Pending;
        ErrorMsg = null;
        ProgressPct = 0;
        RetryCount++;
        LastModified = Clock.Now;
    }

    /// <summary>更新封面上传后的 CDN URL</summary>
    public void SetCoverUrl(string coverUrl)
    {
        CoverUrl = coverUrl;
        LastModified = Clock.Now;
    }

    /// <summary>更新平台特有参数</summary>
    public void SetExtraData(string extraDataJson)
    {
        ExtraDataJson = extraDataJson;
        LastModified = Clock.Now;
    }

    /// <summary>合并 extra_data_json 字段（新值覆盖同 key，不删已有 key）</summary>
    public void MergeExtraData(string extraDataJson)
    {
        if (string.IsNullOrWhiteSpace(ExtraDataJson))
        {
            ExtraDataJson = extraDataJson;
        }
        else if (!string.IsNullOrWhiteSpace(extraDataJson))
        {
            try
            {
                var existing = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(ExtraDataJson)
                    ?? [];
                var incoming = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(extraDataJson)
                    ?? [];
                foreach (var kv in incoming)
                    existing[kv.Key] = kv.Value;
                ExtraDataJson = System.Text.Json.JsonSerializer.Serialize(existing);
            }
            catch
            {
                // JSON 解析失败则保留原有值
            }
        }
        LastModified = Clock.Now;
    }

    /// <summary>从上传队列中隐藏（软删除，保留 DB 记录供视频管理页查看）</summary>
    public void HideFromQueue()
    {
        HiddenInQueue = true;
        LastModified = Clock.Now;
    }

}

/// <summary>
/// 平台发布状态 —
///   Pending     = 0 — 等待处理
///   Uploading   = 1 — 上传视频中
///   Transcoding = 2 — 等待平台转码
///   Publishing  = 3 — 提交发布中
///   Published   = 4 — 发布成功
///   Failed      = 5 — 发布失败
/// </summary>
public enum PublishPlatformStatus
{
    Pending = 0,
    Uploading = 1,
    Transcoding = 2,
    Publishing = 3,
    Published = 4,
    Failed = 5,
}
