namespace CreatFlow.Platforms.Abstractions.Collections;

/// <summary>
/// 合集列表查询 — 获取平台上的合集列表。
/// 所有平台都实现此接口。
/// </summary>
public interface ICollectionList
{
    /// <summary>
    /// 分页获取合集列表。
    /// </summary>
    /// <param name="cookie">平台 Cookie 字符串</param>
    /// <param name="cursor">分页游标，首次传 0</param>
    /// <param name="count">每页数量</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>合集列表和分页信息</returns>
    Task<CollectionListResult> ListAsync(
        string cookie,
        long cursor = 0,
        int count = 12,
        CancellationToken ct = default);
}

/// <summary>
/// 合集列表查询的结果。
/// </summary>
public sealed record CollectionListResult(
    List<CollectionInfo> Items,
    bool HasMore,
    long NextCursor
);

/// <summary>
/// 平台返回的合集信息。
/// </summary>
public sealed record CollectionInfo(
    string PlatformCollectionId,   // 平台合集 ID（mix_id）
    string Title,                  // 合集标题（mix_name）
    string? Description,           // 合集简介（mix_desc）
    string? CoverUrl,              // 封面图 URL
    int Status,                    // 状态码
    string StatusText,             // 状态文本
    int VideoCount,                // 包含的视频数
    DateTime? CreatedAt,           // 创建时间
    DateTime? UpdatedAt,           // 修改时间
    int ContentType = 0            // 0=视频 1=文章（头条文章合集；其余平台恒 0）
);
