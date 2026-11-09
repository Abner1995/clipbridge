using CreatFlow.Platforms.Abstractions;

namespace CreatFlow.Platforms.Douyin;

/// <summary>
/// 抖音平台能力声明。
///
/// 抖音合集能力矩阵：
///   ✅ 列表 — GET /web/api/mix/list/
///   ✅ 创建 — POST /web/api/mix/create（封面图需先上传 TOS）
///   ✅ 编辑 — POST /web/api/mix/save
///   ✅ 删除 — POST /web/api/mix/destroy
///   ❌ 置顶 — 不支持合集置顶
///   ✅ 封面 — 创建时可设置封面（需先 ApplyImageUpload → TOS PUT → CommitImageUpload）
/// </summary>
public sealed class DouyinCapabilities : IPlatformCapabilities
{
    public bool CanCreateCollection => true;
    public bool CanUpdateCollection => true;
    public bool CanDeleteCollection => true;
    public bool CanPinCollection => false;
    public bool CanSetCover => true;

    public bool CanUploadVideo => true;
    public bool CanPublishVideo => true;
    public bool CanDeleteVideo => true;
    public bool CanListVideos => true;

    public bool CanProbeSession => true;
    public bool CanRefreshToken => false;   // STS token 需重新扫码
}
