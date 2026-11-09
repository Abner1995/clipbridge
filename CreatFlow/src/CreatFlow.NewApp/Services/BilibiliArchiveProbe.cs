using System.Text;
using System.Text.Json;
using CreatFlow.Platforms.Bilibili;
using CreatFlow.Platforms.Core;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// B站稿件详情探针 —— <c>--probe-bilibili &lt;bvid&gt;</c> 运行。
///
/// **诊断工具，不进业务路径**：发布后拉一次 <c>/x/vupre/web/archive/view</c>，
/// 把 <c>topic_id / topic_name / mission_id / dtime / tag / creation_statement /
/// is_only_self</c> 写进 <c>%TEMP%\bilibili_probe.txt</c>，回答「add/v3 下发的字段
/// 是否真的落到了稿件上」（2.6.10 / 4.7 的回显通道）。
///
/// 前提：服务端在跑（会话 Cookie 在服务端库里），本机已登录会员、且该平台至少绑定过一个账号。
/// 可选 <c>--session &lt;guid&gt;</c> 指定账号；缺省取本地镜像里 B站 的第一个账号。
/// </summary>
public static class BilibiliArchiveProbe
{
    public static async Task<int> RunAsync(string bvid, Guid? sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ B站稿件详情探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
        sb.AppendLine($"bvid={bvid}  session={sessionId?.ToString() ?? "-"}");

        var ctx = new SessionContext();
        if (ctx.MemberId == Guid.Empty)
        {
            sb.AppendLine("未登录 CreatFlow —— 先正常启动软件登录会员，再运行探针");
            return Write(sb, 3);
        }

        var store = new PlatformAccountStore(ctx);
        var sessions = store.Load().Where(r => r.PlatformKey == PlatformNames.Bilibili).ToList();
        var chosen = sessionId is { } want
            ? sessions.FirstOrDefault(s => s.Id == want)
            : sessions.FirstOrDefault();

        Guid id;
        if (chosen is not null) id = chosen.Id;
        else if (sessionId is { } explicitId) id = explicitId;
        else
        {
            sb.AppendLine("本地没有 B站 账号（sessions.json）—— 先绑定/登录一次");
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
            var info = await new BilibiliArchiveService(options).GetAsync(creds.Cookie, bvid);
            sb.AppendLine($"  title={info.Title}");
            sb.AppendLine($"  aid={info.Aid}  bvid={info.Bvid}  tid={info.Tid}");
            sb.AppendLine($"  topic_id={info.TopicId}  topic_name={info.TopicName ?? "(空)"}");
            sb.AppendLine($"  mission_id={info.MissionId}  mission_name={info.MissionName ?? "(空)"}");
            sb.AppendLine($"  dtime={info.Dtime}（0=立即发布）  tag={info.Tag ?? "(空)"}");
            sb.AppendLine($"  is_only_self={info.IsOnlySelf}  "
                          + $"creation_statement={info.CreationStatementId?.ToString() ?? "(无)"} "
                          + $"{info.CreationStatementContent ?? ""}");
            return Write(sb, 0);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[失败] {ex.Message}");
            Log.Warning(ex, "[BilibiliArchiveProbe] 拉取稿件详情失败");
            return Write(sb, 1);
        }
    }

    private static int Write(StringBuilder sb, int exitCode)
    {
        var path = Path.Combine(Path.GetTempPath(), "bilibili_probe.txt");
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
