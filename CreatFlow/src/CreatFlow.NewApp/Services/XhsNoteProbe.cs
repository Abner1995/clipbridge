using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Xiaohongshu;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 小红书笔记详情回读探针 —— <c>--probe-xhs-note &lt;noteId&gt;</c> 运行。**诊断工具，不进业务路径**。
///
/// 发布接口收下字段 ≠ 字段落到笔记上（头条 <c>HashtagList</c> 被静默忽略的教训）。
/// 本探针拉 <c>GET /web_api/sns/capa/postgw/note/detail</c>，把 <c>data</c> 里的
/// <c>hash_tag</c>（话题是否挂上）/ <c>privacy</c> / <c>post_time</c> / <c>post_locs</c> /
/// <c>ats</c>（@用户，V1）/ <c>cover.fileid</c> / <c>video_info</c> 写进
/// <c>%TEMP%\xhs_note_probe.txt</c>，
/// 原始 JSON 另存 <c>%TEMP%\xhs_note_probe.json</c>（2.6.10 遗留：hash_tag 回显未回读）。
///
/// **x-s 签名需要页面签名宿主**（mnsv2 只在页面 webpack 里）—— Avalonia 启动后由 App
/// 开一个隐藏 WebView（该账号档案）跑 <see cref="XhsPageSigner"/>，与快手回查探针同款。
///
/// 前提：服务端在跑（会话 Cookie 在服务端库里），本机已登录会员、绑定过小红书账号。
/// 可选 <c>--session &lt;guid&gt;</c> 指定账号；缺省取本地镜像里小红书的第一个账号。
/// <c>noteId</c> 传 <c>list</c> 时只列已发布笔记（找不到可回读的笔记时用）；
/// 传 <c>sketch</c> 时列速览合集（发布页「添加短剧」下拉的数据源）；
/// 传 <c>groups</c> 时列可绑定的群聊（发布页「选择群聊」，<c>group_list</c>）。
/// <c>--probe-xhs-route &lt;关键词1,关键词2&gt;</c> 走「添加路线」探针（POI 搜索 →
/// <c>save/travel</c>，见 <see cref="RunRouteAsync"/>）。
/// </summary>
public static class XhsNoteProbe
{
    /// <summary>发布页 —— creator 平台签名模块（mnsv2）随该页 webpack 一起加载</summary>
    private const string PublishPageUrl =
        "https://creator.xiaohongshu.com/publish/publish?source=official";

