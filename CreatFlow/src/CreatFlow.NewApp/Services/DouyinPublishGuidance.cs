namespace CreatFlow.NewApp.Services;

/// <summary>
/// 抖音发布失败的「看得懂的提示」—— 纯函数，自检钉死（<c>--selftest</c>）。
///
/// 两条失败路径的文案都收在这里：
///   · passport 二次验证（<c>x-tt-verify-passport-decision</c>）：CreateWork 被 authlv 拦下；
///   · 上传凭证被拒（<c>/web/api/media/upload/auth/v5/</c> 的 <c>status_code</c>）。
///
/// 2026-10-28 实测：同一个账号两条路会**同时**出现 —— 17:42 CreateWork 被 authlv 拦，
/// 17:48 起上传凭证一律 <c>status_code=8</c>（重登后仍 8）。所以状态码 8 不再报
/// 「Cookie 可能已过期」（那会把用户推进反复重登的死循环），改报「需要完成身份验证」。
///
/// 平台没公开 status_code 的含义，8 → 验证是**相关性推断**；真不确定的码只透出原始码 +
/// 平台的 status_msg，让用户和我们都能拿日志说话。
/// </summary>
public static class DouyinPublishGuidance
{
    /// <summary>验证方式兜底文案（<see cref="ParsePassportVerifyWays"/> 解析失败时用）</summary>
    public const string DefaultVerifyWays = "短信/密码";

    /// <summary>抖音 passport 拦截（CreateWork 返回 <c>x-tt-verify-passport-decision</c>）</summary>
    /// <param name="wayText">验证方式中文列表；空 = 用兜底文案</param>
    /// <param name="windowOpened">是否已把该账号的窗口亮出来并落到发布页</param>
    public static string VerifyRequired(string? wayText, bool windowOpened)
    {
        var ways = string.IsNullOrWhiteSpace(wayText) ? DefaultVerifyWays : wayText.Trim();
        return windowOpened
            ? $"发布失败: 抖音要求完成身份验证（{ways}）。已打开该账号窗口（发布页），完成验证后请点「重试」"
            : $"发布失败: 抖音要求完成身份验证（{ways}）。请在账号坞打开该账号完成验证后重试";
    }

    /// <summary>
    /// 上传凭证刷新失败 —— status_code=8 与 passport authlv 同源（账号被要求验证），
    /// 其余状态码原样透出（含平台 status_msg），不猜。
    /// </summary>
    public static string StsFailure(int statusCode, string? statusMsg, bool windowOpened)
    {
        if (StsLooksLikeVerifyRequired(statusCode))
        {
            return windowOpened
                ? "账号需要完成抖音身份验证（短信/上行短信）。已打开该账号窗口（发布页），完成验证后请点「重试」"
                : "账号需要完成抖音身份验证（短信/上行短信）。请在账号坞打开该账号完成验证后重试";
        }

        if (statusCode == 0)
            return "STS 凭证刷新失败（平台未返回上传凭证），请重新登录抖音后重试";

        var detail = string.IsNullOrWhiteSpace(statusMsg) ? "" : $"（{statusMsg.Trim()}）";
        return $"STS 凭证刷新失败（status_code={statusCode}{detail}），请重新登录抖音后重试";
    }

    /// <summary>
    /// 该状态码是否要引导「完成身份验证」。
    /// 8 = 2026-10-28 实测与 passport authlv 同时出现在同一账号（上传凭证与 CreateWork 一起被拦）。
    /// </summary>
    public static bool StsLooksLikeVerifyRequired(int statusCode) => statusCode == 8;
}
