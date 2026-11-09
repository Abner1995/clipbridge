using CreatFlow.Platforms.Abstractions;
using CreatFlow.Platforms.Abstractions.Collections;
using CreatFlow.Platforms.Core;
using Microsoft.Extensions.Logging;

namespace CreatFlow.Platforms.WechatVideo;

/// <summary>
/// 微信视频号平台门面 — 聚合上传、内容管理、合集、认证等功能模块。
///
/// 不直接实现任何功能接口，按功能模块拆分后组合：
///   - Upload       → WechatVideoUploadService  (CDN 直传 3 阶段管线)
///   - Content      → WechatVideoContentService (发布/列表/删除/编辑)
///   - Collections  → WechatVideoCollectionService (合集 CRUD + 置顶 + 位置搜索)
///   - Auth         → WechatVideoAuthService    (helper_upload_params → authKey/uin)
///
/// 每个功能模块是一个独立文件，只实现和自己相关的小接口。
///
/// 认证方式：Cookie + device_id（无 AWS SigV4）
/// CDN 上传：Authorization + X-Arguments header（finder.video.qq.com）
/// 内容管理：Cookie 认证（channels.weixin.qq.com）
/// </summary>
public sealed class WechatVideoPlatform
{
    private readonly PlatformOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    public WechatVideoPlatform(PlatformOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    // ── 功能模块（惰性初始化） ──

    private WechatVideoUploadService? _upload;
    /// <summary>视频上传服务 — 3 阶段管线（applyUpload → chunks → complete）→ CDN URL</summary>
    public WechatVideoUploadService Upload => _upload ??= new WechatVideoUploadService(
        _options, _loggerFactory?.CreateLogger<WechatVideoUploadService>());

    private WechatVideoContentService? _content;
    /// <summary>内容管理服务 — 发布、列表、删除、编辑视频</summary>
    public WechatVideoContentService Content => _content ??= new WechatVideoContentService(
        _options, _loggerFactory?.CreateLogger<WechatVideoContentService>());

    private WechatVideoCollectionService? _collections;
    /// <summary>合集服务 — CRUD + 置顶 + 位置搜索</summary>
    public WechatVideoCollectionService Collections => _collections ??= new WechatVideoCollectionService(
        _options, _loggerFactory?.CreateLogger<WechatVideoCollectionService>());

    private WechatVideoAuthService? _auth;
    /// <summary>认证服务 — 通过 Cookie + device_id 获取 authKey/uin</summary>
    public WechatVideoAuthService Auth => _auth ??= new WechatVideoAuthService(
        _options, _loggerFactory?.CreateLogger<WechatVideoAuthService>());

    // ── 能力声明 ──

    /// <summary>视频号平台能力声明</summary>
    public IPlatformCapabilities Capabilities => _capabilities ??= new WechatVideoCapabilities();
    private WechatVideoCapabilities? _capabilities;

    // ── 接口导出（方便 Dispatcher 使用） ──

    /// <summary>合集列表接口</summary>
    public ICollectionList AsCollectionList() => Collections;

    /// <summary>合集创建接口</summary>
    public ICollectionCreate AsCollectionCreate() => Collections;

    /// <summary>合集编辑接口</summary>
    public ICollectionUpdate AsCollectionUpdate() => Collections;

    /// <summary>合集删除接口</summary>
    public ICollectionDelete AsCollectionDelete() => Collections;

    /// <summary>合集置顶接口（视频号独有）</summary>
    public ICollectionPin AsCollectionPin() => Collections;
}
