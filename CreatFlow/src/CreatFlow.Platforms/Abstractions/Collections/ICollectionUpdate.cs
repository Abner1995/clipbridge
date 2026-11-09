namespace CreatFlow.Platforms.Abstractions.Collections;

/// <summary>
/// 编辑合集 — 更新平台上已有合集的元数据（标题、简介、封面等）。
/// 目前抖音、快手、小红书、视频号都支持编辑。
/// </summary>
public interface ICollectionUpdate
{
    /// <summary>
    /// 更新平台上的合集。
    /// </summary>
    /// <param name="cookie">平台 Cookie 字符串</param>
    /// <param name="request">更新参数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>更新结果</returns>
    Task<UpdateCollectionResult> UpdateAsync(
        string cookie,
        UpdateCollectionInfo request,
        CancellationToken ct = default);
}

/// <summary>
/// 更新合集的参数。
/// </summary>
public sealed record UpdateCollectionInfo(
    string PlatformCollectionId,   // 平台合集 ID（mix_id）— 必填
    string Title,                  // 新标题
    string? Description,           // 新简介
    string? CoverTosKey,           // 新封面 TOS 路径（抖音；头条没换封面时传 null，服务内部查详情回填）
    string? CsrfToken,
    string? MsToken,
    string? ABogus,
    int CoverWidth = 0,            // 封面原图宽（头条）
    int CoverHeight = 0,           // 封面原图高（头条）
    string? CoverUrl = null,       // 封面签名预览 URL（头条文章合集 cover_image.url）
    string? CoverOriginUri = null  // 封面处理前 TOS key（头条文章合集 cover_image.originUri）
);

/// <summary>
/// 更新合集的结果。
/// </summary>
public sealed record UpdateCollectionResult(
    bool Success,
    string? ErrorMessage
);
