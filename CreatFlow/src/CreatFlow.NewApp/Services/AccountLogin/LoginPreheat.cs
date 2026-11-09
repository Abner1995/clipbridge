namespace CreatFlow.NewApp.Services.AccountLogin;

/// <summary>
/// 登录收尾的「预热页」表 —— 对齐老版（CreatFlow.App WebViewLoginWindow.DoneButton_Click）语义：
/// 提取凭证前先把标签页跳到该平台的发布/创作页，让页面自身的带签请求跑起来，
/// WebResourceRequested 才抓得到 msToken / a_bogus / csrfToken（URL 参数 + 请求头）
/// 与 localStorage 里的 api_ph。
///
/// 为什么必须跳：这些 token 不是常量，是页面 SPA 在真实请求里现生成/现落盘的 ——
/// 老版靠「首页 → 跳发布页 → 停几秒 → 点完成登录」的人肉动作补齐；坞内自动登录只有
/// 登录页/首页的流量，2026-09-12 实测：快手 extra_data 无 apiPh、抖音无 msToken/a_bogus
/// （合集接口不校验所以能过，发布链路会缺料）。
///
/// 注意「账号信息页」不在这张表里：四平台统一用平台配置的 LoginUrl（= 各创作者平台入口，
/// 登录后落在带账号卡的首页）。宿主次序固定为「先抓账号信息 → 再跳本表的预热页」，
/// 见 DockTabHostView.PrepareBeforeExtractAsync / CaptureUserInfoAsync。
///
/// 不列入预热页的平台：
///   快手 —— 老版的 api_ph/api_st 取自 localStorage，2026-09-12 实测该键已不存在
///           （localStorage 里只剩 refresh_last_time_* 一类键；发布页跑满 12s 也读不到），
///           合集链路用 SDK 内置兜底值即可通过；将来移植发布若确需 api_ph，
///           得在页面内 hook fetch/XHR 抓请求体（WebResourceRequested 拿不到 body）；
///   视频号 —— deviceId 来自登录流程自身的请求头（finger-print-device-id），实测已抓到 deviceIdLen=32；
///   小红书 —— 登录期没有 token 需求（x-s 由 XhsPageSigner 在签名时用页面 mnsv2 实时算）。
/// </summary>
public static class LoginPreheat
{
    /// <summary>平台 → 预热页（发布/创作页，老版原页）</summary>
    private static readonly Dictionary<string, string> Pages = new()
    {
        // 老版 DoneButton_Click 抖音段：跳合集创建页 + 等 5s + ReadCapturedTokensAsync
        // （msToken/a_bogus 在 URL 参数、csrf 在请求头 —— 全靠页面自身的带签请求跑起来）
        [PlatformNames.Douyin] = "https://creator.douyin.com/creator-micro/content/collection/create",
    };

    /// <summary>该平台是否有预热页（无 = 提取前不需要跳转）</summary>
    public static bool TryGetPage(string platformKey, out string url) =>
        Pages.TryGetValue(platformKey, out url!);

    /// <summary>
    /// 该平台的入口 URL 本身就是**登录页**（视频号：channels.weixin.qq.com → /login.html）。
    /// 这类平台任何「跳回入口页 / 重载登录页」的动作都等于把刚登录的会话逼回扫码 ——
    /// 账号信息补抓、浏览态重抓都要跳过跳页，只在当前页原地读（2026-09-14 用户实测反馈：
    /// 视频号一点「完成登录」页面就又要登录）。
    /// </summary>
    public static bool EntryUrlIsLoginPage(string platformKey) =>
        platformKey == PlatformNames.WechatVideo;
}