    public static async Task<int> RunAsync(string noteId, Guid? sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ 小红书笔记详情回读探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
        sb.AppendLine($"note_id={noteId}  session={sessionId?.ToString() ?? "-"}");

        var ctx = new SessionContext();
        if (ctx.MemberId == Guid.Empty)
        {
            sb.AppendLine("未登录 CreatFlow —— 先正常启动软件登录会员，再运行探针");
            return Write(sb, 3);
        }

        var store = new PlatformAccountStore(ctx);
        var sessions = store.Load().Where(r => r.PlatformKey == PlatformNames.Xiaohongshu).ToList();
        var chosen = sessionId is { } want
            ? sessions.FirstOrDefault(s => s.Id == want)
            : sessions.FirstOrDefault();

        Guid id;
        if (chosen is not null) id = chosen.Id;
        else if (sessionId is { } explicitId) id = explicitId;
        else
        {
            sb.AppendLine("本地没有小红书账号（sessions.json）—— 先绑定/登录一次");
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

        // 签名模块只在页面里 —— 开一个隐藏 WebView（该账号档案），用 XhsPageSigner
        // 接管 XhsPlatform.SetSigner，探针里的 SDK 请求就能签名。
        var profileDir = store.ProfileDir(id);
        var window = new Window
        {
            Title = $"小红书笔记回读探针 — {id}",
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
        view.Navigate(new Uri(PublishPageUrl));
        var pageSigner = new XhsPageSigner(view);
        XhsPlatform.SetSigner((urlPath, bodyJson, _, ct) =>
            pageSigner.SignAsync(urlPath, bodyJson, ct));

        var options = new PlatformOptions
        {
            Environment = ParseEnv(creds.ExtraDataJson),
            SessionId = id,
        };

        try
        {
            var signerReady = await pageSigner.PrepareAsync(CancellationToken.None);
            sb.AppendLine($"  签名宿主：{(signerReady ? "就绪" : "不可用（请求可能 406）")}");
            sb.AppendLine($"  会话 {id}  账号 {creds.PlatformName ?? creds.PlatformUid ?? "?"}");

            // noteId 传 "sketch" → 列速览合集（发布页「添加短剧」下拉的数据源；
            // 接口 get /api/sns/v1/note/collection/pc/list?cursor=&type=1&mode=detail）
            if (string.Equals(noteId, "sketch", StringComparison.OrdinalIgnoreCase))
            {
                var page = await new XhsCollectionService(options)
                    .ListSketchCollectionsAsync(creds.Cookie, ct: CancellationToken.None);
                sb.AppendLine($"  速览合集 {page.Items.Count} 条  nextCursor={page.NextCursor}");
                foreach (var it in page.Items)
                {
                    sb.AppendLine($"    · {it.Id}  {Truncate(it.Name, 40)}  "
                                  + $"集数={it.NoteNum}/{it.TotalEpisodes}  满={it.IsFull}");
                }
                sb.AppendLine($"  原始响应：{Truncate(page.RawJson, 1500)}");
                return Write(sb, 0);
            }

            // noteId 传 "groups" → 列账号可绑定的群聊（发布页「选择群聊」的数据源；
            // 接口 get /api/im/web/nns/group_list?noteId=）。账号没群时返回空列表。
            if (string.Equals(noteId, "groups", StringComparison.OrdinalIgnoreCase))
            {
                var page = await new XhsGroupService(options)
                    .ListAsync(creds.Cookie, noteId: "", ct: CancellationToken.None);
                sb.AppendLine($"  群聊 {page.Items.Count} 个");
                foreach (var g in page.Items)
                {
                    sb.AppendLine($"    · groupId={g.GroupId}  {Truncate(g.GroupName, 40)}  "
                                  + $"linkable={g.Linkable}  desc={Truncate(g.Desc ?? "-", 40)}  "
                                  + $"avatar={(string.IsNullOrEmpty(g.Avatar) ? "-" : "有")}");
                }
                sb.AppendLine($"  原始响应：{Truncate(page.RawJson, 2500)}");
                return Write(sb, 0);
            }

            // noteId 传 "list" → 列出已发布笔记（找一条还在的回读，免得笔记被清理后只能猜）
            if (string.Equals(noteId, "list", StringComparison.OrdinalIgnoreCase))
            {
                var svc = new XhsVideoService(options);
                var noteSvc = new XhsNoteService(options);
                foreach (var (tab, label) in new[] { (0, "全部"), (1, "已发布"), (2, "审核中") })
                {
                    // 先看原文（区分「真的 0 条」与「接口拒了/形状变了」）
                    var raw = await noteSvc.ListPostedRawAsync(creds.Cookie, tab: tab);
                    sb.AppendLine($"  [{label} tab={tab}] 原始响应：{Truncate(raw.GetRawText(), 500)}");

                    var items = await svc.ListAsync(creds.Cookie, tab: tab);
                    sb.AppendLine($"  [{label} tab={tab}] 笔记 {items.Count} 条：");
                    foreach (var it in items)
                    {
                        var nid = string.IsNullOrWhiteSpace(it.NoteId) ? it.Id : it.NoteId;
                        sb.AppendLine($"    · {nid}  type={it.Type}  "
                                      + $"title={Truncate(it.DisplayTitle ?? it.Title ?? "", 40)}  "
                                      + $"time={FormatUnix(it.Time > 0 ? it.Time.ToString() : null)}");
                    }
                }
                return Write(sb, 0);
            }

            var root = await new XhsNoteService(options).GetDetailAsync(
                creds.Cookie, noteId, CancellationToken.None);

            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                sb.AppendLine("  data 节点缺失或不是对象 —— 原始响应见 .json");
                DumpRaw(root);
                return Write(sb, 1);
            }

            sb.AppendLine($"  data 顶层键：{string.Join(", ", data.EnumerateObject().Select(x => x.Name))}");
            sb.AppendLine($"  id={Str(data, "id")}  type={Str(data, "type")}");
            sb.AppendLine($"  title={Str(data, "title") ?? Str(data, "name") ?? "(空)"}");
            sb.AppendLine($"  desc={Truncate(Str(data, "desc") ?? "(空)", 300)}");
            sb.AppendLine($"  privacy={Raw(data, "privacy")}");
            sb.AppendLine($"  post_time={Str(data, "post_time")}（{FormatUnix(Str(data, "post_time"))}）");
            sb.AppendLine($"  hash_tag={Raw(data, "hash_tag")}");
            // @用户（V1）：发布体 common.ats，详情回读在 data.ats（数组）
            sb.AppendLine($"  ats={Raw(data, "ats")}");
            sb.AppendLine($"  post_locs={Raw(data, "post_locs")}");
            sb.AppendLine($"  cover.fileid={Str(data, "cover", "fileid")}");
            sb.AppendLine($"  video_info={Raw(data, "video_info")}");
            sb.AppendLine($"  business_binds={Raw(data, "business_binds")}");

            // 兜底：响应里所有名字含 hash/topic/tag 的字段（平台键名换过也要看得见）
            foreach (var (k, v) in FlattenKeysContaining(data, "hash", "topic", "tag"))
                sb.AppendLine($"  ★ {k} = {Truncate(v.GetRawText(), 800)}");

            DumpRaw(root);
            return Write(sb, 0);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[失败] {ex.Message}");
            Log.Warning(ex, "[XhsNoteProbe] 拉取笔记详情失败");
            return Write(sb, 1);
        }
        finally
        {
            // 恢复 App 注入的签名器（探针进程随后退出，这里只是保持语义）
            XhsPlatform.SetSigner((urlPath, bodyJson, sessionId2, ct) =>
                XhsSignerService.SignAsync(sessionId2, urlPath, bodyJson, ct));
            try { window.Close(); } catch { }
        }
    }

    /// <summary>
    /// 视频标记标签搜索探针 —— <c>--probe-xhs-label &lt;关键词&gt; [--label-type user|poi]</c>。
    /// 只读：打 <c>/web_api/sns/v1/search/label</c> 并落原始响应
    /// （<c>%TEMP%\xhs_label_probe.txt</c>），用于补「标记地点/标记朋友」的响应结构。
    /// </summary>
    public static async Task<int> RunLabelAsync(
        string keyword, string type, Guid? sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ 小红书标记标签搜索探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
        sb.AppendLine($"keyword={keyword}  type={type}  session={sessionId?.ToString() ?? "-"}");

        var ctx = new SessionContext();
        if (ctx.MemberId == Guid.Empty)
        {
            sb.AppendLine("未登录 CreatFlow —— 先正常启动软件登录会员，再运行探针");
            return Write(sb, 3);
        }

        var store = new PlatformAccountStore(ctx);
        var sessions = store.Load().Where(r => r.PlatformKey == PlatformNames.Xiaohongshu).ToList();
        var chosen = sessionId is { } want
            ? sessions.FirstOrDefault(s => s.Id == want)
            : sessions.FirstOrDefault();

        Guid id;
        if (chosen is not null) id = chosen.Id;
        else if (sessionId is { } explicitId) id = explicitId;
        else
        {
            sb.AppendLine("本地没有小红书账号（sessions.json）—— 先绑定/登录一次");
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

        var profileDir = store.ProfileDir(id);
        var window = new Window
        {
            Title = $"小红书标记标签探针 — {id}",
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
        view.Navigate(new Uri(PublishPageUrl));
        var pageSigner = new XhsPageSigner(view);
        XhsPlatform.SetSigner((urlPath, bodyJson, _, ct) =>
            pageSigner.SignAsync(urlPath, bodyJson, ct));

        var options = new PlatformOptions
        {
            Environment = ParseEnv(creds.ExtraDataJson),
            SessionId = id,
        };

        try
        {
            var signerReady = await pageSigner.PrepareAsync(CancellationToken.None);
            sb.AppendLine($"  签名宿主：{(signerReady ? "就绪" : "不可用（请求可能 406）")}");
            sb.AppendLine($"  会话 {id}  账号 {creds.PlatformName ?? creds.PlatformUid ?? "?"}");

            var root = await new XhsLabelService(options)
                .SearchRawAsync(creds.Cookie, keyword, type, CancellationToken.None);

            sb.AppendLine($"  原始响应：{Truncate(root.GetRawText(), 6000)}");
            if (root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object)
            {
                sb.AppendLine($"  data 顶层键：{string.Join(", ", data.EnumerateObject().Select(x => x.Name))}");
                foreach (var prop in data.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        sb.AppendLine($"  {prop.Name}[] 共 {prop.Value.GetArrayLength()} 条，"
                                      + $"首条：{Truncate(prop.Value.GetArrayLength() > 0
                                          ? prop.Value[0].GetRawText() : "(空)", 1200)}");
                    }
                }
            }

            try
            {
                var path = Path.Combine(Path.GetTempPath(), "xhs_label_probe.json");
                File.WriteAllText(path, root.GetRawText());
                sb.AppendLine($"  原始 JSON：{path}");
            }
            catch { /* 落盘失败不影响主流程 */ }

            return Write(sb, 0);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[失败] {ex.Message}");
            Log.Warning(ex, "[XhsNoteProbe] 标记标签搜索失败");
            return Write(sb, 1);
        }
        finally
        {
            XhsPlatform.SetSigner((urlPath, bodyJson, sessionId2, ct) =>
                XhsSignerService.SignAsync(sessionId2, urlPath, bodyJson, ct));
            try { window.Close(); } catch { }
        }
    }

    /// <summary>
    /// 小红书「添加路线」探针 —— <c>--probe-xhs-route &lt;关键词1,关键词2[,关键词3]&gt;</c>
    /// <c>[--route-name &lt;名字&gt;]</c>。回答两件事（正式代码/UI 之前必须先知道）：
    /// ① 账号有没有建路线的权限（页面开关 <c>isCanUseTravel</c>）；
    /// ② <c>save/travel</c> 的 <c>polygon</c> 能不能给空数组（能空就不必接 route/v2）。
    ///
    /// 流程：POI 搜索 → <c>simple/markers</c> 换坐标 → 逐段 <c>route/v2</c>（取 polygon）→
    /// <c>save/travel</c> 依次试「空 polygon」→「算路 polygon」→「算路 polygon + walkingRoute」，
    /// 第一个返回 <c>travelId</c> 的变体即成功；成功后 <c>travel/detail</c> 回读。
    /// 报告 <c>%TEMP%\xhs_route_probe.txt</c>，各步原始响应 <c>%TEMP%\xhs_route_*.json</c>。
    /// </summary>
    public static async Task<int> RunRouteAsync(
        string keywordsCsv, string? routeName, Guid? sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ 小红书添加路线探针 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
        sb.AppendLine($"keywords={keywordsCsv}  routeName={routeName ?? "(默认)"}  "
                      + $"session={sessionId?.ToString() ?? "-"}");

        var ctx = new SessionContext();
        if (ctx.MemberId == Guid.Empty)
        {
            sb.AppendLine("未登录 CreatFlow —— 先正常启动软件登录会员，再运行探针");
            return Write(sb, 3);
        }

        var store = new PlatformAccountStore(ctx);
        var sessions = store.Load().Where(r => r.PlatformKey == PlatformNames.Xiaohongshu).ToList();
        var chosen = sessionId is { } want
            ? sessions.FirstOrDefault(s => s.Id == want)
            : sessions.FirstOrDefault();

        Guid id;
        if (chosen is not null) id = chosen.Id;
        else if (sessionId is { } explicitId) id = explicitId;
        else
        {
            sb.AppendLine("本地没有小红书账号（sessions.json）—— 先绑定/登录一次");
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

        var profileDir = store.ProfileDir(id);
        var window = new Window
        {
            Title = $"小红书添加路线探针 — {id}",
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
        view.Navigate(new Uri(PublishPageUrl));
        var pageSigner = new XhsPageSigner(view);
        XhsPlatform.SetSigner((urlPath, bodyJson, _, ct) =>
            pageSigner.SignAsync(urlPath, bodyJson, ct));

        var options = new PlatformOptions
        {
            Environment = ParseEnv(creds.ExtraDataJson),
            SessionId = id,
        };

        try
        {
            var signerReady = await pageSigner.PrepareAsync(CancellationToken.None);
            sb.AppendLine($"  签名宿主：{(signerReady ? "就绪" : "不可用（请求可能 406）")}");
            sb.AppendLine($"  会话 {id}  账号 {creds.PlatformName ?? creds.PlatformUid ?? "?"}");

            var userId = CredentialFields.CookieValue(
                creds.Cookie, "x-user-id-creator.xiaohongshu.com") ?? creds.PlatformUid;
            sb.AppendLine($"  userId={userId ?? "(没取到 —— save/travel 需要它)"}");
            if (string.IsNullOrWhiteSpace(userId))
            {
                sb.AppendLine("[失败] Cookie 里没有 x-user-id-creator，无法拼 save/travel 请求体");
                return Write(sb, 6);
            }

            // 页面同款：没有定位时用固定中心（创建页/路线页的默认 fallback）。
            const double centerLng = 116.397027;
            const double centerLat = 39.91799;
            var userCenter = $"{centerLng.ToString(CultureInfo.InvariantCulture)},"
                             + $"{centerLat.ToString(CultureInfo.InvariantCulture)}";
            sb.AppendLine($"  搜索中心（固定）：{userCenter}");

            var routes = new XhsRouteService(options);
            var keywords = keywordsCsv.Split(
                [',', '，', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // ① POI 搜索 + simple/markers 换坐标
            var pois = new List<(string Id, string Name, double Lon, double Lat)>();
            foreach (var kw in keywords)
            {
                var search = await routes.SearchPoiRawAsync(
                    creds.Cookie, kw, centerLng, centerLat, CancellationToken.None);
                var searchPath = Dump("xhs_route_poi.json", search);
                var list = ParsePoiList(search);
                sb.AppendLine($"  POI「{kw}」{list.Count} 条（原始 {searchPath}）");
                foreach (var it in list.Take(3))
                    sb.AppendLine($"    · {it.Id}  {Truncate(it.Name, 30)}  {Truncate(it.Address, 40)}");
                if (list.Count == 0) continue;

                var pick = list[0];
                var markers = await routes.MarkersRawAsync(
                    creds.Cookie, pick.Id, userCenter, CancellationToken.None);
                var markersPath = Dump("xhs_route_markers.json", markers);
                var coords = ParseMarkerCoords(markers);
                if (coords is not { } c)
                {
                    sb.AppendLine($"    ✗ simple/markers 没给坐标（原始 {markersPath}）");
                    continue;
                }
                sb.AppendLine($"    ✓ 坐标 {c.Lon},{c.Lat}  {Truncate(c.Name ?? pick.Name, 30)}");
                pois.Add((pick.Id, c.Name ?? pick.Name, c.Lon, c.Lat));
            }

            if (pois.Count < 2)
            {
                sb.AppendLine($"[失败] 可用地点只有 {pois.Count} 个 —— 路线至少需要 2 个地点"
                              + "（换两个有搜索结果的 POI 关键词再试）");
                return Write(sb, 1);
            }

            // ② 逐段算路（只为拿 polygon；失败不致命，还有空 polygon 变体）
            var polygonPoints = new List<object>();
            for (var i = 0; i + 1 < pois.Count; i++)
            {
                var origin = $"{pois[i].Lon.ToString(CultureInfo.InvariantCulture)},"
                             + $"{pois[i].Lat.ToString(CultureInfo.InvariantCulture)}";
                var destination = $"{pois[i + 1].Lon.ToString(CultureInfo.InvariantCulture)},"
                                  + $"{pois[i + 1].Lat.ToString(CultureInfo.InvariantCulture)}";
                try
                {
                    var routeRaw = await routes.RouteRawAsync(
                        creds.Cookie, origin, destination, userCenter, CancellationToken.None);
                    var routePath = Dump($"xhs_route_route_{i}.json", routeRaw);
                    var polygons = FindPolygonArrays(routeRaw).ToList();
                    sb.AppendLine($"  route {i}→{i + 1}：polygon 数组 {polygons.Count} 个"
                                  + $"（原始 {routePath}）");
                    if (polygons.Count > 0)
                        sb.AppendLine($"    首个 polygon：{Truncate(polygons[0].GetRawText(), 200)}");
                    if (polygonPoints.Count == 0 && polygons.Count > 0)
                        polygonPoints = PolygonPoints(polygons[0]);
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  route {i}→{i + 1} 失败（继续）：{ex.Message}");
                }
            }

            // ③ save/travel：空 polygon 优先（决定正式实现要不要接 route/v2）
            var name = string.IsNullOrWhiteSpace(routeName)
                ? $"探针路线{DateTime.Now:MMddHHmm}"
                : routeName!;
            var variants = new List<(string Label, List<object> Polygon, bool WithTransport)>
            {
                ("空polygon", [], false),
            };
            if (polygonPoints.Count > 0)
                variants.Add(("算路polygon", polygonPoints, false));
            variants.Add(("算路polygon+walkingRoute", polygonPoints, true));

            foreach (var (label, polygon, withTransport) in variants)
            {
                var body = BuildSaveTravelBody(
                    userId, name, pois, polygon, withTransport);
                var bodyJson = JsonSerializer.Serialize(body);
                sb.AppendLine($"  save[{label}] 请求体：{Truncate(bodyJson, 1200)}");
                try
                {
                    var resp = await routes.SaveTravelRawAsync(
                        creds.Cookie, body, CancellationToken.None);
                    var respPath = Dump("xhs_route_save.json", resp);
                    var travelId = TryTravelId(resp);
                    sb.AppendLine($"  save[{label}] 响应：{Truncate(resp.GetRawText(), 1200)}");
                    if (string.IsNullOrWhiteSpace(travelId))
                    {
                        sb.AppendLine($"    ✗ 没有 travelId（原始 {respPath}）—— 试下一个变体");
                        continue;
                    }

                    sb.AppendLine($"  ✅ 建路线成功 [{label}] travelId={travelId}（原始 {respPath}）");

                    try
                    {
                        var detail = await routes.TravelDetailRawAsync(
                            creds.Cookie, travelId, CancellationToken.None);
                        var detailPath = Dump("xhs_route_detail.json", detail);
                        sb.AppendLine($"  travel/detail：{Truncate(detail.GetRawText(), 1500)}"
                                      + $"（原始 {detailPath}）");
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  travel/detail 失败（不影响结论）：{ex.Message}");
                    }

                    sb.AppendLine($"  结论：polygon {(polygon.Count == 0 ? "可为空" : "带算路 bbox")}、"
                                  + $"selectedRouteType {(withTransport ? "需填" : "可省")}、"
                                  + "账号可建路线（未验证发布落位）");
                    return Write(sb, 0);
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  save[{label}] 失败：{ex.Message}");
                }
            }

            sb.AppendLine("[失败] 三种 save/travel 变体都没拿到 travelId —— "
                          + "看上面各变体的响应（权限 / 字段 / 风控）");
            return Write(sb, 1);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[失败] {ex.Message}");
            Log.Warning(ex, "[XhsNoteProbe] 添加路线探针失败");
            return Write(sb, 1);
        }
        finally
        {
            XhsPlatform.SetSigner((urlPath, bodyJson, sessionId2, ct) =>
                XhsSignerService.SignAsync(sessionId2, urlPath, bodyJson, ct));
            try { window.Close(); } catch { }
        }
    }

    private static Dictionary<string, object?> BuildSaveTravelBody(
        string userId,
        string travelName,
        IReadOnlyList<(string Id, string Name, double Lon, double Lat)> pois,
        IReadOnlyList<object> polygon,
        bool withTransport)
    {
        var elements = new List<object?>();
        for (var i = 0; i < pois.Count; i++)
        {
            var element = new Dictionary<string, object?>
            {
                ["id"] = pois[i].Id,
                ["type"] = "poi",
                ["lon"] = pois[i].Lon,
                ["lat"] = pois[i].Lat,
            };
            // 页面只在用户选了交通方式时才带 selectedRouteType（没选 = 键缺失）；
            // 最后一段恒空。⚠️ wire 是 snake_case（页面 HTTP 层 transform:true）。
            if (withTransport && i < pois.Count - 1)
                element["selected_route_type"] = "walkingRoute";
            elements.Add(element);
        }

        return new Dictionary<string, object?>
        {
            ["save_type"] = 2, // iu.Finish（1=Save 草稿）
            ["travel_id"] = "",
            ["travel_type"] = "singleRoute",
            ["travel_name"] = travelName,
            ["user_id"] = userId,
            ["plans"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["plan_name"] = "DAY 1",
                    ["plan_img"] = "",
                    ["plan_marker_img"] = "",
                    ["elements"] = elements,
                },
            },
            ["polygon"] = polygon,
            ["travel_img"] = "",
        };
    }

    private static List<(string Id, string Name, string Address)> ParsePoiList(JsonElement root)
    {
        var result = new List<(string, string, string)>();
        if (!TryArray(root, "poiList", out var list) && !TryArray(root, "poi_list", out list))
            return result;
        foreach (var it in list.EnumerateArray())
        {
            var poiId = Str(it, "poiId") ?? Str(it, "poi_id") ?? Str(it, "id");
            if (string.IsNullOrWhiteSpace(poiId)) continue;
            result.Add((poiId,
                Str(it, "poiName") ?? Str(it, "poi_name") ?? Str(it, "name") ?? "",
                Str(it, "address") ?? Str(it, "fullAddress") ?? Str(it, "full_address") ?? ""));
        }
        return result;
    }

    private static (double Lon, double Lat, string? Name)? ParseMarkerCoords(JsonElement root)
    {
        if (!TryArray(root, "markers", out var markers) || markers.GetArrayLength() == 0)
            return null;
        var m = markers[0];
        if (!TryNumber(m, "longitude", out var lon) || !TryNumber(m, "latitude", out var lat))
            return null;
        string? name = null;
        if (m.TryGetProperty("poi", out var poi) && poi.ValueKind == JsonValueKind.Object)
            name = Str(poi, "poiName") ?? Str(poi, "poi_name") ?? Str(poi, "name");
        return (lon, lat, name);
    }

    private static bool TryArray(JsonElement obj, string name, out JsonElement array)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array)
            return true;
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array)
            return true;
        array = default;
        return false;
    }

    private static bool TryNumber(JsonElement obj, string name, out double value)
    {
        value = 0;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
            return false;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static string? TryTravelId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if ((Str(root, "travelId") ?? Str(root, "travel_id")) is { Length: > 0 } direct) return direct;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && (Str(data, "travelId") ?? Str(data, "travel_id")) is { Length: > 0 } nested)
            return nested;
        return null;
    }

