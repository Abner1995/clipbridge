namespace CreatFlow.Platforms.Toutiao;

/// <summary>
/// 头条视频封面 —— **发布必填**。不选封面直接点发布时前端直接 toast
/// 「该视频需要上传一个封面」，<c>PublishVideo</c> 根本不会发出。
///
/// **wire 形体（2026-11-05 抓包 capture-01-02-37 行 4685–4782 全链路核对）**：
/// <code>
/// 1. GET /xigua/api/upload/getAuthKey/?params={"type":"image_temp",…}   → accessKey/uploadToken
/// 2. GET https://vas-lf-x.ixigua.com/video/openapi/v1/?action=GetImageUploadParams&…&amp;file_size=N
///    → tos_host / tos_sign / object_ids[0]
/// 3. POST https://{tos_host}/{object_ids[0]}   （Authorization=tos_sign / X-Storage-U / Content-CRC32）
/// 4. PublishVideo:
///      VideoInfo.ThumbUri = tos-cn-i-e874c6/{key}            ← 就是第 3 步的对象名
///      VideoInfo.ThumbUrl = blob:https://mp.toutiao.com/{guid} ← 浏览器预览地址（服务端不解析）
/// </code>
///
/// ⚠️ **接口说明书 §4.3 旧版是误读**：「TOS 之后还要 <c>spice/image?upload_source=20020003</c> 登记」
/// 属于**图文**封面 —— 抓包里所有 20020003 请求的 Referer 都是
/// <c>/profile_v4/graphic/publish</c>（图文编辑器）；视频上传页
/// （<c>/profile_v4/xigua/upload-video</c>）从选封面到发布**一次 spice/image 都没调**：
/// 行 4733 把 44223 字节 PNG 传到 <c>tos-cn-i-e874c6/oQNC6F…</c>，行 4776 的 <c>PublishVideo</c>
/// 直接把它当 <c>ThumbUri</c>（发布成功，ItemId=7704491911486980627）。
/// 另：<c>GetEnhancedImage</c> 只对封面编辑器里的**模板图**调用过，不是发布必需步骤。
///
/// 本服务只做「本地校验 + TOS 上传 + 组装发布字段」；封面裁剪是页面/客户端的活，
/// 平台只认最后提交的图片字节（抓包里编辑器导出的就是 1920×1080 PNG）。
/// </summary>
public sealed class ToutiaoVideoCoverService
{
    /// <summary>视频上传页（TOS / 凭证请求的 Referer 与抓包一致）。</summary>
    public const string VideoCoverReferer = "https://mp.toutiao.com/profile_v4/xigua/upload-video";

    /// <summary>页面给 <c>ThumbUrl</c> 的形态（浏览器 blob 预览地址，平台服务端不解析）。</summary>
    public const string BlobUrlPrefix = "blob:https://mp.toutiao.com/";

    /// <summary>封面缺失时的用户文案（平台硬要求，不提供「无封面发布」的降级路径）。</summary>
    public const string CoverRequiredMessage = "头条发布必须上传封面（平台要求），请选择一张封面图后重试";

    /// <summary>
    /// 本地保护性上限（**不是**平台测得的限制）：页面封面编辑器导出约 40~80 KB，
    /// 10 MB 只是防呆，避免把手机原图直接发出去。
    /// </summary>
    private const long MaxCoverBytes = 10 * 1024 * 1024;

    private readonly ToutiaoImageUploadService _images;

    public ToutiaoVideoCoverService(PlatformOptions options, ILoggerFactory? loggerFactory = null)
    {
        _images = new ToutiaoImageUploadService(
            options, loggerFactory?.CreateLogger<ToutiaoImageUploadService>());
    }

    /// <summary>
    /// 上传一张视频封面 → 发布体要的 <c>ThumbUri</c> / <c>ThumbUrl</c>。
    ///
    /// 本地校验（必填 / 存在 / 非空 / 大小 / 图片格式）**全部在发请求之前**完成，
    /// 校验失败或上传失败一律抛 <see cref="ToutiaoVideoCoverException"/>（带用户可读文案）。
    /// </summary>
    public async Task<ToutiaoVideoCover> UploadAsync(
        string? cookie, string? coverPath, string? uid, string? csrfToken,
        CancellationToken ct = default)
    {
        var data = await ReadCoverAsync(coverPath, ct);

        // 没有 secsdk 凭证时 getAuthKey 必被拒；提前给「去哪个窗口重新登录」的引导
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new ToutiaoVideoCoverException(
                "头条登录态缺失：请打开头条账号窗口重新登录后再试");
        }
        if (string.IsNullOrWhiteSpace(csrfToken))
        {
            throw new ToutiaoVideoCoverException(
                "头条会话缺少 secsdk 凭证（x-secsdk-csrf-token）：请打开头条账号窗口重新登录"
                + "（等页面加载完后点「完成登录」刷新凭证）后再试");
        }

