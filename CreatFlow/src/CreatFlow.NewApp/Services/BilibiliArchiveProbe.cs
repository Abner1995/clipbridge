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
/// 2026-11-15 补：同时回读 <c>data.videos[0].cid</c> 与 <c>data.subtitle</c>
/// （字幕是否挂上）、再按 cid 拉 <c>dm/command/creative/list</c>（卡片是否挂上），
/// 原始响应另存 <c>%TEMP%\bilibili_archive_raw.json</c>。
///
/// 前提：服务端在跑（会话 Cookie 在服务端库里），本机已登录会员、且该平台至少绑定过一个账号。
/// 可选 <c>--session &lt;guid&gt;</c> 指定账号；缺省取本地镜像里 B站 的第一个账号。
/// <c>bvid</c> 传 <c>partitions</c> 时改列分区树 + 二级分区类型（发布页「分区」下拉的数据源）。
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

        // bvid 传 "partitions" → 列分区树 + 二级分区类型（发布页「分区」下拉的数据源）
        if (string.Equals(bvid, "partitions", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var svc = new BilibiliPartitionService(options);
                var tree = await svc.GetTreeAsync(creds.Cookie);
                sb.AppendLine($"  分区树：{tree.Groups.Count} 个父分区 "
                              + $"(code={tree.Code} {tree.Message})；"
                              + $"typelist 原始 {tree.RawEntryCount} 条 / 有效 {tree.EntryCount} 条，"
                              + $"其中子分区 {tree.ChildEntryCount}");
                foreach (var g in tree.Groups)
                {
                    sb.AppendLine($"    · {g.Parent.Name}（{g.Parent.Id}）→ "
                                  + string.Join("、", g.Children.Select(c => $"{c.Name}({c.Id})")));
                }
                if (tree.ChildEntryCount == 0)
                    sb.AppendLine($"  ⚠ typelist 没有子分区条目，响应前缀：{tree.RawJson}");


                var types = await svc.GetHumanTypesAsync(creds.Cookie);
                sb.AppendLine($"  二级分区类型 {types.Count} 个："
                              + string.Join("、", types.Select(t => $"{t.Name}({t.Id})")));
                return Write(sb, 0);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[失败] {ex.Message}");
                Log.Warning(ex, "[BilibiliArchiveProbe] 拉取分区列表失败");
                return Write(sb, 1);
            }
        }

        try
        {
            var svc = new BilibiliArchiveService(options);
            var raw = await svc.GetRawAsync(creds.Cookie, bvid);
            var rawPath = Path.Combine(Path.GetTempPath(), "bilibili_archive_raw.json");
            try { File.WriteAllText(rawPath, raw); } catch { /* 原始响应落盘失败不影响报告 */ }

            var info = BilibiliArchiveService.Parse(raw);
            sb.AppendLine($"  title={info.Title}");
            sb.AppendLine($"  aid={info.Aid}  bvid={info.Bvid}  cid={info.Cid}  tid={info.Tid}");
            sb.AppendLine($"  topic_id={info.TopicId}  topic_name={info.TopicName ?? "(空)"}");
            sb.AppendLine($"  mission_id={info.MissionId}  mission_name={info.MissionName ?? "(空)"}");
            sb.AppendLine($"  dtime={info.Dtime}（0=立即发布）  tag={info.Tag ?? "(空)"}");
            sb.AppendLine($"  is_only_self={info.IsOnlySelf}  "
                          + $"creation_statement={info.CreationStatementId?.ToString() ?? "(无)"} "
                          + $"{info.CreationStatementContent ?? ""}");
            sb.AppendLine($"  subtitle：allow={info.SubtitleAllow}  "
                          + $"lan={info.SubtitleLan ?? "(空)"}  lan_doc={info.SubtitleLanDoc ?? "(空)"}  "
                          + $"draft_list={info.SubtitleDraftCount} 条");

            // 卡片回读：dm/command/creative/list（2026-11-15 B5 抓包里创建前后各一次，
            // 创建前 data:null、创建后是数组）。按 cid 查，草稿态也有效。
            if (info.Cid > 0)
            {
                try
                {
                    var cards = await new BilibiliCardService(options)
                        .ListAsync(creds.Cookie, info.Cid);
                    sb.AppendLine(cards.Count == 0
                        ? "  卡片：无（creative/list → data:null）"
                        : $"  卡片：{cards.Count} 条");
                    foreach (var card in cards)
                    {
                        sb.AppendLine($"    · type={card.Type}  id={card.Id}  "
                                      + $"progress={card.Progress}ms  "
                                      + $"command={card.Command ?? "-"}  "
                                      + $"content={card.Content ?? "-"}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  卡片回读失败：{ex.Message}");
                }
            }

            sb.AppendLine($"  原始响应：{rawPath}");
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
