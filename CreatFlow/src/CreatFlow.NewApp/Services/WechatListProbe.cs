using System.Text;
using System.Text.Json;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Topics;
using CreatFlow.Platforms.WechatVideo;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 视频号 <c>post_list</c> 观察探针 —— <c>--probe-wechat-list</c> 运行。**诊断工具，不进业务路径**。
///
/// 用途：发布侧回查不到 objectId 时（<see cref="WechatVideoContentService.FindObjectIdByClipKeyAsync"/>
/// 按 <c>desc.media[0].urlCdnTaskId == clipKey</c> 匹配失败），需要看清平台**原样**返回了什么：
///   · 新作品进没进「视频管理」列表（审核中也会在里面，带 disableInfo 文案）；
///   · 列表里的 <c>urlCdnTaskId</c> 还等不等于发布时的 clipKey；
///   · <c>desc.event</c> 三件套是否真的落在作品上（官方活动 attach 的唯一回读通道，2026-11-14）。
/// 只读：不发布、不改、不删。报告写 <c>%TEMP%\wechat_list_probe.txt</c>，
/// 每页原始 JSON 另存 <c>wechat_list_probe.json</c>。
///
/// 用法：<c>--probe-wechat-list</c>（默认 3 页 × 20 条）；可选 <c>--pages 5</c>、<c>--session &lt;guid&gt;</c>；
/// 给 <c>--clip-key</c>（可再给 <c>--md5</c>）时顺带跑一次发布侧回查，打印查到的 objectId。
/// 前提：服务端在跑且本机已登录会员（会话 Cookie 从服务端会话库取）。
/// </summary>
public static class WechatListProbe
{
    public static async Task<int> RunAsync(
        Guid? sessionId, int pages = 3, string? clipKey = null, string? md5 = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ 视频号 post_list 探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
        sb.AppendLine($"session={sessionId?.ToString() ?? "(第一个视频号账号)"}  pages={pages}  "
                      + $"clipKey={clipKey ?? "-"}  md5={md5 ?? "-"}");

        var ctx = new SessionContext();
        if (ctx.MemberId == Guid.Empty)
        {
            sb.AppendLine("未登录 CreatFlow —— 先正常启动软件登录会员，再运行探针");
            return Write(sb, 3);
        }

        var store = new PlatformAccountStore(ctx);
        var sessions = store.Load().Where(s => s.PlatformKey == PlatformNames.WechatVideo).ToList();
        var chosen = (sessionId is { } want ? sessions.FirstOrDefault(s => s.Id == want) : null)
                     ?? sessions.FirstOrDefault();
        if (chosen is null)
        {
            sb.AppendLine(sessionId is { } id
                ? $"本地没有会话 {id}（sessions.json）—— 先绑定/登录一次"
                : "本地没有视频号账号（sessions.json）—— 先绑定/登录一次");
            return Write(sb, 5);
        }

        var creds = await PlatformSessionApi.GetCredentialsAsync(chosen.Id);
        if (creds?.Cookie is null)
        {
            sb.AppendLine($"取凭证失败或 Cookie 为空（服务端在跑吗？）：{chosen.Id}");
            return Write(sb, 4);
        }

        var deviceId = new PlatformCredentials(creds.Cookie, creds.ExtraDataJson).GetExtraString("deviceId");
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            sb.AppendLine("视频号凭证缺 deviceId —— 到「平台账号」页打开该账号窗口、点「完成登录」刷新一次凭证");
            return Write(sb, 4);
        }

        sb.AppendLine($"  会话 {chosen.Id}  账号 {creds.PlatformName ?? creds.PlatformUid ?? "?"}");

        var svc = new WechatVideoContentService(new PlatformOptions { SessionId = chosen.Id });
        var raws = new List<string>();
        var total = 0;
        var seen = 0;
        for (var page = 1; page <= Math.Max(1, pages); page++)
        {
            var raw = await svc.ListRawAsync(creds.Cookie, deviceId, page, 20, ct: CancellationToken.None);
            if (raw is null)
            {
                sb.AppendLine($"  第 {page} 页：请求失败（errCode≠0，详见日志）");
                break;
            }
            raws.Add(raw);

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (page == 1)
            {
                total = Int(root, "totalCount");
                sb.AppendLine($"  totalCount={total}  continueFlag={Str(root, "continueFlag") ?? "-"}");
            }
            if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                sb.AppendLine($"  第 {page} 页：没有 list 数组");
                break;
            }

