using CreatFlow.Platforms.Abstractions;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书平台能力声明。
///
/// 合集能力矩阵：
///   ✅ 列表 — POST /api/sns/v1/note/collection/pc/list_v2
///   ✅ 创建 — POST /api/sns/v1/note/collection/pc/create
///   ✅ 编辑 — POST /api/sns/v1/note/collection/pc/update（含封面图 fieldId + backgroundColor）
///   ✅ 删除 — POST /api/sns/v1/note/collection/pc/delete
///   ❌ 置顶 — 不支持合集置顶
///   ✅ 封面 — 更新合集支持 image {fieldId, width, height}
///
/// 视频能力矩阵：
///   ✅ 上传 — COS/ROS 直传（4 阶段管线: permit → upload → videoId → transcode）
///   ✅ 发布 — POST /web_api/sns/v2/note
///   ✅ 列表 — galaxy API: /api/galaxy/v2/creator/note/user/posted
///   ✅ 删除 — POST /web_api/sns/capa/postgw/note/delete
///
/// 认证：
///   ✅ 探活 — POST /web_api/sns/capa/postgw/permission/validate
///   ❌ Token 刷新 — Cookie 过期需重新扫码
/// </summary>
public sealed class XhsCapabilities : IPlatformCapabilities
{
    public bool CanCreateCollection => true;
    public bool CanUpdateCollection => true;
    public bool CanDeleteCollection => true;
    public bool CanPinCollection => false;       // 小红书无合集置顶
    public bool CanSetCover => true;             // 更新合集支持封面图

    public bool CanUploadVideo => true;
    public bool CanPublishVideo => true;
    public bool CanDeleteVideo => true;
    public bool CanListVideos => true;

    public bool CanProbeSession => true;         // 可通过 permission/validate 探活
    public bool CanRefreshToken => false;        // Cookie 过期需重新扫码
}
