using System.Text.Json;
using CreatFlow.Platforms.Douyin.Requests;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 抖音发布失败提示自检（<c>--selftest</c> 一并跑）—— 纯本地逻辑，零网络/零 UI。
///
/// 守的是 2026-10-28 那次发布事故的两条教训：
///   1. 上传凭证被拒（<c>status_code=8</c>）与 passport authlv 是同一件事，
///      不能再报「Cookie 可能已过期」（用户会反复重登，日志里重登两次仍是 8）；
///   2. 验证方式/状态码要原样透出（平台没公开的码不猜，交给日志和用户判断）。
///
/// 结果写 %TEMP%/newapp_douyin_guidance_selftest.txt。
/// </summary>
public static class DouyinPublishGuidanceSelfTest
{
    public static int Run()
    {
        var sb = new StringBuilder();
        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        sb.AppendLine("═══ 抖音发布失败提示自检 ═══");

        try
        {
            // ── 1. passport 拦截文案 ──
            sb.AppendLine("\n[1] passport 验证（x-tt-verify-passport-decision）");
            var closed = DouyinPublishGuidance.VerifyRequired("短信验证码/上行短信验证", windowOpened: false);
            var opened = DouyinPublishGuidance.VerifyRequired("短信验证码/上行短信验证", windowOpened: true);
            Check("未引导 → 账号坞提示", closed.Contains("账号坞") && closed.Contains("完成验证"),
                closed);
            Check("已引导 → 已打开窗口 + 点重试",
                opened.Contains("已打开该账号窗口") && opened.Contains("重试"), opened);
            Check("验证方式原样透出",
                closed.Contains("短信验证码/上行短信验证") && opened.Contains("短信验证码/上行短信验证"));
            Check("验证方式为空 → 兜底文案",
                DouyinPublishGuidance.VerifyRequired(null, false)
                    .Contains(DouyinPublishGuidance.DefaultVerifyWays)
                && DouyinPublishGuidance.VerifyRequired("  ", true)
                    .Contains(DouyinPublishGuidance.DefaultVerifyWays));

            // ── 2. STS 状态码 → 提示 ──
            sb.AppendLine("\n[2] 上传凭证失败（upload/auth/v5）");
            Check("8 = 需要身份验证", DouyinPublishGuidance.StsLooksLikeVerifyRequired(8));
            Check("0 / 35 / -1 不猜", !DouyinPublishGuidance.StsLooksLikeVerifyRequired(0)
                && !DouyinPublishGuidance.StsLooksLikeVerifyRequired(35)
                && !DouyinPublishGuidance.StsLooksLikeVerifyRequired(-1));

            var sts8Closed = DouyinPublishGuidance.StsFailure(8, null, windowOpened: false);
            var sts8Opened = DouyinPublishGuidance.StsFailure(8, null, windowOpened: true);
            Check("8 不再报「Cookie 可能已过期」",
                !sts8Closed.Contains("Cookie") && !sts8Opened.Contains("Cookie"), sts8Closed);
            Check("8 未引导 → 身份验证 + 账号坞",
                sts8Closed.Contains("身份验证") && sts8Closed.Contains("账号坞"), sts8Closed);
            Check("8 已引导 → 已打开窗口 + 点重试",
                sts8Opened.Contains("已打开该账号窗口") && sts8Opened.Contains("重试"), sts8Opened);

            var sts35 = DouyinPublishGuidance.StsFailure(35, "请稍后重试", windowOpened: false);
            Check("其它码原样透出（码 + status_msg）",
                sts35.Contains("status_code=35") && sts35.Contains("请稍后重试"), sts35);
            var sts35NoMsg = DouyinPublishGuidance.StsFailure(35, null, windowOpened: false);
            Check("无 status_msg 不出现空括号",
                sts35NoMsg.Contains("status_code=35") && !sts35NoMsg.Contains("（）"), sts35NoMsg);
            var sts0 = DouyinPublishGuidance.StsFailure(0, null, windowOpened: false);
            Check("码 0（没给凭证）→ 未返回上传凭证",
                sts0.Contains("未返回上传凭证") && sts0.Contains("重新登录"), sts0);

            // ── 3. 响应体映射（JsonPropertyName 回归防线）──
            sb.AppendLine("\n[3] auth/v5 响应映射");
            var json = """{"status_code":8,"status_msg":"请先完成验证","auth":""}""";
            var resp = JsonSerializer.Deserialize<DouyinAuthResponse>(json);
            Check("status_code 读得到", resp?.StatusCode == 8, $"实际 {resp?.StatusCode}");
            Check("status_msg 读得到", resp?.StatusMsg == "请先完成验证", resp?.StatusMsg ?? "(null)");

            var ok = new DouyinStsResult(
                new DouyinStsToken("ak", "sk", "token"), 0, null);
            var bad = new DouyinStsResult(null, 8, null);
            Check("DouyinStsResult.Ok 口径", ok.Ok && !bad.Ok);

            // ── 4. 引导落点 ──
            sb.AppendLine("\n[4] 身份验证引导落点");
            Check("落点 = 抖音发布页（验证弹窗只在页面里发起发布时出现）",
                DouyinWebExecutor.PublishPageUrl
                    == "https://creator.douyin.com/creator-micro/content/upload",
                DouyinWebExecutor.PublishPageUrl);
        }
        catch (Exception ex)
        {
            failures++;
            sb.AppendLine($"  [FAIL] 自检抛异常：{ex}");
        }

        var path = Path.Combine(Path.GetTempPath(), "newapp_douyin_guidance_selftest.txt");
        sb.AppendLine(failures == 0 ? "\n═══ 全部通过 ═══" : $"\n═══ {failures} 项失败 ═══");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 写不了盘不影响退出码 */ }

        Console.WriteLine($"[DouyinPublishGuidanceSelfTest] failures={failures} -> {path}");
        return failures;
    }
}
