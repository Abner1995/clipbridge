using System.Text;
using System.Text.Json;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Douyin;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 抖音「添加标签」探针 —— <c>--probe-douyin-anchor &lt;小程序分享链接&gt;</c> 运行。
///
/// **诊断工具，不进业务路径**：回答「一条小程序分享链接到底能不能变成可挂载的 anchor」。
/// 三步全打出来：能力位（账号能不能挂小程序）→ 搜索（链接解析出哪个小程序）→
/// 选中确认（平台让不让挂），最后把**将要下发的发布体片段**照原样拼出来给人看。
/// 报告写 <c>%TEMP%\douyin_anchor_probe.txt</c>。
///
/// 前提：服务端在跑（会话 Cookie 在服务端库里），本机已登录会员、绑定过抖音账号。
/// 可选 <c>--session &lt;guid&gt;</c>；缺省取本地镜像里抖音的第一个账号。
/// </summary>
public static class DouyinAnchorProbe
{
    public static async Task<int> RunAsync(string link, Guid? sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ 抖音添加标签探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
        sb.AppendLine($"link={link}");
        sb.AppendLine();

        if (!DouyinAnchorService.IsAppletLink(link))
        {
            sb.AppendLine("❌ 链接不合页面口径：必须是 http/https 绝对地址，且 host 不能是 creator.douyin.com");
            return Write(sb, 2);
        }

        var ctx = new SessionContext();
        if (ctx.MemberId == Guid.Empty)
        {
            sb.AppendLine("未登录 CreatFlow —— 先正常启动软件登录会员，再运行探针");
            return Write(sb, 3);
        }

        var store = new PlatformAccountStore(ctx);
        var sessions = store.Load().Where(r => r.PlatformKey == PlatformNames.Douyin).ToList();
        var chosen = sessionId is { } want
            ? sessions.FirstOrDefault(s => s.Id == want)
            : sessions.FirstOrDefault();

        Guid id;
        if (chosen is not null) id = chosen.Id;
        else if (sessionId is { } explicitId) id = explicitId;
        else
        {
            sb.AppendLine("本地没有抖音账号（sessions.json）—— 先绑定/登录一次");
            return Write(sb, 4);
        }

        PlatformSessionCredentials? creds;
        try
        {
            creds = await PlatformSessionApi.GetCredentialsAsync(id);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"取凭证异常（服务端在跑吗？）：{ex.Message}");
            return Write(sb, 5);
        }

        if (creds?.Cookie is null)
        {
            sb.AppendLine($"取凭证失败或 Cookie 为空（服务端在跑吗？）：{id}");
            return Write(sb, 5);
        }

        sb.AppendLine($"  会话 {id}  账号 {creds.PlatformName ?? creds.PlatformUid ?? "?"}");
        sb.AppendLine();

        var options = new PlatformOptions
        {
            Environment = ParseEnv(creds.ExtraDataJson),
            SessionId = id,
        };
        var svc = new DouyinAnchorService(options);

        // ── ① 能力位 ──
        DouyinAnchorCapabilities? caps = null;
        try
        {
            caps = await svc.GetCapabilitiesAsync(creds.Cookie);
            sb.AppendLine($"① 能力位 anchor/list → 可用 [{string.Join(",", caps.Available)}]");
            if (caps.Unavailable.Count > 0)
                foreach (var (type, reason) in caps.Unavailable.OrderBy(x => x.Key))
                    sb.AppendLine($"     禁用 type={type}：{reason}");
            sb.AppendLine($"     小程序(type={DouyinAnchorTypes.Applet}) "
                + (caps.IsAvailable(DouyinAnchorTypes.Applet) ? "✅ 可用" : "❌ 不可用"));
        }
        catch (Exception ex)
        {
            sb.AppendLine($"① 能力位拉取失败（按 fail-open 继续）：{ex.Message}");
        }
        sb.AppendLine();

        // ── ② 搜索 ──
        IReadOnlyList<DouyinAnchorCandidate> items;
        try
        {
            items = await svc.SearchAsync(creds.Cookie, DouyinAnchorTypes.Applet, link);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"② 搜索失败：{ex.Message}");
            return Write(sb, 1);
        }

        sb.AppendLine($"② 搜索 anchor/search（keyword=链接, anchor_type=4）→ {items.Count} 条");
        if (items.Count == 0)
        {
            sb.AppendLine("     ⚠️ 一条都没解析出来 —— 链接过期 / 不是小程序链接 / 该账号搜不到");
            return Write(sb, 1);
        }

        foreach (var it in items)
        {
            sb.AppendLine($"   ── id={it.Id}");
            sb.AppendLine($"      type={it.Type}  name={it.Title}  summary={it.Subtitle ?? "(空)"}");
            sb.AppendLine($"      cover={it.Cover ?? "(空)"}");
            sb.AppendLine($"      content(extra) 长度={it.Content?.Length ?? 0}");
            if (it.Content is { Length: > 0 } content)
                sb.AppendLine($"      content={Truncate(content, 1200)}");
            else
                sb.AppendLine("      ⚠️ content 为空 —— 发布体组不出来（BuildAnchorJson 会返回 null）");
        }
        sb.AppendLine();

        // ── ③ 选中确认 ──
        sb.AppendLine("③ 选中确认 anchor/check（小程序传 applet_id）");
        foreach (var it in items)
        {
            try
            {
                var check = await svc.CheckAsync(creds.Cookie, DouyinAnchorTypes.Applet, it.Id);
                sb.AppendLine($"   id={it.Id} → ok={check.Ok}  reason={check.Reason ?? "(无)"}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"   id={it.Id} → 确认失败：{ex.Message}");
            }
        }
        sb.AppendLine();

        // ── ④ 将要下发的发布体片段 ──
        sb.AppendLine("④ 发布体片段（若选中第一条）");
        var first = items[0];
        var anchorJson = DouyinAnchorService.BuildAnchorJson(first.Type, first.Id, first.Content);
        var anchorContentJson = DouyinAnchorService.BuildAnchorContentJson(first.Type, first.Content);
        sb.AppendLine($"   item.anchor.anchor            = {anchorJson ?? "null（组不出来）"}");
        sb.AppendLine($"   item.anchor.anchor_business_type = 4");
        sb.AppendLine($"   item.anchor.anchor_content    = {Truncate(anchorContentJson ?? "null", 1600)}");

        return Write(sb, first.Content is null ? 1 : 0);
    }

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] + "…" : text;

    private static int Write(StringBuilder sb, int exitCode)
    {
        var path = Path.Combine(Path.GetTempPath(), "douyin_anchor_probe.txt");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 报告落盘失败不影响退出码 */ }
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"报告：{path}");
        Log.CloseAndFlush();
        return exitCode;
    }

    /// <summary>extra_data_json 里的 env 快照（与 TopicProbe / DouyinItemInfoProbe 同口径）。</summary>
    private static PlatformEnvironmentSnapshot? ParseEnv(string? extraDataJson)
    {
        if (string.IsNullOrWhiteSpace(extraDataJson)) return null;
        try
        {
            var extra = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extraDataJson);
            if (extra is null || !extra.TryGetValue("env", out var raw)) return null;
            var json = raw.ToString();
            if (string.IsNullOrWhiteSpace(json)) return null;
            var snap = JsonSerializer.Deserialize<PlatformEnvironmentSnapshot>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return snap is { IsValid: true } ? snap : null;
        }
        catch { return null; }
    }
}
