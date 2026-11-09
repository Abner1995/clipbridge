namespace CreatFlow.NewApp.Services;

/// <summary>
/// 合集标题规则自检（`--selftest` 会一并跑）—— 纯函数，无网络无磁盘。
///
/// 守的是 2026-10-22 用户要求的两条：**最多 20 个字**、**不能含符号**
/// （只允许文字 / 数字 / 空格）；2026-11-05 追加平台差异化：快手 12 字、头条 5~30 字。
/// 规则错一条，要么把合法标题拦死，要么让平台的拒收再回来。
///
/// 结果写 %TEMP%/newapp_collection_title_selftest.txt。
/// </summary>
public static class CollectionTitleSelfTest
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

        sb.AppendLine("═══ 合集标题规则自检 ═══");

        sb.AppendLine("\n[1] 空值与归一化");
        Check("null → 标题不能为空", CollectionTitleRules.Validate(null) == "标题不能为空");
        Check("空串 → 标题不能为空", CollectionTitleRules.Validate("") == "标题不能为空");
        Check("全空白 → 标题不能为空", CollectionTitleRules.Validate(" 　\t") == "标题不能为空");
        Check("首尾空白/连续空白收成一个空格",
            CollectionTitleRules.Normalize("  美食　  探店  ") == "美食 探店",
            CollectionTitleRules.Normalize("  美食　  探店  "));

        sb.AppendLine("\n[2] 合法标题（文字 / 数字 / 空格）");
        Check("中文合法", CollectionTitleRules.Validate("萌宠日常") is null);
        Check("中文 + 数字 + 空格合法",
            CollectionTitleRules.Validate("萌宠 2026 日常") is null);
        Check("拉丁字母合法", CollectionTitleRules.Validate("vlog 2026") is null);
        Check("首尾空白不误伤（归一化后合法）",
            CollectionTitleRules.Validate("  萌宠日常  ") is null);
        Check("恰好 20 字合法",
            CollectionTitleRules.Validate(new string('萌', CollectionTitleRules.MaxLength)) is null);
        Check("21 字 → 超长提示",
            CollectionTitleRules.Validate(new string('萌', CollectionTitleRules.MaxLength + 1))
            is { } longError && longError.Contains("最多 20 个字"),
            CollectionTitleRules.Validate(new string('萌', CollectionTitleRules.MaxLength + 1)) ?? "");

        sb.AppendLine("\n[3] 符号一律不收");
        foreach (var (sample, label) in new[]
                 {
                     ("萌宠-日常", "连字符"),
                     ("萌宠_日常", "下划线"),
                     ("萌宠#1", "井号"),
                     ("萌宠·日常", "间隔号"),
                     ("萌宠（日常）", "括号"),
                     ("萌宠，日常", "中文标点"),
                     ("萌宠！", "感叹号"),
                     ("🐶萌宠", "emoji"),
                 })
        {
            var error = CollectionTitleRules.Validate(sample);
            Check($"{label}「{sample}」→ 报符号",
                error is not null && error.Contains("符号"), error ?? "(未拦)");
        }

        sb.AppendLine("\n[4] HasInvalidChar 明细");
        Check("纯文字/数字/空格 → 无非法字符", !CollectionTitleRules.HasInvalidChar("abc 123 中文"));
        Check("空白不算符号", !CollectionTitleRules.HasInvalidChar(" 　\t"));
        Check("间隔号算符号", CollectionTitleRules.HasInvalidChar("·"));
        Check("emoji 算符号", CollectionTitleRules.HasInvalidChar("🐶"));

        sb.AppendLine("\n[5] 平台差异化上下限（2026-11-05 抓包）");
        var ksError = CollectionTitleRules.Validate(new string('萌', 13), [PlatformNames.Kuaishou]);
        Check("快手 12 字合法",
            CollectionTitleRules.Validate(new string('萌', 12), [PlatformNames.Kuaishou]) is null);
        Check("快手 13 字 → 最多 12",
            ksError is not null && ksError.Contains("最多 12"), ksError ?? "");

        var ttMin = CollectionTitleRules.Validate("萌宠日常", [PlatformNames.Toutiao]);
        Check("头条 4 字 → 至少 5",
            ttMin is not null && ttMin.Contains("至少 5"), ttMin ?? "");
        Check("头条 5 字合法",
            CollectionTitleRules.Validate("萌宠日常啊", [PlatformNames.Toutiao]) is null);
        Check("头条 30 字合法",
            CollectionTitleRules.Validate(new string('萌', 30), [PlatformNames.Toutiao]) is null);

        var ttMax = CollectionTitleRules.Validate(new string('萌', 31), [PlatformNames.Toutiao]);
        Check("头条 31 字 → 最多 30",
            ttMax is not null && ttMax.Contains("最多 30"), ttMax ?? "");

        var mixMax = CollectionTitleRules.Validate(new string('萌', 13),
            [PlatformNames.Douyin, PlatformNames.Kuaishou]);
        Check("抖音 + 快手 → 按 12 收紧",
            mixMax is not null && mixMax.Contains("最多 12"), mixMax ?? "");

        var mixMin = CollectionTitleRules.Validate("萌宠",
            [PlatformNames.Douyin, PlatformNames.Toutiao]);
        Check("抖音 + 头条 → 按 5 收紧",
            mixMin is not null && mixMin.Contains("至少 5"), mixMin ?? "");

        Check("未勾选平台按通用 20",
            CollectionTitleRules.Validate(new string('萌', 20)) is null);
        Check("EffectiveMaxLength 快手 = 12",
            CollectionTitleRules.EffectiveMaxLength([PlatformNames.Kuaishou]) == 12);
        Check("EffectiveMinLength 头条 = 5",
            CollectionTitleRules.EffectiveMinLength([PlatformNames.Toutiao]) == 5);

        sb.AppendLine();
        sb.AppendLine(failures == 0 ? "═══ 全部通过 ═══" : $"═══ {failures} 项失败 ═══");

        var report = sb.ToString();
        var path = Path.Combine(Path.GetTempPath(), "newapp_collection_title_selftest.txt");
        Console.WriteLine(report);
        try { File.WriteAllText(path, report); } catch { /* 写不了盘不影响退出码 */ }
        return failures;
    }
}
