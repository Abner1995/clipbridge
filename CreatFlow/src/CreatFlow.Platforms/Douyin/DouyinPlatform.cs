using CreatFlow.Platforms.Abstractions;
using CreatFlow.Platforms.Abstractions.Collections;
using CreatFlow.Platforms.Core;
using Microsoft.Extensions.Logging;

namespace CreatFlow.Platforms.Douyin;

/// <summary>
/// 抖音平台门面 — 聚合合集、上传、视频管理、认证等功能模块。
///
/// 不直接实现任何功能接口，按功能模块拆分后组合：
///   - Collections → DouyinCollectionService (ICollectionList/Create/Update/Delete)
///   - Images      → DouyinImageUploadService (图片→TOS 上传)
///   - Upload      → DouyinUploadService (ApplyUploadInner → TOS chunks → CommitUploadInner)
///   - Videos      → DouyinVideoService (create_work / list / delete / update)
///   - Auth        → DouyinAuthService (STS refresh)
///
/// 每个功能模块是一个独立文件，只实现和自己相关的小接口。
/// </summary>
public sealed class DouyinPlatform
{
    private readonly PlatformOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    public DouyinPlatform(PlatformOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    // ── 功能模块（惰性初始化） ──

    private DouyinCollectionService? _collections;
    /// <summary>合集服务 — CRUD + 详情查询</summary>
    public DouyinCollectionService Collections => _collections ??= new DouyinCollectionService(
        _options, _loggerFactory?.CreateLogger<DouyinCollectionService>());

    private DouyinImageUploadService? _images;
    /// <summary>图片上传服务 — 单张图片→TOS（ImageX）</summary>
    public DouyinImageUploadService Images => _images ??= new DouyinImageUploadService(
        _options, _loggerFactory?.CreateLogger<DouyinImageUploadService>());

    private DouyinUploadService? _upload;
    /// <summary>视频上传服务 — 5 阶段管线（ApplyUploadInner → TOS → CommitUploadInner）</summary>
    public DouyinUploadService Upload => _upload ??= new DouyinUploadService(
        _options, _loggerFactory?.CreateLogger<DouyinUploadService>());

    private DouyinVideoService? _videos;
    /// <summary>视频管理服务 — 发布（create_work）、列表、删除、修改</summary>
    public DouyinVideoService Videos => _videos ??= new DouyinVideoService(
        _options, _loggerFactory?.CreateLogger<DouyinVideoService>());

    private DouyinAuthService? _auth;
    /// <summary>认证服务 — STS 凭证刷新 + 会话探活</summary>
    public DouyinAuthService Auth => _auth ??= new DouyinAuthService(
        _options, _loggerFactory?.CreateLogger<DouyinAuthService>());

    private DouyinMediaService? _media;
    /// <summary>媒体服务 — 已发布视频列表（historyMedia），用于自主声明转载源选择</summary>
    public DouyinMediaService Media => _media ??= new DouyinMediaService(
        _options, _loggerFactory?.CreateLogger<DouyinMediaService>());

    private DouyinPoiService? _poi;
    /// <summary>POI 位置搜索服务 — 发布页「添加标签 → 位置」搜索抖音 POI</summary>
    public DouyinPoiService Poi => _poi ??= new DouyinPoiService(
        _options, _loggerFactory?.CreateLogger<DouyinPoiService>());

    private DouyinHotspotService? _hotspot;
    /// <summary>热点搜索服务 — 发布页「关联热点」搜索热点词</summary>
    public DouyinHotspotService Hotspot => _hotspot ??= new DouyinHotspotService(
        _options, _loggerFactory?.CreateLogger<DouyinHotspotService>());

    // ── 能力声明 ──

    /// <summary>抖音平台能力声明</summary>
    public IPlatformCapabilities Capabilities => _capabilities ??= new DouyinCapabilities();
    private DouyinCapabilities? _capabilities;

    // ── 接口导出（方便 Dispatcher 使用） ──

    /// <summary>合集列表接口</summary>
    public ICollectionList AsCollectionList() => Collections;

    /// <summary>合集创建接口</summary>
    public ICollectionCreate AsCollectionCreate() => Collections;

    /// <summary>合集编辑接口</summary>
    public ICollectionUpdate AsCollectionUpdate() => Collections;

    /// <summary>合集删除接口</summary>
    public ICollectionDelete AsCollectionDelete() => Collections;
}
