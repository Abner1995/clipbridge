namespace CreatFlow.Platforms.Core;

/// <summary>
/// 平台通用配置选项。
/// </summary>
public sealed class PlatformOptions
{
    /// <summary>默认请求超时（秒）</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>浏览器 User-Agent</summary>
    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";

    /// <summary>
    /// 登录环境快照（WebView2 采集，每账号每平台一份）。
    /// 非 null 且 IsValid 时 PlatformHttpClient / BrowserFingerprint 优先用它；
    /// null 时 fallback 上面的硬编码 Chrome/150。
    /// </summary>
    public PlatformEnvironmentSnapshot? Environment { get; init; }

    /// <summary>
    /// 调用方会话 ID（可选）—— 随请求透传给签名 Handler。
    /// App 层签名器（如小红书 mnsv2）据此定位「同账号的 WebView2 档案页」，
    /// 让 x-s 与请求所用 Cookie 出自同一档案（跨账号签名有风控风险）。
    /// null = 未指定，签名器自行挑一个可用档案。
    /// </summary>
    public Guid? SessionId { get; init; }

    /// <summary>浏览器 Accept-Language</summary>
    public string AcceptLanguage { get; init; } = "zh-CN,zh;q=0.9";

    /// <summary>抖音 creator 域名</summary>
    public string DouyinCreatorHost { get; init; } = "https://creator.douyin.com";

    /// <summary>抖音 ImageX 域名（封面图上传）</summary>
    public string DouyinImageXHost { get; init; } = "https://imagex.bytedanceapi.com";

    /// <summary>抖音 TOS 上传域名</summary>
    public string DouyinTosHost { get; init; } = "https://tos-d-x-lf.douyin.com";

    /// <summary>抖音 VOD API 域名（视频上传）</summary>
    public string DouyinVodHost { get; init; } = "https://vod.bytedanceapi.com";

    /// <summary>抖音 VOD 上传空间名</summary>
    public string DouyinSpaceName { get; init; } = "aweme";

    // ── 微信视频号 ──

    /// <summary>视频号 CDN 上传域名（assistance CDN — helper_upload_params 的 authKey 签发给此域名）</summary>
    public string WechatVideoCdnHost { get; init; } = "https://finderassistancea.video.qq.com";

    /// <summary>视频号内容管理 API 域名</summary>
    public string WechatVideoApiHost { get; init; } = "https://channels.weixin.qq.com";

    /// <summary>视频号 CDN 域名（用于替换 http://wxapp.tc.qq.com）</summary>
    public string WechatVideoReplaceHost { get; init; } = "https://finder.video.qq.com";

    // ── 快手 ──

    /// <summary>快手创作者平台域名</summary>
    public string KuaishouCpHost { get; init; } = "https://cp.kuaishou.com";

    // ── 小红书 ──

    /// <summary>小红书创作者平台域名</summary>
    public string XhsCreatorHost { get; init; } = "https://creator.xiaohongshu.com";

    /// <summary>小红书 Edith API 域名（内容管理后端）</summary>
    public string XhsEdithHost { get; init; } = "https://edith.xiaohongshu.com";

    /// <summary>小红书登录域</summary>
    public string XhsLoginHost { get; init; } = "https://www.xiaohongshu.com";

    // ── B站 ──

    /// <summary>B站创作中心 API 域名（投稿 / 封面上传 / 分区 / 标签等）</summary>
    public string BilibiliHost { get; init; } = "https://member.bilibili.com";

    // ── 今日头条 ──

    /// <summary>头条号创作平台域名</summary>
    public string ToutiaoHost { get; init; } = "https://mp.toutiao.com";

    /// <summary>
    /// ByteDance VOD 上传域名（阶段 5.1）—— 头条视频 apply/commit 与抖音同一套 VOD。
    /// </summary>
    public string ToutiaoVodHost { get; init; } = "https://vod.bytedanceapi.com";

    /// <summary>头条 VOD 的 <c>app_id</c>（接口说明书 §4.2.2：1231）</summary>
    public string ToutiaoAppId { get; init; } = "1231";

    /// <summary>
    /// 头条 VAS 上传网关（2026-11-05 抓包：GetImageUploadParams / GetImageUploadInfos 都走这里）。
    /// 图片上传必须先取 <c>tos_sign</c> + 预分配的 <c>object_ids</c>，不能拿 getAuthKey 的 uploadToken 直接传 TOS。
    /// </summary>
    public string ToutiaoVasHost { get; init; } = "https://vas-lf-x.ixigua.com";

    /// <summary>VAS 备用网关（SDK 内置的备份域名）</summary>
    public string ToutiaoVasFallbackHost { get; init; } = "https://vas.snssdk.com";

    /// <summary>头条图片 TOS 上传域名（仅当 VAS 响应没给 tos_host 时兜底；2026-11-05 抓包：tos-d-x-hl / tos-lf-x）</summary>
    public string ToutiaoTosHost { get; init; } = "https://tos-d-x-hl.snssdk.com";

    /// <summary>头条图片 TOS 备用 edge 域名</summary>
    public string ToutiaoTosFallbackHost { get; init; } = "https://tos-lf-x.snssdk.com";
}

/// <summary>
/// 浏览器指纹参数 — 抖音 Web API 所有请求都需要携带。
/// 对标 Python fangai/douyin/api.py 中的 BROWSER_PARAMS。
/// </summary>
public static class BrowserFingerprint
{
    public static readonly Dictionary<string, string> Default = new()
    {
        ["cookie_enabled"] = "true",
        ["screen_width"] = "1920",
        ["screen_height"] = "1080",
        ["browser_language"] = "zh-CN",
        ["browser_platform"] = "Win32",
        ["browser_name"] = "Mozilla",
        ["browser_version"] =
            "5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36",
        ["browser_online"] = "true",
        ["timezone_name"] = "Asia/Shanghai",
        ["aid"] = "1128",
        ["support_h265"] = "1",
    };

    /// <summary>
    /// 从环境快照构造指纹参数（用快照值覆盖屏幕/语言/UA/时区等动态字段）。
    /// 快照缺失或无效时返回 Default 副本（调用方可安全修改）。
    /// </summary>
    public static Dictionary<string, string> FromSnapshot(PlatformEnvironmentSnapshot? snap)
    {
        if (snap is not { IsValid: true })
            return new Dictionary<string, string>(Default);

        var qs = new Dictionary<string, string>(Default);
        if (!string.IsNullOrWhiteSpace(snap.ScreenWidth)) qs["screen_width"] = snap.ScreenWidth;
        if (!string.IsNullOrWhiteSpace(snap.ScreenHeight)) qs["screen_height"] = snap.ScreenHeight;
        if (!string.IsNullOrWhiteSpace(snap.BrowserLanguage)) qs["browser_language"] = snap.BrowserLanguage;
        if (!string.IsNullOrWhiteSpace(snap.BrowserPlatform)) qs["browser_platform"] = snap.BrowserPlatform;
        if (!string.IsNullOrWhiteSpace(snap.UserAgent)) qs["browser_version"] = snap.UserAgent;
        if (!string.IsNullOrWhiteSpace(snap.TimezoneName)) qs["timezone_name"] = snap.TimezoneName;
        return qs;
    }
}
