namespace CreatFlow.Platforms.Abstractions.Collections;

/// <summary>
/// 置顶合集 — 将合集置顶到个人主页。
/// 仅视频号支持此功能。
/// </summary>
public interface ICollectionPin
{
    /// <summary>
    /// 置顶 / 取消置顶合集。
    /// </summary>
    /// <param name="cookie">平台 Cookie 字符串</param>
    /// <param name="platformCollectionId">平台合集 ID</param>
    /// <param name="pinned">true=置顶, false=取消置顶</param>
    /// <param name="ct">取消令牌</param>
    Task<PinCollectionResult> PinAsync(
        string cookie,
        string platformCollectionId,
        bool pinned,
        CancellationToken ct = default);
}

/// <summary>
/// 置顶合集的结果。
/// </summary>
public sealed record PinCollectionResult(
    bool Success,
    string? ErrorMessage
);
