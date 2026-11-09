using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Kuaishou;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 快手作品回查探针 —— <c>--probe-kuaishou-work</c> 运行。**诊断工具，不进业务路径**。
///
/// 快手私密作品没有分享页、发布接口也不回 ID（2.6.10 遗留），本探针从**创作中心作品列表**
/// （<c>/rest/cp/works/v2/video/pc/photo/list</c>）回读：
///   · 行的 <c>hotspotActivity</c> / <c>hotspot</c> —— 官方活动是否挂上（模型未建模，走 ExtraFields）；
///   · 行的 <c>title</c> —— 正文里的 <c>#话题</c> 文本；
///   · <c>workId</c> —— 公开作品可拼 <c>m.gifshow.com/fw/photo/{id}</c> 分享页，进一步看 SSR 里的
///     <c>INIT_STATE</c>（<c>--photo</c> 显式传入或由行里的 workId 自动尝试）。
///
/// 用法：
///   <c>--probe-kuaishou-work --publish-id 3710051890</c>
///   <c>--probe-kuaishou-work --keyword 家居</c>
///   <c>--probe-kuaishou-work --photo &lt;photoId&gt;</c>（只拉分享页）
/// 可选 <c>--session &lt;guid&gt;</c>；缺省取本地镜像里快手的第一个账号。
/// 报告写 <c>%TEMP%\kuaishou_work_probe.txt</c>。
/// </summary>
public static class KuaishouWorkProbe
{
    private const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 " +
        "(KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1";

    public static async Task<int> RunAsync(
        string? keyword, long? publishId, string? photoId, Guid? sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ 快手作品回查探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
        sb.AppendLine($"keyword={keyword ?? "-"}  publish_id={publishId?.ToString() ?? "-"}  "
                      + $"photo={photoId ?? "-"}  session={sessionId?.ToString() ?? "-"}");

        var ctx = new SessionContext();
        if (ctx.MemberId == Guid.Empty)
        {
            sb.AppendLine("未登录 CreatFlow —— 先正常启动软件登录会员，再运行探针");
            return Write(sb, 3);
        }

        var store = new PlatformAccountStore(ctx);
        var sessions = store.Load().Where(r => r.PlatformKey == PlatformNames.Kuaishou).ToList();
        var chosen = sessionId is { } want
            ? sessions.FirstOrDefault(s => s.Id == want)
            : sessions.FirstOrDefault();

        Guid id;
        if (chosen is not null) id = chosen.Id;
        else if (sessionId is { } explicitId) id = explicitId;
        else
        {
            sb.AppendLine("本地没有快手账号（sessions.json）—— 先绑定/登录一次");
            return Write(sb, 4);
        }

        PlatformSessionCredentials? creds;
        try { creds = await PlatformSessionApi.GetCredentialsAsync(id); }
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

        // 签名模块只在页面 webpack 里 —— 开一个隐藏 WebView（该账号档案），
        // 用 KuaishouPageSigner 接管 KuaishouPlatform.SetSigner，探针里的 SDK 请求就能签名。
        var profileDir = store.ProfileDir(id);
        var window = new Window
        {
            Title = $"快手作品回查探针 — {id}",
            Width = 900,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
        };
        var view = new NativeWebView();
        window.Content = view;
        view.EnvironmentRequested += (_, args) =>
        {
            if (args is WindowsWebView2EnvironmentRequestedEventArgs wv)
            {
                wv.UserDataFolder = profileDir;
                wv.ProfileName = "main";
            }
        };
        window.Show();
        view.Navigate(new Uri("https://cp.kuaishou.com/article/publish/video"));
        var pageSigner = new KuaishouPageSigner(view);
        KuaishouPlatform.SetSigner((_, reqJson, ct) => pageSigner.SignAsync(reqJson, ct));

        try
        {
            var signerReady = await pageSigner.PrepareAsync(CancellationToken.None);
            sb.AppendLine($"  签名宿主：{(signerReady ? "就绪" : "不可用（回查请求可能被拒）")}");

            var extra = CredentialFields.Extra(creds.ExtraDataJson);
        var apiPh = extra.GetValueOrDefault("apiPh")?.ToString()
            ?? CredentialFields.CookieValue(creds.Cookie, "kuaishou.web.cp.api_ph");
        var options = new PlatformOptions
        {
            Environment = ParseEnv(creds.ExtraDataJson),
            SessionId = id,
        };

        sb.AppendLine($"  会话 {id}  账号 {creds.PlatformName ?? creds.PlatformUid ?? "?"}");

        var autoPhoto = new List<string>();

        // 没给 --photo（或还给了别的条件）就先拉列表；只给 --photo 时直奔分享页。
        if (publishId is not null || !string.IsNullOrWhiteSpace(keyword)
            || string.IsNullOrWhiteSpace(photoId))
        {
            try
            {
                var svc = new KuaishouVideoService(options);
                // 参数就用**生产默认**（queryType=0 / 前后 180 天 / 远期 cursor）。2026-11-09
                // 用内容管理页的抓包原文核对过：页面就是这套口径（startTime 一年内），
                // 探针早期改的 `queryType=2 + startTime 超一年` 反而回 result=1000。
                // 这时若仍失败，多半是凭证过期（109 = 缺 api_st，500002 = api_st 过期），
                // 不是参数问题 —— 见 CredentialHint。
                var list = await svc.ListAsync(
                    creds.Cookie,
                    limit: 30,
                    keyword: keyword ?? "",
                    apiPh: apiPh);

                var rows = list.Items.Where(r =>
                        publishId is { } pid
                            ? r.PublishId == pid
                            : !string.IsNullOrWhiteSpace(keyword)
                              && (string.Equals(r.Title, keyword, StringComparison.Ordinal)
                                  || (r.Title?.Contains(keyword, StringComparison.Ordinal) ?? false)))
                    .ToList();

                sb.AppendLine($"  作品列表 total={list.Total}  命中 {rows.Count} 行"
                              + (rows.Count == 0 ? "（未命中，下面给前 10 行做参考）" : ""));
                if (rows.Count == 0) rows = list.Items.Take(10).ToList();

                foreach (var r in rows)
                {
                    sb.AppendLine($"  ── publishId={r.PublishId}  workId={r.WorkId ?? "(null)"}  "
                                  + $"publishStatus={r.PublishStatus}  photoStatus={r.PhotoStatus}  "
                                  + $"collection={r.CollectionId}/{r.CollectionTitle ?? "-"}");
                    sb.AppendLine($"     title={r.Title ?? "(空)"}");
                    if (!string.IsNullOrWhiteSpace(r.WorkId)) autoPhoto.Add(r.WorkId!);
                    if (r.ExtraFields is { Count: > 0 })
                    {
                        foreach (var (k, v) in r.ExtraFields.OrderBy(x => x.Key))
                            sb.AppendLine($"     {k} = {Truncate(v.GetRawText(), 600)}");
                    }
                }
            }
            catch (KuaishouApiException ex)
            {
                sb.AppendLine($"[作品列表失败] result={ex.ResultCode} {ex.Message}");
                var hint = KuaishouVideoService.CredentialHint(ex.ResultCode);
                if (hint is not null) sb.AppendLine("  → " + hint);
                Log.Warning(ex, "[KuaishouWorkProbe] 作品列表请求失败 result={Code}", ex.ResultCode);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[作品列表失败] {ex.Message}");
                Log.Warning(ex, "[KuaishouWorkProbe] 作品列表请求失败");
            }
        }

        if (!string.IsNullOrWhiteSpace(photoId)) autoPhoto.Insert(0, photoId!);
        foreach (var pid in autoPhoto.Distinct().Take(3))
        {
            sb.AppendLine();
            await DumpSharePageAsync(sb, pid);
        }

            return Write(sb, 0);
        }
        finally
        {
            // 恢复 App 注入的签名器（探针进程随后退出，这里只是保持语义）
            KuaishouPlatform.SetSigner((sessionId, reqJson, ct) =>
                KuaishouSignerService.SignAsync(sessionId, reqJson, ct));
            try { window.Close(); } catch { }
        }
    }