            sb.AppendLine($"  ── 第 {page} 页（{list.GetArrayLength()} 条）──");
            foreach (var item in list.EnumerateArray())
            {
                seen++;
                var oid = Str(item, "objectId") ?? "(无)";
                var createTime = Long(item, "createTime");
                var createdAt = createTime > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(createTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                    : "-";
                string? taskId = null, description = null;
                string? eventId = null, eventName = null, eventCreator = null;
                if (item.TryGetProperty("desc", out var desc) && desc.ValueKind == JsonValueKind.Object)
                {
                    description = Str(desc, "description");
                    if (desc.TryGetProperty("media", out var media)
                        && media.ValueKind == JsonValueKind.Array
                        && media.GetArrayLength() > 0)
                        taskId = Str(media[0], "urlCdnTaskId");
                    if (desc.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.Object)
                    {
                        eventId = Str(ev, "eventTopicId");
                        eventName = Str(ev, "eventName");
                        eventCreator = Str(ev, "eventCreatorNickname");
                    }
                }

                var wording = "-";
                if (item.TryGetProperty("disableInfo", out var di) && di.ValueKind == JsonValueKind.Object
                    && Str(di, "disableShowWording") is { Length: > 0 } w)
                    wording = w;

                sb.AppendLine($"    · objectId={oid}");
                sb.AppendLine($"      createTime={createdAt}  status={Int(item, "status")}  "
                              + $"urlCdnTaskId={taskId ?? "(无)"}  disable={wording}");
                sb.AppendLine($"      description={(description is { Length: > 40 } ? description[..40] + "…" : description ?? "(空)")}");
                sb.AppendLine($"      event={{id={(string.IsNullOrWhiteSpace(eventId) ? "-" : eventId)}, "
                              + $"name={(string.IsNullOrWhiteSpace(eventName) ? "-" : eventName)}, "
                              + $"creator={(string.IsNullOrWhiteSpace(eventCreator) ? "-" : eventCreator)}}}");
            }

            var more = root.TryGetProperty("continueFlag", out var cf)
                       && cf.ValueKind == JsonValueKind.True;
            if (!more) break;
        }

        sb.AppendLine();
        sb.AppendLine($"  合计见 {seen} 条（totalCount={total}）");

        // 给了 clipKey / md5 就顺带跑一次发布侧的回查逻辑 —— 只读，验证 objectId 能不能查到
        if (!string.IsNullOrWhiteSpace(clipKey))
        {
            var found = await svc.FindObjectIdByClipKeyAsync(
                creds.Cookie, deviceId, clipKey, md5: md5, ct: CancellationToken.None);
            sb.AppendLine($"  [回查] clipKey={clipKey} md5={md5 ?? "-"} → "
                          + (found is null ? "未找到 objectId" : $"objectId={found}"));
        }

        // 原始 JSON（排障用：字段名/嵌套结构以它为准）
        try
        {
            var rawPath = Path.Combine(Path.GetTempPath(), "wechat_list_probe.json");
            File.WriteAllText(rawPath,
                string.Join("\n\n", raws.Select((r, i) => $"/* page {i + 1} */\n{r}")),
                new UTF8Encoding(false));
            sb.AppendLine($"  原始 JSON：{rawPath}");
        }
        catch { /* 原始落盘失败不影响报告 */ }

        return Write(sb, 0);
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static int Write(StringBuilder sb, int exitCode)
    {
        var path = Path.Combine(Path.GetTempPath(), "wechat_list_probe.txt");
        try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); }
        catch { /* 报告落盘失败不影响退出码 */ }
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"报告：{path}");
        Log.CloseAndFlush();
        return exitCode;
    }
}
