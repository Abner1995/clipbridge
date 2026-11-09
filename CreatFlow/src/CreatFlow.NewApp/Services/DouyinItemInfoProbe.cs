using System.Text;
using System.Text.Json;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Douyin;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 抖音作品详情（创作中心）探针 —— <c>--probe-douyin-item &lt;itemId&gt;</c> 运行。
///
/// **诊断工具，不进业务路径**：Web 端 <c>aweme/detail</c> 的活动字段为空，
/// 本探针改拉创作中心 <c>/web/api/media/item/info/</c>，把响应里所有名字含
/// <c>activity</c> 的字段（顶层 + <c>aweme</c> 对象）原样写进
/// <c>%TEMP%\douyin_item_probe.txt</c> —— 回答「官方活动是否挂到作品上」（2.6.10）。
///
/// 前提：服务端在跑（会话 Cookie 在服务端库里），本机已登录会员、绑定过抖音账号。
/// 可选 <c>--session &lt;guid&gt;</c>；缺省取本地镜像里抖音的第一个账号。
/// </summary>
public static class DouyinItemInfoProbe
{
    public static async Task<int> RunAsync(string itemId, Guid? sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ 抖音作品详情探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
        sb.AppendLine($"item_id={itemId}  session={sessionId?.ToString() ?? "-"}");

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
        var options = new PlatformOptions
        {
            Environment = ParseEnv(creds.ExtraDataJson),
            SessionId = id,
        };

        try
        {
            var info = await new DouyinItemInfoService(options).GetAsync(creds.Cookie, itemId);
            sb.AppendLine($"  desc={(info.Desc ?? "(空)")}");
            if (info.ActivityFields.Count == 0)
            {
                sb.AppendLine("  activity 字段：(响应里一个都没有)");
            }
            else
            {
                sb.AppendLine($"  activity 字段 {info.ActivityFields.Count} 个：");
                foreach (var (k, v) in info.ActivityFields.OrderBy(x => x.Key))
                    sb.AppendLine($"    {k} = {Truncate(v, 600)}");
            }
            return Write(sb, 0);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[失败] {ex.Message}");
            Log.Warning(ex, "[DouyinItemInfoProbe] 拉取作品详情失败");
            return Write(sb, 1);
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] + "…" : text;

    private static int Write(StringBuilder sb, int exitCode)
    {
        var path = Path.Combine(Path.GetTempPath(), "douyin_item_probe.txt");
        try { File.WriteAllText(path, sb.ToString()); } catch { /* 报告落盘失败不影响退出码 */ }
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"报告：{path}");
        Log.CloseAndFlush();
        return exitCode;
    }

    /// <summary>extra_data_json 里的 env 快照（与 TopicProbe.ParseEnv 同口径）。</summary>
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
