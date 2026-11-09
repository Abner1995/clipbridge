using CreatFlow.Platforms.Abstractions;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手平台能力声明。
///
/// 快手合集能力矩阵：
///   ✅ 列表 — POST /rest/cp/works/v2/collection/list
///   ✅ 创建 — POST /rest/cp/works/v2/collection/add
///   ✅ 编辑 — POST /rest/cp/works/v2/collection/update
///   ✅ 删除 — POST /rest/cp/works/v2/collection/delete
///   ❌ 置顶 — 不支持合集置顶
///   ❌ 封面 — 合集创建不需要单独上传封面
///
/// 视频能力矩阵：
///   ✅ 上传 — CDN 直传（cp.kuaishou.com CDN endpoints）
///   ✅ 发布 — POST /rest/cp/works/v2/video/pc/submit
///   ✅ 列表 — POST /rest/cp/works/v2/video/pc/photo/list
///   ✅ 删除 — POST /rest/cp/works/v2/video/pc/delete
/// </summary>
public sealed class KuaishouCapabilities : IPlatformCapabilities
{
    public bool CanCreateCollection => true;
    public bool CanUpdateCollection => true;
    public bool CanDeleteCollection => true;
    public bool CanPinCollection => false;       // 快手无合集置顶
    public bool CanSetCover => false;             // 合集不需要单独上传封面

    public bool CanUploadVideo => true;
    public bool CanPublishVideo => true;
    public bool CanDeleteVideo => true;
    public bool CanListVideos => true;

    public bool CanProbeSession => true;          // 可通过 userInfo 探活
    public bool CanRefreshToken => false;         // Cookie 过期需重新扫码
}
