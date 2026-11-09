using CreatFlow.Platforms.Abstractions;
using CreatFlow.Platforms.Abstractions.Collections;
using CreatFlow.Platforms.Core;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手平台门面 — 聚合合集、上传、视频管理、发布、辅助功能模块。
///
/// 不直接实现任何功能接口，按功能模块拆分后组合：
///   - Collections → KuaishouCollectionService (ICollectionList/Create/Update/Delete)
///   - Upload      → KuaishouUploadService (preInfo → CDN chunks → notifyFinish)
///   - Videos      → KuaishouVideoService (list/delete/top/untop/publishNow)
///   - Publish     → KuaishouPublishService (submit)
///   - Helpers     → KuaishouHelperService (tags, POIs)
///   - Auth        → KuaishouAuthService (probe session)
/// </summary>
public sealed class KuaishouPlatform
{
    private readonly PlatformOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    public KuaishouPlatform(PlatformOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    // ── 功能模块（惰性初始化） ──

    private KuaishouCollectionService? _collections;
    /// <summary>合集服务 — CRUD + 详情查询</summary>
    public KuaishouCollectionService Collections =>
        _collections ??= new KuaishouCollectionService(
            _options, _loggerFactory?.CreateLogger<KuaishouCollectionService>());

    private KuaishouUploadService? _upload;
    /// <summary>视频上传服务 — 5 阶段 CDN 管线</summary>
    public KuaishouUploadService Upload =>
        _upload ??= new KuaishouUploadService(
            _options, _loggerFactory?.CreateLogger<KuaishouUploadService>());

    private KuaishouVideoService? _videos;
    /// <summary>视频管理服务 — 列表、删除、置顶/取消置顶</summary>
    public KuaishouVideoService Videos =>
        _videos ??= new KuaishouVideoService(
            _options, _loggerFactory?.CreateLogger<KuaishouVideoService>());

    private KuaishouPublishService? _publish;
    /// <summary>发布服务 — submit API</summary>
    public KuaishouPublishService Publish =>
        _publish ??= new KuaishouPublishService(
            _options, _loggerFactory?.CreateLogger<KuaishouPublishService>());

    private KuaishouHelperService? _helpers;
    /// <summary>辅助功能 — 标签搜索、POI 搜索、附近地点</summary>
    public KuaishouHelperService Helpers =>
        _helpers ??= new KuaishouHelperService(
            _options, _loggerFactory?.CreateLogger<KuaishouHelperService>());

    private KuaishouAuthService? _auth;
    /// <summary>认证服务 — 会话探活</summary>
    public KuaishouAuthService Auth =>
        _auth ??= new KuaishouAuthService(
            _options, _loggerFactory?.CreateLogger<KuaishouAuthService>());

    // ── 能力声明 ──

    /// <summary>快手平台能力声明</summary>
    public IPlatformCapabilities Capabilities => _capabilities ??= new KuaishouCapabilities();
    private KuaishouCapabilities? _capabilities;

    // ── 接口导出（方便 Dispatcher 使用） ──

    public ICollectionList AsCollectionList() => Collections;
    public ICollectionCreate AsCollectionCreate() => Collections;
    public ICollectionUpdate AsCollectionUpdate() => Collections;
    public ICollectionDelete AsCollectionDelete() => Collections;

    // ── 签名注入 ──

    /// <summary>
    /// 设置自定义签名器（页面 webpack 里的 sig3/sig4 模块；替代已移除的 node.exe 回退）。
    ///
    /// 参数: 会话 ID + JSON 格式的签名请求字符串 → 签名字符串（直接拼到 URL 后面）。
    /// 未设置或返回 null → 请求裸发（平台拒绝，由上层回报失败原因）。
    ///
    /// 用法（WebView2 签名）:
    ///   KuaishouPlatform.SetSigner((sessionId, reqJson, ct) =>
    ///       KuaishouSignerService.SignAsync(sessionId, reqJson, ct));
    /// </summary>
    public static void SetSigner(Func<Guid?, string, CancellationToken, Task<string?>>? signer)
    {
        Handlers.KuaishouSigHandler.SignerDelegate = signer;
    }

}
