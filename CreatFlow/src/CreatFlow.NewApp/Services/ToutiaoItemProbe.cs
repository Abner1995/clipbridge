using System.Text;
using System.Text.Json;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Toutiao;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 头条作品详情（编辑页初始化 EditItem）探针 —— <c>--probe-toutiao-item &lt;itemId&gt;</c> 运行。
///
/// **诊断工具，不进业务路径**。用途：回读 <c>PublishVideo</c> 下发后平台侧的真实状态，
/// 回答两个问题：
///   · <c>HashtagList</c> 提交后是否真的挂到作品上（此前只有抓包里别人作品的元素形状，未带话题真发过）；
///   · <c>VideoInfo.Vid</c>（<c>hashtag/rec</c> 推荐接口需要它，2.6.8 的头条腿）。
///
/// 端点见 <c>docs/toutiao/20261105/requests/video_detail.json</c>：
/// <code>GET /xigua/api/upload/EditItem/?params={"ItemId":"…","CreateSource":2}</code>
///
/// 前提：服务端在跑（会话 Cookie 在服务端库里），本机已登录会员、绑定过头条账号。
/// 可选 <c>--session &lt;guid&gt;</c>；缺省取本地镜像里头条的第一个账号。
/// 报告写 <c>%TEMP%\toutiao_item_probe.txt</c>，原始 JSON 另存同目录 <c>.json</c>。
/// </summary>
public static class ToutiaoItemProbe
{
    public static async Task<int> RunAsync(string itemId, Guid? sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ 头条作品详情探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
        sb.AppendLine($"item_id={itemId}  session={sessionId?.ToString() ?? "-"}");

        var ctx = new SessionContext();
        if (ctx.MemberId == Guid.Empty)
        {
            sb.AppendLine("未登录 CreatFlow —— 先正常启动软件登录会员，再运行探针");
            return Write(sb, 3);
        }

        var store = new PlatformAccountStore(ctx);
        var sessions = store.Load().Where(r => r.PlatformKey == PlatformNames.Toutiao).ToList();
        var chosen = sessionId is { } want
            ? sessions.FirstOrDefault(s => s.Id == want)
            : sessions.FirstOrDefault();

        Guid id;
        if (chosen is not null) id = chosen.Id;
        else if (sessionId is { } explicitId) id = explicitId;
        else
        {
            sb.AppendLine("本地没有头条账号（sessions.json）—— 先绑定/登录一次");
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

        var extra = CredentialFields.Extra(creds.ExtraDataJson);
        var csrfToken = extra.GetValueOrDefault("csrfToken")?.ToString();
        var options = new PlatformOptions
        {
            Environment = ParseEnv(creds.ExtraDataJson),
            SessionId = id,
        };

        sb.AppendLine($"  会话 {id}  账号 {creds.PlatformName ?? creds.PlatformUid ?? "?"}");
        if (string.IsNullOrWhiteSpace(csrfToken))
            sb.AppendLine("  ⚠️ extra_data 里没有 csrfToken（GET 详情通常不需要，删/写接口才要）");

        try
        {
            var svc = new ToutiaoVideoService(options);
            var raw = await svc.GetEditItemRawAsync(creds.Cookie, itemId, csrfToken);
            var rawPath = Path.Combine(Path.GetTempPath(), "toutiao_item_probe.json");
            try { File.WriteAllText(rawPath, raw); } catch { /* 原始落盘失败不影响报告 */ }

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                ? d
                : root;

            sb.AppendLine($"  Title={Str(data, "Title") ?? "(空)"}");
            sb.AppendLine($"  Abstract={Str(data, "Abstract") ?? "(空)"}");
            sb.AppendLine($"  VideoType={Num(data, "VideoType")}  IsLandscape={Str(data, "IsLandscape")}  "
                          + $"TimerStatus={Num(data, "TimerStatus")}  TimerTime={Num(data, "TimerTime")}  "
                          + $"ClaimOrigin={Num(data, "ClaimOrigin")}  Praise={Num(data, "Praise")}");
            if (data.TryGetProperty("VideoInfo", out var vi) && vi.ValueKind == JsonValueKind.Object)
                sb.AppendLine($"  VideoInfo.Vid={Str(vi, "Vid")}  VName={Str(vi, "VName")}");

            if (data.ValueKind == JsonValueKind.Object)
            {
                sb.AppendLine("  data 顶层键：" + string.Join(", ",
                    data.EnumerateObject().Select(p => p.Name)));
            }

            var hits = new List<string>();
            Walk(data, "data", hits, depth: 0);
            sb.AppendLine(hits.Count == 0
                ? "  hashtag 字段：(响应里一个都没有)"
                : $"  hashtag 字段 {hits.Count} 个：");
            foreach (var line in hits) sb.AppendLine("    " + line);

            sb.AppendLine($"  原始响应：{rawPath}");

            // ── 作品列表（内容管理）：HashtagList 只在这里回显 ──
            // EditItem 详情不带 HashtagList（2026-11-09 实测）；列表的
            // itemCell.extra.origin_content（JSON 字符串）→ ArticleAttr.ArticleItem.HashtagList。
            try
            {
                var userId = await svc.GetUserIdAsync(creds.Cookie, csrfToken);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    sb.AppendLine("  [作品列表] 拿不到 user_id，跳过");
                }
                else
                {
                    // status：0 全部 / 1 审核中 / 2 已发布 / 4 仅我可见（抓包口径）。
                    // 「全部」未必覆盖仅自己/审核中，逐个试到命中为止。
                    var listPath = Path.Combine(Path.GetTempPath(), "toutiao_list_probe.json");
                    JsonElement? hit = null;
                    var hitStatus = -1;
                    var summary = new List<string>();
                    foreach (var status in new[] { 0, 2, 4, 1 })
                    {
                        var listRaw = await svc.GetMpFeedRawAsync(
                            creds.Cookie, csrfToken, userId, "mp_videos",
                            keyword: "", offset: 0, count: 20, status: status);
                        try { File.WriteAllText(
                            Path.Combine(Path.GetTempPath(), $"toutiao_list_probe_s{status}.json"), listRaw); } catch { }

                        using var listDoc = JsonDocument.Parse(listRaw);
                        var rowCount = CountRows(listDoc.RootElement);
                        summary.Add($"status={status}:{rowCount}条");
                        if (rowCount <= 5)
                        {
                            foreach (var line in DescribeRows(listDoc.RootElement))
                                sb.AppendLine($"      · [s{status}] {line}");
                        }
                        var found = TryFindInList(listDoc.RootElement, itemId);
                        if (found is not null) { hit = found; hitStatus = status; break; }
                    }

                    sb.AppendLine("  [作品列表] " + string.Join(" / ", summary));
                    if (hit is null)
                    {
                        sb.AppendLine("  [作品列表] 各状态前 20 条里都没找到这个 ItemId");
                    }
                    else
                    {
                        var cell = hit.Value;
                        sb.AppendLine($"  [作品列表] 命中（status={hitStatus}）：");
                        sb.AppendLine($"    articleBase.title={Str(cell, "articleBase", "title") ?? "(空)"}  "
                                      + $"visibilityLevel={NumText(cell, "articleBase", "visibilityLevel")}");
                        if (cell.TryGetProperty("reviewInfo", out var review))
                            sb.AppendLine($"    reviewInfo={review.GetRawText()}");
                        sb.AppendLine("    HashtagList = " + DescribeHashtags(cell));
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  [作品列表失败] {ex.Message}");
                Log.Warning(ex, "[ToutiaoItemProbe] 作品列表回查失败");
            }

            return Write(sb, 0);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[失败] {ex.Message}");
            Log.Warning(ex, "[ToutiaoItemProbe] 拉取作品详情失败");
            return Write(sb, 1);
        }
    }

    /// <summary>递归收集名字含 hashtag 的字段（顶层 + data 任意层级），原样输出值。</summary>
    private static void Walk(JsonElement el, string path, List<string> hits, int depth)
    {
        if (depth > 8) return;

        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    var child = path + "." + p.Name;
                    if (p.Name.Contains("hashtag", StringComparison.OrdinalIgnoreCase))
                        hits.Add($"{child} = {Truncate(p.Value.GetRawText(), 800)}");
                    Walk(p.Value, child, hits, depth + 1);
                }
                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                {
                    if (i >= 20) break;
                    Walk(item, $"{path}[{i++}]", hits, depth + 1);
                }
                break;
        }
    }

