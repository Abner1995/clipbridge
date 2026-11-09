using CreatFlow.Platforms.Abstractions;

namespace CreatFlow.Platforms.WechatVideo;

/// <summary>
/// 微信视频号平台能力声明。
///
/// 视频号合集能力矩阵：
///   ✅ 列表 — POST /collection/get_collection_list
///   ✅ 创建 — POST /collection/create_collection
///   ✅ 编辑 — POST /collection/update_collection_info
///   ✅ 删除 — POST /collection/delete_collection
///   ✅ 置顶 — stickyOp=1（置顶）/2（取消置顶）via update_collection_info
///   ❌ 封面 — 合集创建不需要单独上传封面
///
/// 视频能力矩阵：
///   ✅ 上传 — CDN 直传（finder.video.qq.com）
///   ✅ 发布 — POST /post/post_create
///   ✅ 列表 — POST /post/post_list
///   ✅ 删除 — POST /post/post_delete
///   ✅ 编辑 — POST /post/finder_update_post_info
/// </summary>
public sealed class WechatVideoCapabilities : IPlatformCapabilities
{
    public bool CanCreateCollection => true;
    public bool CanUpdateCollection => true;
    public bool CanDeleteCollection => true;
    public bool CanPinCollection => true;       // 视频号独有：支持合集置顶
    public bool CanSetCover => false;            // 合集创建不需要单独上传封面

    public bool CanUploadVideo => true;
    public bool CanPublishVideo => true;
    public bool CanDeleteVideo => true;
    public bool CanListVideos => true;

    public bool CanProbeSession => true;         // 可通过轻量 API 探活
    public bool CanRefreshToken => false;        // Cookie 过期需重新扫码
}
