namespace CreatFlow.Platforms.Abstractions.Collections;

/// <summary>
/// 删除合集 — 从平台上删除一个合集（合集内视频不受影响）。
/// 注意：小红书删除接口还要求带**当前合集名**（2026-11-05 抓包），且合集内不能有笔记；
/// 调用方拿不到名称时由实现自行查详情补齐。
/// </summary>
public interface ICollectionDelete
{
    /// <summary>
    /// 删除平台上的合集。
    /// </summary>
    /// <param name="cookie">平台 Cookie 字符串</param>
    /// <param name="request">删除参数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>删除结果</returns>
    Task<DeleteCollectionResult> DeleteAsync(
        string cookie,
        DeleteCollectionInfo request,
        CancellationToken ct = default);
}

/// <summary>
/// 删除合集的参数。
/// </summary>
public sealed record DeleteCollectionInfo(
    string PlatformCollectionId,   // 平台合集 ID（mix_id）— 必填
    string? CsrfToken,
    string? MsToken,
    string? ABogus,
    string? Title = null           // 平台当前合集名（小红书删除必填；null = 实现自行查详情）
);

/// <summary>
/// 删除合集的结果。
/// </summary>
public sealed record DeleteCollectionResult(
    bool Success,
    string? ErrorMessage
);