    /// <summary>路由响应里名字叫 polygon、且元素是 "lng,lat" 字符串的数组（形状未定，递归找）。</summary>
    private static IEnumerable<JsonElement> FindPolygonArrays(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Name.Equals("polygon", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.Array
                    && prop.Value.GetArrayLength() >= 2
                    && prop.Value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String))
                    yield return prop.Value;
                foreach (var nested in FindPolygonArrays(prop.Value))
                    yield return nested;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                foreach (var nested in FindPolygonArrays(item))
                    yield return nested;
        }
    }

    /// <summary>"[lng,lat","lng,lat"]" → 页面同款 <c>{longitude,latitude,time:0}</c> 点列。</summary>
    private static List<object> PolygonPoints(JsonElement polygon)
    {
        var points = new List<object>();
        foreach (var s in polygon.EnumerateArray())
        {
            var parts = (s.GetString() ?? "").Split(',');
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lng)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
                continue;
            points.Add(new Dictionary<string, object?>
            {
                ["longitude"] = lng,
                ["latitude"] = lat,
                ["time"] = 0,
            });
        }
        return points;
    }

    private static string Dump(string fileName, JsonElement root)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllText(path, root.GetRawText());
            return path;
        }
        catch
        {
            return "(落盘失败)";
        }
    }

    private static void DumpRaw(JsonElement root)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "xhs_note_probe.json");
            File.WriteAllText(path, root.GetRawText());
        }
        catch { /* 落盘失败不影响主流程 */ }
    }

    private static IEnumerable<(string Key, JsonElement Value)> FlattenKeysContaining(
        JsonElement obj, params string[] needles)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                if (needles.Any(n => prop.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                    yield return (prop.Name, prop.Value);
            }
        }
    }

    private static string? Str(JsonElement obj, params string[] path)
    {
        var cur = obj;
        foreach (var key in path)
        {
            if (cur.ValueKind != JsonValueKind.Object
                || !cur.TryGetProperty(key, out cur))
                return null;
        }
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            JsonValueKind.Number => cur.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string Raw(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) ? Truncate(v.GetRawText(), 600) : "(无)";

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] + "…" : text;

    private static string FormatUnix(string? raw) =>
        long.TryParse(raw, out var sec) && sec > 0
            ? DateTimeOffset.FromUnixTimeSeconds(sec).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "-";

    private static int Write(StringBuilder sb, int exitCode)
    {
        var path = Path.Combine(Path.GetTempPath(), "xhs_note_probe.txt");
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
