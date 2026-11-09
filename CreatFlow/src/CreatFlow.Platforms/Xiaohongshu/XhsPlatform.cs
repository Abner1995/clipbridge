using CreatFlow.Platforms.Abstractions;
using CreatFlow.Platforms.Abstractions.Collections;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Xiaohongshu.Handlers;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>小红书 x-s 签名结果。</summary>
public sealed record XhsSignHeaders(string XS, string XSCommon, string XT);

/// <summary>
/// 小红书平台门面 — 聚合合集、上传、发布、视频管理、认证功能模块。
///
/// 不直接实现任何功能接口，按功能模块拆分后组合：
///   - Collections → XhsCollectionService (ICollectionList/Create/Update/Delete + add/remove notes)
///   - Upload      → XhsUploadService (4 阶段管线: permit → COS → videoId → transcode)
///   - Publish     → XhsPublishService (build note JSON → POST /web_api/sns/v2/note)
///   - Videos      → XhsVideoService (list/delete)
///   - Auth        → XhsAuthService (probe session)
/// </summary>
public sealed class XhsPlatform
{
    private readonly PlatformOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    public XhsPlatform(PlatformOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    // ── 功能模块（惰性初始化） ──

    private XhsCollectionService? _collections;
    /// <summary>合集服务 — CRUD + add/remove notes + detail</summary>
    public XhsCollectionService Collections =>
        _collections ??= new XhsCollectionService(
            _options, _loggerFactory?.CreateLogger<XhsCollectionService>());

    private XhsUploadService? _upload;
    /// <summary>视频上传服务 — 4 阶段管线（permit → COS → videoId → transcode）</summary>
    public XhsUploadService Upload =>
        _upload ??= new XhsUploadService(
            _options, _loggerFactory?.CreateLogger<XhsUploadService>());

    private XhsPublishService? _publish;
    /// <summary>发布服务 — 构建 note JSON → POST /web_api/sns/v2/note</summary>
    public XhsPublishService Publish =>
        _publish ??= new XhsPublishService(
            _options, _loggerFactory?.CreateLogger<XhsPublishService>());

    private XhsVideoService? _videos;
    /// <summary>视频管理服务 — 列表、删除</summary>
    public XhsVideoService Videos =>
        _videos ??= new XhsVideoService(
            _options, _loggerFactory?.CreateLogger<XhsVideoService>());

    private XhsAuthService? _auth;
    /// <summary>认证服务 — 会话探活</summary>
    public XhsAuthService Auth =>
        _auth ??= new XhsAuthService(
            _options, _loggerFactory?.CreateLogger<XhsAuthService>());

    private XhsTopicService? _topics;
    /// <summary>话题推荐服务 — 根据标题/描述推荐 hash_tag</summary>
    public XhsTopicService Topics =>
        _topics ??= new XhsTopicService(
            _options, _loggerFactory?.CreateLogger<XhsTopicService>());

    private XhsPoiService? _poi;
    /// <summary>POI 搜索服务 — 搜索位置/地点</summary>
    public XhsPoiService Poi =>
        _poi ??= new XhsPoiService(
            _options, _loggerFactory?.CreateLogger<XhsPoiService>());

    private XhsPrivacyService? _privacy;
    /// <summary>隐私服务 — 好友/粉丝列表 + 用户搜索</summary>
    public XhsPrivacyService Privacy =>
        _privacy ??= new XhsPrivacyService(
            _options, _loggerFactory?.CreateLogger<XhsPrivacyService>());

    // ── 能力声明 ──

    /// <summary>小红书平台能力声明</summary>
    public IPlatformCapabilities Capabilities => _capabilities ??= new XhsCapabilities();
    private XhsCapabilities? _capabilities;

    // ── 接口导出（方便 Dispatcher 使用） ──

    public ICollectionList AsCollectionList() => Collections;
    public ICollectionCreate AsCollectionCreate() => Collections;
    public ICollectionUpdate AsCollectionUpdate() => Collections;
    public ICollectionDelete AsCollectionDelete() => Collections;

    // ── 签名注入 ──

    /// <summary>
    /// 设置自定义签名器（替代默认的空 x-s header）。
    ///
    /// 参数:
    ///   urlPath: API 路径（含 query string）
    ///   bodyJson: JSON 序列化的请求体（可为 null）
    ///   sessionId: 发起请求的会话 ID（PlatformOptions.SessionId；可为 null）
    ///   ct: 取消令牌
    ///
    /// 返回: (x_s, x_s_common, x_t) 或 null 表示签名不可用
    ///
    /// 用法（WebView2 签名）:
    ///   XhsPlatform.SetSigner(async (urlPath, bodyJson, sessionId, ct) => {
    ///       var hidden = WebViewLoginWindow.HiddenInstances["xiaohongshu"];
    ///       return await hidden.GetSignHeadersAsync(urlPath, bodyJson, ct);
    ///   });
    /// </summary>
    public static void SetSigner(
        Func<string, string?, Guid?, CancellationToken, Task<XhsSignHeaders?>>? signer)
    {
        XhsSignHandler.SignerDelegate = signer;
    }
}
