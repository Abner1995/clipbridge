namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书风控状态码 → 用户可读提示。
///
/// 这些码来自平台自己的安全 SDK（<c>docs/xiaohongshu/上传/20260617/js/xhs_creator_sign_other.js</c>）：
///   <c>PULL_BLOCK_STATUS = 461</c>、<c>NONE_FINGERPRINT_STATUS = 462</c>、
///   <c>RISK_LOGIN_STATUS = 465</c>、<c>RISK_SPAM_STATUS = 471</c>。
///
/// ⚠️ 必须按 **HTTP 状态码**判断，不能看响应体 —— 461 的 body 是
/// <c>{"code":0,"success":true,"data":{}}</c> 的假成功壳，照 body 判会当成发布成功。
/// </summary>
public static class XhsRiskBlock
{
    /// <summary>状态码对应的提示；非风控码返回 null（调用方回落原始错误）。</summary>
    public static string? HintFor(int status) => status switch
    {
        // 同会话其它签名接口（合集/话题/上传许可/转码查询）全是 200，
        // 只有发布被拦 —— 是账号/内容级风控，不是签名或参数问题（2026-10-25 实测）
        461 => "小红书拦截了这次发布（风控 461）。请到「平台账号」打开小红书窗口完成人机验证后重试",
        462 => "小红书要求补充浏览器指纹（462）。请打开小红书窗口刷新后重试",
        465 => "小红书判定登录环境有风险（465），请重新登录小红书",
        471 => "小红书判定为高频/垃圾发布（471），请降低发布频率后重试",
        _ => null,
    };
}
