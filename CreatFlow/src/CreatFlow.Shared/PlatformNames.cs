namespace CreatFlow.Shared;

/// <summary>
/// 平台标识常量与中文名映射。
///
/// 集中管理 platform_key 字符串，各层统一引用，避免硬编码和拼写错误。
/// 新增平台时只需改这一个文件。
/// </summary>
public static class PlatformNames
{
    // ── 平台标识常量 ──

    public const string Douyin = "douyin";
    public const string Kuaishou = "kuaishou";
    public const string Xiaohongshu = "xiaohongshu";
    public const string WechatVideo = "wechatvideo";

    /// <summary>B站（哔哩哔哩创作中心）—— 2026-11-05 起只接入「平台账号页」绑定登录</summary>
    public const string Bilibili = "bilibili";

    /// <summary>今日头条（头条号创作平台）—— 2026-11-05 起只接入「平台账号页」绑定登录</summary>
    public const string Toutiao = "toutiao";

    // ── 全部平台列表 ──

    public static readonly IReadOnlyList<string> All =
        [Douyin, Kuaishou, Xiaohongshu, WechatVideo, Bilibili, Toutiao];

    /// <summary>
    /// **发布页 / 合成页账号芯片可选的平台**，也是**上传队列里会真正执行发布的平台**。
    ///
    /// 六平台全部接通（2026-11-08 更新）：B站走 `multipart` 三段式 + `add/v3`
    /// （2026-11-07 真账号发布通过），头条走 ByteDance VOD + `PublishVideo`
    /// （2026-11-07 横/竖版发布与删除通过）。「可勾选」= 「可发布」。
    /// </summary>
    public static readonly IReadOnlyList<string> Publishing =
        [Douyin, Kuaishou, Xiaohongshu, WechatVideo, Bilibili, Toutiao];

    /// <summary>该平台是否已接入发布链路（见 <see cref="Publishing"/>）</summary>
    public static bool SupportsPublishing(string platformKey) =>
        platformKey is Douyin or Kuaishou or Xiaohongshu or WechatVideo or Bilibili or Toutiao;

    /// <summary>
    /// **已接入合集链路**的平台（2026-11-05：新增头条；B站合集接口仍是 JS 反解，
    /// 账号也未到可创建等级，暂不摆进合集页）。合集页的平台选择按这份清单过滤，
    /// 与发布链路分开。头条视频合集可挂进发布（`PrependPSeriesId`，仅横版），
    /// 文章合集只在图文页用（`CollectionInfo.ContentType=1`，视频发布下拉已过滤）。
    /// </summary>
    public static readonly IReadOnlyList<string> Collections =
        [Douyin, Kuaishou, Xiaohongshu, WechatVideo, Toutiao];

    /// <summary>该平台是否已接入合集链路（见 <see cref="Collections"/>）</summary>
    public static bool SupportsCollections(string platformKey) =>
        platformKey is Douyin or Kuaishou or Xiaohongshu or WechatVideo or Toutiao;

    /// <summary>
    /// **已接入「视频作品删除」链路**的平台（2026-11-07）。
    ///
    /// B站**故意不在**这里：视频删除要过 Geetest 人机验证（抓包 340022），
    /// 我们不做验证码规避 —— 视频管理页对 B站只清本地记录，并提示到创作中心删除。
    /// 头条是 2026-11-07 接的（横版 `articledelete` / 竖版 `ugc/publish/video/v1/delete`，无验证码）。
    /// </summary>
    public static readonly IReadOnlyList<string> VideoDeletion =
        [Douyin, Kuaishou, Xiaohongshu, WechatVideo, Toutiao];

    /// <summary>该平台是否已接入视频作品删除（见 <see cref="VideoDeletion"/>）</summary>
    public static bool SupportsVideoDeletion(string platformKey) =>
        platformKey is Douyin or Kuaishou or Xiaohongshu or WechatVideo or Toutiao;

    // ── platform_key → 中文显示名 ──

    /// <summary>
    /// 根据平台标识返回中文显示名。
    /// 未知 key 原样返回，不抛异常。
    /// </summary>
    public static string GetDisplayName(string platformKey) => platformKey switch
    {
        Douyin => "抖音",
        Kuaishou => "快手",
        Xiaohongshu => "小红书",
        WechatVideo => "视频号",
        Bilibili => "B站",
        Toutiao => "头条",
        _ => platformKey
    };
}
