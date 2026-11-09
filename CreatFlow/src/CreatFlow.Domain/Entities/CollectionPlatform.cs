namespace CreatFlow.Domain.Entities;

/// <summary>
/// 平台合集记录 — 逻辑合集在某个平台上的同步状态。
///
/// status 追踪：
///   Pending — 待同步到平台
///   Created — 已在平台成功创建（记录平台返回的合集 ID）
///   Failed  — 同步失败（记录错误原因，可重试）
/// </summary>
public class CollectionPlatform : Aggregate<CollectionPlatformId>
{
    /// <summary>关联 x_collections.id</summary>
    public CollectionId CollectionId { get; private set; } = default!;

    /// <summary>平台标识（douyin / kuaishou / xiaohongshu / wechatvideo）</summary>
    public string PlatformKey { get; private set; } = default!;

    /// <summary>用哪个登录会话创建的（关联 x_platform_sessions.id）</summary>
    public Guid? PlatformSessionId { get; private set; }

    /// <summary>平台返回的合集 ID（同步成功后记录）</summary>
    public string? PlatformCollectionId { get; private set; }

    /// <summary>平台侧标题（null = 继承逻辑合集的标题）</summary>
    public string? Title { get; private set; }

    /// <summary>平台侧简介（null = 继承逻辑合集的简介）</summary>
    public string? Description { get; private set; }

    /// <summary>平台侧封面（null = 继承逻辑合集的封面）</summary>
    public string? CoverImage { get; private set; }

    /// <summary>同步状态：0=Pending 1=Created 2=Failed</summary>
    public CollectionPlatformStatus Status { get; private set; }

    /// <summary>失败原因（status = Failed 时填充）</summary>
    public string? ErrorMsg { get; private set; }

    /// <summary>合集类型（账号级自定义，如"美食探店""城市攻略"）</summary>
    public string? CollectionType { get; private set; }

    /// <summary>平台特有扩展数据（JSON）— 快手展示设置等</summary>
    public string? ExtraDataJson { get; private set; }

    // ── ctor ──

    private CollectionPlatform() { }

    // ═══ Factory ═══

    public static CollectionPlatform Create(
        CollectionPlatformId id,
        CollectionId collectionId,
        string platformKey,
        Guid? platformSessionId = null,
        string? collectionType = null,
        string? title = null,
        string? description = null,
        string? coverImage = null,
        string? extraDataJson = null)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(platformKey);

        var now = Clock.Now;
        return new CollectionPlatform
        {
            Id = id,
            CollectionId = collectionId,
            PlatformKey = platformKey,
            PlatformSessionId = platformSessionId,
            CollectionType = collectionType,
            Title = title,
            Description = description,
            CoverImage = coverImage,
            ExtraDataJson = extraDataJson,
            Status = CollectionPlatformStatus.Pending,
            CreatedAt = now,
            LastModified = now,
        };
    }

    // ═══ Behaviors ═══

    /// <summary>平台同步成功，记录平台返回的合集 ID</summary>
    public void MarkCreated(string platformCollectionId)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(platformCollectionId);
        PlatformCollectionId = platformCollectionId;
        Status = CollectionPlatformStatus.Created;
        ErrorMsg = null;
        LastModified = Clock.Now;
    }

    /// <summary>平台同步失败，记录错误原因（可重试）</summary>
    public void MarkFailed(string errorMsg)
    {
        ErrorMsg = errorMsg;
        Status = CollectionPlatformStatus.Failed;
        LastModified = Clock.Now;
    }

    /// <summary>重置为待同步状态（用于重试失败的同步）</summary>
    public void ResetToPending()
    {
        Status = CollectionPlatformStatus.Pending;
        ErrorMsg = null;
        LastModified = Clock.Now;
    }

    /// <summary>更新平台侧信息（标题/简介/封面/扩展数据）</summary>
    public void UpdatePlatformInfo(
        string? title, string? description, string? coverImage, string? extraDataJson)
    {
        Title = title;
        Description = description;
        CoverImage = coverImage;
        ExtraDataJson = extraDataJson;
        LastModified = Clock.Now;
    }

}

/// <summary>
/// 平台合集同步状态
///   Pending = 0 — 待同步
///   Created = 1 — 已创建
///   Failed  = 2 — 同步失败
/// </summary>
public enum CollectionPlatformStatus
{
    Pending = 0,
    Created = 1,
    Failed = 2,
}