    // ═══ 分享页（公开作品）════

    private static async Task DumpSharePageAsync(StringBuilder sb, string photoId)
    {
        var url = "https://m.gifshow.com/fw/photo/" + photoId;
        sb.AppendLine($"  ── 分享页 {url}");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", MobileUserAgent);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://m.gifshow.com/");
            var html = await http.GetStringAsync(url);

            var initJson = TryExtractInitState(html);
            if (initJson is null)
            {
                sb.AppendLine($"     INIT_STATE 未找到（HTML {html.Length} 字符 —— 私密/已删/风控都可能）");
                return;
            }

            using var doc = JsonDocument.Parse(initJson);
            var photo = FindPhotoObject(doc.RootElement);
            if (photo is null)
            {
                sb.AppendLine("     INIT_STATE 里没有作品对象（私密/已删）");
                return;
            }

            var p = photo.Value;
            sb.AppendLine($"     caption={Str(p, "caption") ?? "(空)"}");
            sb.AppendLine("     作品对象键：" + string.Join(", ", p.EnumerateObject().Select(x => x.Name)));
            foreach (var prop in p.EnumerateObject())
            {
                if (prop.Name.Contains("tag", StringComparison.OrdinalIgnoreCase)
                    || prop.Name.Contains("hotspot", StringComparison.OrdinalIgnoreCase)
                    || prop.Name.Contains("activity", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"     {prop.Name} = {Truncate(prop.Value.GetRawText(), 800)}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"     [分享页失败] {ex.Message}");
        }
    }

    /// <summary>取 <c>window.INIT_STATE = { ... }</c> 的 JSON 本体（按括号配对，避开字符串里的花括号）。</summary>
    private static string? TryExtractInitState(string html)
    {
        var marker = html.IndexOf("window.INIT_STATE", StringComparison.Ordinal);
        if (marker < 0) return null;
        var start = html.IndexOf('{', marker);
        if (start < 0) return null;
        var end = FindMatchingBrace(html, start);
        return end < 0 ? null : html[start..(end + 1)];
    }

    private static int FindMatchingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>递归找作品对象（含 <c>photoType</c> + <c>photoId</c> 的那个）。</summary>
    private static JsonElement? FindPhotoObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("photoType", out _)
                    && element.TryGetProperty("photoId", out _))
                    return element;
                foreach (var property in element.EnumerateObject())
                {
                    var found = FindPhotoObject(property.Value);
                    if (found.HasValue) return found;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var found = FindPhotoObject(item);
                    if (found.HasValue) return found;
                }
                break;
        }
        return null;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] + "…" : text;

    private static int Write(StringBuilder sb, int exitCode)
    {
        var path = Path.Combine(Path.GetTempPath(), "kuaishou_work_probe.txt");
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
