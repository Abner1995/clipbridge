namespace CreatFlow.Platforms.Abstractions.Collections;

/// <summary>
/// 创建合集 — 在平台上新建一个合集。
/// 目前抖音、快手、小红书、视频号都支持创建。
/// </summary>
public interface ICollectionCreate
{
    /// <summary>
    /// 在平台上创建合集。
    /// </summary>
    /// <param name="cookie">平台 Cookie 字符串</param>
    /// <param name="request">创建参数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>创建结果（包含平台分配的合集 ID）</returns>
    Task<CreateCollectionResult> CreateAsync(
        string cookie,
        CreateCollectionInfo request,
        CancellationToken ct = default);
}

/// <summary>
/// 创建合集的参数。
/// </summary>
public sealed record CreateCollectionInfo(
    string Title,                  // 合集名称（mix_name）— 必填
    string? Description,           // 合集简介（mix_desc）
    string? CoverTosKey,           // 封面图 TOS 路径（抖音/头条需要先上传）
    List<string>? ItemIds,         // 初始纳入的视频 item_id 列表
    string? CsrfToken,             // CSRF Token（用于有状态防护的平台）
    string? MsToken,               // 安全 SDK msToken
    string? ABogus,                // 安全 SDK a_bogus
    int CoverWidth = 0,            // 封面原图宽（头条必填；其它平台忽略）
    int CoverHeight = 0,           // 封面原图高（头条必填；其它平台忽略）
    string? CoverUrl = null,       // 封面签名预览 URL（头条文章合集 cover_image.url）
    string? CoverOriginUri = null  // 封面处理前 TOS key（头条文章合集 cover_image.originUri）
);

/// <summary>
/// 创建合集的结果。
/// </summary>
public sealed record CreateCollectionResult(
    string PlatformCollectionId,   // 平台返回的合集 ID（mix_id）
    bool Success,
    string? ErrorMessage
);
