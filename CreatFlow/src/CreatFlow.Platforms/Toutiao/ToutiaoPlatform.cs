using CreatFlow.Platforms.Abstractions.Collections;

namespace CreatFlow.Platforms.Toutiao;

/// <summary>
/// 今日头条平台门面 — 目前接合集（视频合集 / 专辑 + 文章合集）与合集封面上传。
/// 发布 / 删除视频尚未接入（账号页只做登录绑定）。
/// </summary>
public sealed class ToutiaoPlatform
{
    private readonly PlatformOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    public ToutiaoPlatform(PlatformOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    private ToutiaoCollectionService? _collections;
    /// <summary>视频合集服务 — 列表 / 创建 / 编辑 / 删除 / 详情</summary>
    public ToutiaoCollectionService Collections => _collections ??=
        new ToutiaoCollectionService(_options, _loggerFactory?.CreateLogger<ToutiaoCollectionService>());

    private ToutiaoArticleCollectionService? _articleCollections;
    /// <summary>文章合集服务 — /mp/fe_api/collection/*（与视频合集两套接口，2026-11-05）</summary>
    public ToutiaoArticleCollectionService ArticleCollections => _articleCollections ??=
        new ToutiaoArticleCollectionService(
            _options, _loggerFactory?.CreateLogger<ToutiaoArticleCollectionService>());

    private ToutiaoImageUploadService? _images;
    /// <summary>图片上传服务 — 合集封面 → 头条 TOS</summary>
    public ToutiaoImageUploadService Images => _images ??=
        new ToutiaoImageUploadService(_options, _loggerFactory?.CreateLogger<ToutiaoImageUploadService>());

    private ToutiaoVideoCoverService? _videoCovers;
    /// <summary>视频封面服务 — 发布必填（TOS 上传 → <c>VideoInfo.ThumbUri</c>）</summary>
    public ToutiaoVideoCoverService VideoCovers => _videoCovers ??=
        new ToutiaoVideoCoverService(_options, _loggerFactory);

    // ── 接口导出（方便 Dispatcher 使用） ──

    public ICollectionList AsCollectionList() => Collections;
    public ICollectionCreate AsCollectionCreate() => Collections;
    public ICollectionUpdate AsCollectionUpdate() => Collections;
    public ICollectionDelete AsCollectionDelete() => Collections;
}