        var uploaded = await _images.UploadAsync(
            cookie, data, uid ?? string.Empty, csrfToken, ct);
        if (uploaded is null)
        {
            throw new ToutiaoVideoCoverException(
                "头条封面上传失败（TOS 凭证或网络异常），详见日志；"
                + "请确认头条登录未过期后重试");
        }

        return new ToutiaoVideoCover(
            ThumbUri: uploaded.Uri,
            ThumbUrl: BuildThumbUrl(),
            Width: uploaded.Width,
            Height: uploaded.Height,
            SizeBytes: data.Length);
    }

    /// <summary>
    /// 纯函数：TOS 上传结果 → 发布体字段（<c>ThumbUri</c> 原样、<c>ThumbUrl</c> 页面同形 blob）。
    /// <paramref name="guid"/> 只为自检可复现，正常调用不传。
    /// </summary>
    public static ToutiaoVideoCover FromUpload(
        ToutiaoImageUploadResult uploaded, long sizeBytes = 0, string? guid = null)
        => new(uploaded.Uri, BuildThumbUrl(guid), uploaded.Width, uploaded.Height, sizeBytes);

    /// <summary>页面同形的 blob 预览地址（每次生成新的；平台服务端不解析，照发只为与页面同形）。</summary>
    public static string BuildThumbUrl(string? guid = null)
        => BlobUrlPrefix + (string.IsNullOrWhiteSpace(guid) ? Guid.NewGuid().ToString("D") : guid);

    /// <summary>认图片并取尺寸（JPEG / PNG / GIF / BMP / WebP）；认不出 → false。</summary>
    public static bool TryReadCoverSize(byte[] data, out int width, out int height)
        => ToutiaoImageUploadService.TryReadImageSize(data, out width, out height);

    /// <summary>
    /// 读封面文件并做本地校验（**在发任何请求之前**）。
    /// 路径缺失/空文件/超过本地保护上限/认不出图片格式 → <see cref="ToutiaoVideoCoverException"/>。
    /// </summary>
    internal static async Task<byte[]> ReadCoverAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ToutiaoVideoCoverException(CoverRequiredMessage);

        if (!File.Exists(path))
            throw new ToutiaoVideoCoverException($"头条封面图不存在：{path}");

        var info = new FileInfo(path);
        if (info.Length <= 0)
            throw new ToutiaoVideoCoverException($"头条封面图是空文件：{path}");
        if (info.Length > MaxCoverBytes)
        {
            throw new ToutiaoVideoCoverException(
                $"头条封面图 {info.Length / 1024.0 / 1024.0:0.#} MB，超过本地保护上限 "
                + $"{MaxCoverBytes / 1024 / 1024} MB；请换一张小一点的图");
        }

        var data = await File.ReadAllBytesAsync(path, ct);
        if (!TryReadCoverSize(data, out var width, out var height) || width <= 0 || height <= 0)
        {
            throw new ToutiaoVideoCoverException(
                $"头条封面图不是可识别的图片（JPEG / PNG / GIF / BMP / WebP）：{path}");
        }

        return data;
    }
}

/// <summary>
/// 头条视频封面结果。
/// <c>ThumbUri</c> → <c>PublishVideo.VideoInfo.ThumbUri</c>（TOS 对象名，就是封面本身）；
/// <c>ThumbUrl</c> → <c>VideoInfo.ThumbUrl</c>（页面同形 blob 地址，服务端不解引用）。
/// </summary>
public sealed record ToutiaoVideoCover(
    string ThumbUri, string ThumbUrl, int Width, int Height, long SizeBytes)
{
    /// <summary>落到 <c>VideoInfo</c> 的两个封面字段（顺序与命名固定，别对调）。</summary>
    public (string ThumbUri, string ThumbUrl) ToVideoInfoFields() => (ThumbUri, ThumbUrl);
}

/// <summary>头条视频封面错误（必填缺失 / 文件非法 / 上传失败）。</summary>
public sealed class ToutiaoVideoCoverException : Exception
{
    public ToutiaoVideoCoverException(string message) : base(message) { }
}
