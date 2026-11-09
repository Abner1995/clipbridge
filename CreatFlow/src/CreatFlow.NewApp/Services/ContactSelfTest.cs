namespace CreatFlow.NewApp.Services;

/// <summary>
/// 「联系我们」页内容自检（`--selftest` 会一并跑）—— 纯本地逻辑，零网络/零 UI。
///
/// 守的是三件事：
///   1. 服务端公开配置的取值口径（键名 contact.content、字符串/缺失/空串）；
///   2. 本地兜底内容（服务器不可用时页面仍可读，多邮箱必须分行）；
///   3. 链接白名单（只放行 http/https/mailto，挡住 javascript:/file: 等）。
///
/// 结果写 %TEMP%/newapp_contact_selftest.txt。
/// </summary>
public static class ContactSelfTest
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

        sb.AppendLine("═══ 联系我们内容自检 ═══");

        try
        {
            // ── 1. 配置键固定 ──
            sb.AppendLine("\n[1] 服务端配置键");
            Check("键名钉死 contact.content", ContactContent.ConfigKey == "contact.content");

            // ── 2. 多邮箱拆分 / 登录窗展示 ──
            sb.AppendLine("\n[2] 多邮箱拆分与换行");
            var split = AppConfig.SplitEmails("a@x.com/b@x.com;c@x.com，d@x.com、e@x.com");
            Check("兼容 / ; ， 、 五种分隔", split.Count == 5, $"count={split.Count}");
            Check("保留顺序并去空白",
                split.SequenceEqual(["a@x.com", "b@x.com", "c@x.com", "d@x.com", "e@x.com"]),
                string.Join("|", split));
            Check("空串/空白 → 空列表",
                AppConfig.SplitEmails("").Count == 0 && AppConfig.SplitEmails("  ").Count == 0);
            Check("登录窗展示串按行拆开（不残留 /）",
                !AppConfig.ContactEmailDisplay.Contains('/')
                && (AppConfig.ContactEmails.Count <= 1
                    || AppConfig.ContactEmailDisplay.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
                       == AppConfig.ContactEmails.Count),
                AppConfig.ContactEmailDisplay.Replace("\n", " | "));

            // ── 3. 服务端公开配置取值 ──
            sb.AppendLine("\n[3] 公开配置取值口径");
            var html = "<p>测试联系方式</p>";
            var resp = new Models.PublicConfigResponse(null, null,
                new Dictionary<string, JsonElement>
                {
                    [ContactContent.ConfigKey] = JsonSerializer.SerializeToElement(html),
                });
            Check("contact.content 字符串原样取出", ContactContent.ExtractHtml(resp) == html);
            Check("Contact 组缺失 → null", ContactContent.ExtractHtml(null) is null
                && ContactContent.ExtractHtml(new Models.PublicConfigResponse(null, null, null)) is null);
            Check("键缺失 → null", ContactContent.ExtractHtml(
                new Models.PublicConfigResponse(null, null,
                    new Dictionary<string, JsonElement>())) is null);
            Check("空串/空白 → null", ContactContent.ExtractHtml(
                new Models.PublicConfigResponse(null, null,
                    new Dictionary<string, JsonElement>
                    {
                        [ContactContent.ConfigKey] = JsonSerializer.SerializeToElement("   "),
                    })) is null);

            // ── 4. 本地兜底内容 ──
            sb.AppendLine("\n[4] 本地兜底（服务器不可用/未配置）");
            var emails = new[] { "a@x.com", "b@x.com", "c@x.com" };
            var fallback = ContactContent.BuildFallbackHtml(emails, "周一至周五 9:00-17:00");
            Check("兜底包含全部邮箱", emails.All(fallback.Contains));
            Check("多邮箱逐个换行（<br/> 分隔）", fallback.Contains("a@x.com<br/>b@x.com")
                && fallback.Contains("b@x.com<br/>c@x.com"));
            Check("兜底包含工作时间", fallback.Contains("周一至周五 9:00-17:00"));
            Check("HTML 特殊字符被转义",
                ContactContent.BuildFallbackHtml(["<x>"], "<b>").Contains("&lt;x&gt;")
                && ContactContent.BuildFallbackHtml(["<x>"], "<b>").Contains("&lt;b&gt;"));
            Check("真实配置的兜底非空", ContactContent.BuildFallbackHtml().Length > 0);

            // ── 5. 链接白名单 ──
            sb.AppendLine("\n[5] 链接白名单（只放行 http/https/mailto）");
            Check("https 放行", ContactContent.ResolveLink("https://example.com/a")?.Scheme == "https");
            Check("http 放行", ContactContent.ResolveLink("http://example.com")?.Scheme == "http");
            Check("mailto 放行", ContactContent.ResolveLink("mailto:a@x.com")?.Scheme == "mailto");
            Check("裸 www. 补 https",
                ContactContent.ResolveLink("www.example.com")?.ToString() == "https://www.example.com/");
            Check("javascript: 拒绝", ContactContent.ResolveLink("javascript:alert(1)") is null);
            Check("file: 拒绝", ContactContent.ResolveLink("file:///C:/Windows/win.ini") is null);
            Check("坏链接/空值拒绝", ContactContent.ResolveLink("不是链接") is null
                && ContactContent.ResolveLink(null) is null
                && ContactContent.ResolveLink("  ") is null);
        }
        catch (Exception ex)
        {
            failures++;
            sb.AppendLine($"  [FAIL] 自检抛异常：{ex}");
        }

        var path = Path.Combine(Path.GetTempPath(), "newapp_contact_selftest.txt");
        sb.AppendLine(failures == 0 ? "\n═══ 全部通过 ═══" : $"\n═══ {failures} 项失败 ═══");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 写不了盘不影响退出码 */ }

        Console.WriteLine($"[ContactSelfTest] failures={failures} -> {path}");
        return failures;
    }
}
