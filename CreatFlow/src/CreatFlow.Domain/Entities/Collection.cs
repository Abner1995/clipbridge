namespace CreatFlow.Domain.Entities;

/// <summary>
/// 合集（逻辑合集）— 用户创建的合集元数据，不绑定任何平台。
///
/// 一个合集可以同步到多个平台（抖音/快手/视频号/小红书），
/// 每个平台各建一条 CollectionPlatform 记录，追踪同步状态。
///
/// 快手特殊处理：
///   - 可以创建合集（同步到平台）
///   - 但发布视频时快手 API 没有合集字段，发布请求中跳过
/// </summary>
public class Collection : Aggregate<CollectionId>
{
    /// <summary>所属会员</summary>
    public Guid MemberId { get; private set; }

    /// <summary>
    /// 合集内容类型（2026-11-05：头条分文章/视频两套合集）。
    /// 文章合集仅头条，创建后不可变更；老数据默认视频。
    /// </summary>
    public CollectionContentType ContentType { get; private set; } = CollectionContentType.Video;

    /// <summary>合集标题（所有平台共用）</summary>
    public string Title { get; private set; } = default!;

    /// <summary>合集简介（抖音/小红书使用；视频号/快手留空）</summary>
    public string? Description { get; private set; }

    /// <summary>封面图本地绝对路径（用户选择时写入，永不修改，用于重试）</summary>
    public string? CoverImagePath { get; private set; }

    /// <summary>封面图上传后的 CDN URL / TOS key（上传成功后写入，重试时会更新）</summary>
    public string? CoverImageUrl { get; private set; }

    // ── ctor ──

    private Collection() { }

    // ═══ Factory ═══

    public static Collection Create(
        CollectionId id,
        Guid memberId,
        string title,
        string? description = null,
        string? coverImagePath = null,
        CollectionContentType contentType = CollectionContentType.Video)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(title);

        var now = Clock.Now;
        return new Collection
        {
            Id = id,
            MemberId = memberId,
            ContentType = contentType,
            Title = title,
            Description = description,
            CoverImagePath = coverImagePath,
            CreatedAt = now,
            LastModified = now,
        };
    }

    // ═══ Behaviors ═══

    /// <summary>
    /// 修改合集元数据（本地路径级别，不改 CDN URL）。
    /// **内容类型不可改**：视频 AlbumId 与文章 collection_id 是平台侧两个不同对象，
    /// 切换类型会让已有平台记录错配；要换类型只能新建合集。
    /// </summary>
    public void Update(string title, string? description, string? coverImagePath)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(title);
        Title = title;
        Description = description;
        CoverImagePath = coverImagePath;
        LastModified = Clock.Now;
    }

    /// <summary>更新封面图 CDN URL（平台同步完成后，用 TOS key / CDN URL 回填）</summary>
    public void SetCoverImageUrl(string url)
    {
        CoverImageUrl = url;
        LastModified = Clock.Now;
    }
}