    /// <summary>行摘要（gid / 标题 / visibilityLevel），空列表状态排查用。</summary>
    private static IEnumerable<string> DescribeRows(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            yield break;

        var n = 0;
        foreach (var row in data.EnumerateArray())
        {
            if (n++ >= 20) break;
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("assembleCell", out var ac)
                || ac.ValueKind != JsonValueKind.Object
                || !ac.TryGetProperty("itemCell", out var cell)
                || cell.ValueKind != JsonValueKind.Object
                || !cell.TryGetProperty("articleBase", out var ab))
                continue;

            var gid = Str(ab, "gidStr") ?? NumText(ab, "groupID");
            var title = Str(ab, "title") ?? "";
            yield return $"{gid} | {Truncate(title, 24)} | v{NumText(ab, "visibilityLevel")}";
        }
    }

    private static int CountRows(JsonElement root) =>
        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.GetArrayLength()
            : 0;

    /// <summary>在列表响应里找 itemId 对应的 <c>assembleCell.itemCell</c>。</summary>
    private static JsonElement? TryFindInList(JsonElement root, string itemId)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("assembleCell", out var ac)
                || ac.ValueKind != JsonValueKind.Object
                || !ac.TryGetProperty("itemCell", out var cell)
                || cell.ValueKind != JsonValueKind.Object
                || !cell.TryGetProperty("articleBase", out var ab))
                continue;

            var gid = Str(ab, "gidStr") ?? NumText(ab, "groupID");
            // Clone：JsonElement 绑定在 listDoc 上，循环里 using 释放后就不能再访问
            if (string.Equals(gid, itemId, StringComparison.Ordinal)) return cell.Clone();
        }
        return null;
    }

    /// <summary>
    /// 从 <c>itemCell.extra.origin_content</c>（JSON 字符串）里取
    /// <c>ArticleAttr.ArticleItem.HashtagList</c> —— 列表里的话题回显就藏在这一层。
    /// </summary>
    private static string DescribeHashtags(JsonElement cell)
    {
        if (!cell.TryGetProperty("extra", out var extra)
            || extra.ValueKind != JsonValueKind.Object
            || !extra.TryGetProperty("origin_content", out var oc)
            || oc.ValueKind != JsonValueKind.String)
            return "(没有 extra.origin_content)";

        var text = oc.GetString();
        if (string.IsNullOrWhiteSpace(text)) return "(origin_content 为空)";

        try
        {
            using var doc = JsonDocument.Parse(text);
            var node = doc.RootElement;
            if (node.ValueKind == JsonValueKind.Object
                && node.TryGetProperty("ArticleAttr", out var attr)) node = attr;
            if (node.ValueKind == JsonValueKind.Object
                && node.TryGetProperty("ArticleItem", out var item)) node = item;
            return node.ValueKind == JsonValueKind.Object
                   && node.TryGetProperty("HashtagList", out var hl)
                ? hl.GetRawText()
                : "(origin_content 里没有 HashtagList)";
        }
        catch (JsonException ex)
        {
            return $"(origin_content 解析失败：{ex.Message})";
        }
    }

    private static string? Str(JsonElement obj, string parent, string child) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(parent, out var p)
        && p.ValueKind == JsonValueKind.Object
            ? Str(p, child)
            : null;

    private static string NumText(JsonElement obj, string parent, string child) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(parent, out var p)
        && p.ValueKind == JsonValueKind.Object
            ? NumText(p, child)
            : "-";

    private static string NumText(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el)
            ? el.ValueKind switch
            {
                JsonValueKind.Number => el.GetRawText(),
                JsonValueKind.String => el.GetString() ?? "",
                _ => "",
            }
            : "";

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var el)
        && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string Num(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el)
            ? el.ValueKind switch
            {
                JsonValueKind.Number => el.GetRawText(),
                JsonValueKind.String => el.GetString() ?? "-",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => "-",
            }
            : "-";

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] + "…" : text;

    private static int Write(StringBuilder sb, int exitCode)
    {
        var path = Path.Combine(Path.GetTempPath(), "toutiao_item_probe.txt");
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
