namespace CreatFlow.Platforms.Abstractions;

/// <summary>
/// 平台能力声明 — 声明式表达每个平台支持/不支持的功能。
///
/// 不用 <c>is ICollectionDelete</c> 运行时判断，
/// 用声明式属性，编译期安全 + 一眼看懂平台能力边界。
/// </summary>
public interface IPlatformCapabilities
{
    // ── 合集 ──
    bool CanCreateCollection { get; }
    bool CanUpdateCollection { get; }
    bool CanDeleteCollection { get; }
    bool CanPinCollection { get; }
    bool CanSetCover { get; }             // 创建时是否需要/支持设置封面

    // ── 视频 ──
    bool CanUploadVideo { get; }
    bool CanPublishVideo { get; }
    bool CanDeleteVideo { get; }
    bool CanListVideos { get; }

    // ── 认证 ──
    bool CanProbeSession { get; }         // 是否支持探活检测
    bool CanRefreshToken { get; }         // 是否支持 token 自动刷新
}
