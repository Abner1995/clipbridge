using System.Text.Encodings.Web;

namespace CreatFlow.Platforms.Douyin;

/// <summary>
/// 抖音「添加标签」类型（2026-11-11 抓包：<c>item.anchor.anchor</c> 的单选 <c>type</c>）。
/// 四类已接线：影视演艺 / 游戏手柄 / 标记万物 / 小程序（小程序搜索 2026-11-11 补抓，
/// 发布体形状来自发布页 bundle 逐字，真账号回读待 P5）。
/// </summary>
public static class DouyinAnchorTypes
{
    /// <summary>影视演艺 —— 抓包 <c>{"id":"6531…","type":11,"content":null}</c></summary>
    public const int Movie = 11;

    /// <summary>游戏手柄 —— 抓包 <c>type=13017</c>，<c>content</c> 整段透传 <c>short_video_point</c></summary>
    public const int Game = 13017;

    /// <summary>标记万物（SPU）—— 抓包 <c>type=13093</c>，<c>content</c> 只有 <c>anchor_id</c></summary>
    public const int Spu = 13093;

    /// <summary>
    /// 小程序 —— 搜索 <c>POST /web/api/media/anchor/search/</c>（<c>anchor_type=4</c>，粘贴分享链接）；
    /// 发布体除 <c>anchor</c> 外还带 <c>anchor_business_type</c> + <c>anchor_content</c>（见 <see cref="DouyinAnchorService"/>）。
    /// </summary>
    public const int Applet = 4;

    /// <summary>位置（既有 POI 实现，未接入本选择器）</summary>
    public const int Poi = -11;

    public static bool IsSupported(int type) => type is Movie or Game or Spu or Applet;

    public static string Label(int type) => type switch
    {
        Movie => "影视演艺",
        Game => "游戏手柄",
        Spu => "标记万物",
        Applet => "小程序",
        Poi => "位置",
        _ => "标签",
    };
}

/// <summary>「添加标签」候选（三类共用一种展示形态，<see cref="Type"/> 区分类别）。</summary>
/// <param name="Type">anchor 类型（<see cref="DouyinAnchorTypes"/>）</param>
/// <param name="Id">anchor 对象 ID（影视 <c>search_list[].id</c> / 游戏 <c>unified_game_id</c> / SPU <c>spu_id</c>）</param>
/// <param name="Title">展示名</param>
/// <param name="Subtitle">副标题（影视类型名 / 游戏标签 / SPU 分类）</param>
/// <param name="Cover">封面 URL（仅展示）</param>
/// <param name="Content">
/// 发布体 <c>content</c>：游戏 = <c>game_info.short_video_point</c> 整段透传；
/// 小程序 = 搜索结果的 <c>extra</c> 字符串（发布页 bundle 的 <c>content: anchorExtra</c>）；
/// 影视固定 null、SPU 由 <see cref="DouyinAnchorService.BuildAnchorJson"/> 现拼。
/// </param>
public sealed record DouyinAnchorCandidate(
    int Type,
    string Id,
    string Title,
    string? Subtitle = null,
    string? Cover = null,
    string? Content = null);

/// <summary>
/// 账号的标签能力位（<c>GET /web/api/media/user/anchor/list</c>）。
/// 页面就是按它渲染下拉可选项；<see cref="Unavailable"/> 里是平台给好的禁用文案
/// （<c>{anchor_name}</c> 占位符保留原样，展示端按需替换）。
/// </summary>
public sealed record DouyinAnchorCapabilities(
    IReadOnlyList<int> Available,
    IReadOnlyDictionary<int, string> Unavailable)
{
    /// <summary>能力接口拉不到时的兜底：四类全放行（fail-open，不因门控接口挂了就禁用功能）。</summary>
    public static DouyinAnchorCapabilities AllowAll { get; } = new(
        [DouyinAnchorTypes.Movie, DouyinAnchorTypes.Game, DouyinAnchorTypes.Spu, DouyinAnchorTypes.Applet],
        new Dictionary<int, string>());

    public bool IsAvailable(int type) => Available.Contains(type);

    public string? ReasonFor(int type) =>
        Unavailable.TryGetValue(type, out var reason) ? reason : null;
}

/// <summary>选中标签前的统一确认（<c>GET /web/api/media/user/anchor/check</c>）。</summary>
public sealed record DouyinAnchorCheckResult(bool Ok, string? Reason);

/// <summary>
/// 抖音「添加标签」服务（B1 抓包，2026-11-11）。
///
/// 端点在 <c>creator.douyin.com</c> 域（wire 见 <c>docs/douyin/wire-2026-11-11.md</c>）：
/// <code>
///   GET  /web/api/media/user/anchor/list           → availableAnchor / unavailableAnchor（能力位）
///   GET  /web/api/media/medium/search              → 影视搜索  search_list[]
///   GET  /webcast/gamecp/mount_page/search         → 游戏搜索  data.mount_games[].game_info
///   GET  /web/api/media/aweme/mark_anchor/spu_list → SPU 搜索  data.spu_list[]
///   POST /web/api/media/anchor/search/             → 小程序搜索（form: keyword=链接, anchor_type=4）anchor_list[]
///   GET  /web/api/media/user/anchor/check          → 选中确认 ok / reason（小程序传 applet_id）
/// </code>
///
/// 发布体：<c>item.anchor.anchor</c> = 字符串化 JSON <c>{"id","type","content"}</c>
/// （<see cref="BuildAnchorJson"/>）；小程序额外带 <c>item.anchor.anchor_business_type</c> 和
/// <c>item.anchor.anchor_content</c>（<see cref="BuildAnchorContentJson"/>，页面 bundle 逐字）。
/// **四类互斥**（位置也是同一个 anchor；本选择器做四类，位置仍走旧 POI 路径，
/// 互斥由预检说明、发布体只出现一个）。
/// </summary>
public sealed class DouyinAnchorService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<DouyinAnchorService>? _logger;

    public DouyinAnchorService(
        PlatformOptions options,
        ILogger<DouyinAnchorService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    // ═══ 能力位 ═══

    /// <summary>账号能挂哪些标签（页面下拉的可选/禁用来源）。</summary>
    public async Task<DouyinAnchorCapabilities> GetCapabilitiesAsync(
        string cookie, CancellationToken ct = default)
    {
        using var client = BuildClient(cookie);
        var url = $"{_options.DouyinCreatorHost}/web/api/media/user/anchor/list"
                + $"?{BuildSignedQuery(new Dictionary<string, string> { ["item_type"] = "1" })}";

        var json = await client.GetStringAsync(url, ct);
        var caps = ParseCapabilities(json);
        _logger?.LogDebug("[DouyinAnchor] anchor/list → 可用 {Types}",
            string.Join(",", caps.Available));
        return caps;
    }

    /// <summary>
    /// 解析 <c>anchor/list</c>。坏响应抛 <see cref="DouyinAnchorException"/> ——
    /// 调用方按「能力位拿不到」处理（fail-open），不要把异常当「不可用」。
    /// </summary>
    public static DouyinAnchorCapabilities ParseCapabilities(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        EnsureStatusOk(root, "anchor/list");

        var available = new List<int>();
        if (root.TryGetProperty("availableAnchor", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var t))
                    available.Add(t);
        }

        var unavailable = new Dictionary<int, string>();
        if (root.TryGetProperty("unavailableAnchor", out var un) && un.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in un.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var type = GetInt32(item, "anchor");
                if (type is null) continue;
                unavailable[type.Value] = GetString(item, "reason") ?? "";
            }
        }

        return new DouyinAnchorCapabilities(available, unavailable);
    }

    // ═══ 搜索（三类） ═══

    /// <summary>
    /// 按类型搜索候选。<paramref name="keyword"/> 为空时：SPU 返回平台推荐一页；
    /// 影视 / 游戏由 UI 先拦（空关键词平台不给有意义的列表）。
    /// </summary>
    public async Task<IReadOnlyList<DouyinAnchorCandidate>> SearchAsync(
        string cookie, int anchorType, string keyword, CancellationToken ct = default)
    {
        return anchorType switch
        {
            DouyinAnchorTypes.Movie => await SearchMoviesAsync(cookie, keyword, ct),
            DouyinAnchorTypes.Game => await SearchGamesAsync(cookie, keyword, ct),
            DouyinAnchorTypes.Spu => await SearchSpuAsync(cookie, keyword, ct),
            DouyinAnchorTypes.Applet => await SearchAppletsAsync(cookie, keyword, ct),
            _ => [],
        };
    }

    /// <summary>影视演艺搜索：<c>GET /web/api/media/medium/search?count=12&amp;keyword=…&amp;offset=0</c></summary>
    private async Task<IReadOnlyList<DouyinAnchorCandidate>> SearchMoviesAsync(
        string cookie, string keyword, CancellationToken ct)
    {
        using var client = BuildClient(cookie);
        var url = $"{_options.DouyinCreatorHost}/web/api/media/medium/search"
                + $"?{BuildSignedQuery(new Dictionary<string, string>
                {
                    ["count"] = "12",
                    ["keyword"] = keyword,
                    ["offset"] = "0",
                })}";

        var json = await client.GetStringAsync(url, ct);
        var items = ParseMediaSearch(json);
        _logger?.LogDebug("[DouyinAnchor] medium/search 「{Keyword}」→ {Count} 条", keyword, items.Count);
        return items;
    }

    /// <summary>
    /// 解析影视搜索。wire 节选：
    /// <c>{"status_code":0,"search_list":[{"type_name":"电影","id":"…","name":"流浪地球","type":3,"poster":{"url_list":["…"]}}]}</c>
    /// ⚠️ <c>search_list[].type</c>（3 = 电影）**不是** anchor 的 type（11），别混用。
    /// </summary>
    public static IReadOnlyList<DouyinAnchorCandidate> ParseMediaSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        EnsureStatusOk(root, "medium/search");

        var list = new List<DouyinAnchorCandidate>();
        if (!root.TryGetProperty("search_list", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = GetString(item, "id");
            var name = GetString(item, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;

            list.Add(new DouyinAnchorCandidate(
                DouyinAnchorTypes.Movie,
                id!,
                name!,
                Subtitle: GetString(item, "type_name"),
                Cover: FirstUrl(item, "poster")));
        }
        return list;
    }

    /// <summary>
    /// 游戏手柄搜索：<c>GET /webcast/gamecp/mount_page/search?game_name=…&amp;count=20&amp;scene=1&amp;version_code=24.0.0&amp;aid=2906</c>。
    /// 这个端点抓包里**不带 msToken/a_bogus**（但公共浏览器参数齐全），按原样发。
    /// </summary>
    private async Task<IReadOnlyList<DouyinAnchorCandidate>> SearchGamesAsync(
        string cookie, string keyword, CancellationToken ct)
    {
        using var client = BuildClient(cookie);
        var env = _options.Environment;
        var extra = new Dictionary<string, string>
        {
            ["game_name"] = keyword,
            ["count"] = "20",
            ["scene"] = "1",
            ["version_code"] = "24.0.0",
            ["aid"] = "2906",   // 覆盖指纹里的 1128（抓包就是 2906）
        };
        var qs = PlatformHttpClient.BuildQueryString(extra, env: env);
        var url = $"{_options.DouyinCreatorHost}/webcast/gamecp/mount_page/search?{qs}";

        var json = await client.GetStringAsync(url, ct);
        var items = ParseGameSearch(json);
        _logger?.LogDebug("[DouyinAnchor] mount_page/search 「{Keyword}」→ {Count} 条", keyword, items.Count);
        return items;
    }

    /// <summary>
    /// 解析游戏搜索。wire 节选：<c>data.mount_games[].game_info</c>：
    /// <c>unified_game_id</c> / <c>name</c> / <c>tag_names</c> / <c>icon</c> / <c>short_video_point</c>。
    /// **没有 <c>short_video_point</c> 的游戏直接跳过** —— 发布体 content 只能整段透传它，
    /// 本地重建会丢账号相关字段（promoter_id 等），选中了也挂不上。
    /// </summary>
    public static IReadOnlyList<DouyinAnchorCandidate> ParseGameSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        EnsureStatusOk(root, "mount_page/search");

        var list = new List<DouyinAnchorCandidate>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return list;
        if (!data.TryGetProperty("mount_games", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var mount in arr.EnumerateArray())
        {
            if (mount.ValueKind != JsonValueKind.Object) continue;
            if (!mount.TryGetProperty("game_info", out var info) || info.ValueKind != JsonValueKind.Object)
                continue;

            var id = GetString(info, "unified_game_id");
            var name = GetString(info, "name");
            var point = GetString(info, "short_video_point");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(point))
                continue;

            list.Add(new DouyinAnchorCandidate(
                DouyinAnchorTypes.Game,
                id!,
                name!,
                Subtitle: JoinStrings(info, "tag_names"),
                Cover: GetString(info, "icon"),
                Content: point));
        }
        return list;
    }

    /// <summary>
    /// SPU 搜索：<c>GET /web/api/media/aweme/mark_anchor/spu_list?query_word=…&amp;page_size=10&amp;page=0</c>。
    /// <c>query_word</c> 为空也返回一页推荐（页面首屏就是「输入」前的默认列表）。
    /// </summary>
    private async Task<IReadOnlyList<DouyinAnchorCandidate>> SearchSpuAsync(
        string cookie, string keyword, CancellationToken ct)
    {
        using var client = BuildClient(cookie);
        var url = $"{_options.DouyinCreatorHost}/web/api/media/aweme/mark_anchor/spu_list"
                + $"?{BuildSignedQuery(new Dictionary<string, string>
                {
                    ["query_word"] = keyword,
                    ["page_size"] = "10",
                    ["page"] = "0",
                })}";

        var json = await client.GetStringAsync(url, ct);
        var items = ParseSpuList(json);
        _logger?.LogDebug("[DouyinAnchor] spu_list 「{Keyword}」→ {Count} 条", keyword, items.Count);
        return items;
    }

    /// <summary>
    /// 解析 SPU 列表。wire 节选：<c>data.spu_list[].{spu_id,title,cover,front_category.front_category_name}</c>。
    /// </summary>
    public static IReadOnlyList<DouyinAnchorCandidate> ParseSpuList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        EnsureStatusOk(root, "spu_list");

        var list = new List<DouyinAnchorCandidate>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return list;
        if (!data.TryGetProperty("spu_list", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = GetString(item, "spu_id");
            var title = GetString(item, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;

            string? category = null;
            if (item.TryGetProperty("front_category", out var fc) && fc.ValueKind == JsonValueKind.Object)
                category = GetString(fc, "front_category_name");

            list.Add(new DouyinAnchorCandidate(
                DouyinAnchorTypes.Spu,
                id!,
                title!,
                Subtitle: category,
                Cover: GetString(item, "cover")));
        }
        return list;
    }

    /// <summary>
    /// 小程序搜索（2026-11-11 补抓）：<c>POST /web/api/media/anchor/search/</c>，
    /// **form 体** <c>keyword=&lt;小程序分享链接&gt;&amp;anchor_type=4</c>（不是 JSON），
    /// 查询串带页面同款公共参数 + msToken + a_bogus。
    /// 页面只接受 http/https 且非 creator.douyin.com 的链接（bundle 里的 <c>er(o)</c>）——
    /// 由 <see cref="IsAppletLink"/> 在 UI 侧先拦。
    /// </summary>
    private async Task<IReadOnlyList<DouyinAnchorCandidate>> SearchAppletsAsync(
        string cookie, string keyword, CancellationToken ct)
    {
        using var client = BuildClient(cookie);
        var url = $"{_options.DouyinCreatorHost}/web/api/media/anchor/search/"
                + $"?{BuildSignedQuery()}";

        var form = new Dictionary<string, string>
        {
            ["keyword"] = keyword.Trim(),
            ["anchor_type"] = DouyinAnchorTypes.Applet.ToString(),
        };
        var json = await client.PostFormAsync(url, form, ct);
        var items = ParseAppletSearch(json);
        _logger?.LogDebug("[DouyinAnchor] anchor/search 「{Keyword}」→ {Count} 条", keyword, items.Count);
        return items;
    }

    /// <summary>
    /// 页面同款链接校验（bundle 逐字）：必须能解析成 http/https 绝对地址，且 host 不是 creator.douyin.com。
    /// </summary>
    public static bool IsAppletLink(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        return !string.Equals(uri.Host, "creator.douyin.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析小程序搜索。wire（2026-11-11 抓包，节选）：
    /// <code>
    /// {"anchor_list":[{"enable_mount":true,
    ///   "extra":"{\"Content\":\"null\",\"Query\":\"\",\"Title\":\"猛鬼宿舍\",\"Token\":\"…\"}",
    ///   "id":"tt3a9d4c20a5a4387002","name":"躺平发育",
    ///   "poster":{"url_list":["https://…"]},"summary":"猛鬼宿舍","type":4}],
    ///  "extra":{"logid":"…","now":…}}
    /// </code>
    /// 没有 <c>status_code</c>（页面按 <c>anchor_list || search_list</c> 取列表）；
    /// 展示名兜底 = <c>JSON.parse(extra).Title</c>（bundle 里 <c>4===type &amp;&amp; extra</c> 的分支）。
    /// </summary>
    public static IReadOnlyList<DouyinAnchorCandidate> ParseAppletSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        EnsureStatusOk(root, "anchor/search");

        JsonElement arr;
        if (!root.TryGetProperty("anchor_list", out arr) || arr.ValueKind != JsonValueKind.Array)
            if (!root.TryGetProperty("search_list", out arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

        var list = new List<DouyinAnchorCandidate>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            var extra = GetString(item, "extra");
            var title = GetString(item, "name");
            if (string.IsNullOrWhiteSpace(title)) title = ReadExtraTitle(extra);
            if (string.IsNullOrWhiteSpace(title)) continue;

            list.Add(new DouyinAnchorCandidate(
                GetInt32(item, "type") ?? DouyinAnchorTypes.Applet,
                id!,
                title!,
                Subtitle: GetString(item, "summary"),
                Cover: FirstUrl(item, "poster"),
                Content: extra));
        }
        return list;
    }

    /// <summary>从 <c>extra</c>（JSON 字符串）里取 <c>Title</c>；读不动返回 null（不抛）。</summary>
    private static string? ReadExtraTitle(string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra)) return null;
        try
        {
            using var doc = JsonDocument.Parse(extra);
            return GetString(doc.RootElement, "Title");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ═══ 选中确认 ═══

    /// <summary>
    /// 统一确认：<c>GET /web/api/media/user/anchor/check?item_type=1&amp;select_anchor=…&amp;applet_id=&amp;anchor_id=…</c>。
    /// 页面选中后必发；<c>ok=false</c> 就不该进发布体（本实现选中时拦，发布体不重复校验）。
    /// 小程序走 <c>applet_id=&lt;搜索结果的 id&gt;</c>（页面同款，不再带 anchor_id）。
    /// </summary>
    public async Task<DouyinAnchorCheckResult> CheckAsync(
        string cookie, int anchorType, string anchorId, CancellationToken ct = default)
    {
        using var client = BuildClient(cookie);
        var extra = new Dictionary<string, string>
        {
            ["item_type"] = "1",
            ["select_anchor"] = anchorType.ToString(),
        };
        if (anchorType == DouyinAnchorTypes.Applet)
        {
            extra["applet_id"] = anchorId;
        }
        else
        {
            extra["applet_id"] = "";
            extra["anchor_id"] = anchorId;
        }

        var url = $"{_options.DouyinCreatorHost}/web/api/media/user/anchor/check"
                + $"?{BuildSignedQuery(extra)}";

        var json = await client.GetStringAsync(url, ct);
        return ParseCheck(json);
    }

    /// <summary>解析 anchor/check：<c>{"ok":true,"reason":"","status_code":0}</c>。</summary>
    public static DouyinAnchorCheckResult ParseCheck(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        EnsureStatusOk(root, "anchor/check");

        var ok = root.TryGetProperty("ok", out var okEl)
            && (okEl.ValueKind == JsonValueKind.True
                || (okEl.ValueKind == JsonValueKind.Number && okEl.GetInt32() == 1));
        var reason = GetString(root, "reason");
        return new DouyinAnchorCheckResult(ok, string.IsNullOrWhiteSpace(reason) ? null : reason);
    }

    // ═══ 发布体 ═══

    /// <summary>
    /// 发布体 <c>item.anchor.anchor</c> 的字符串化 JSON（**逐字对齐抓包 / bundle**）：
    /// <list type="bullet">
    /// <item>影视：<c>{"id":"…","type":11,"content":null}</c></item>
    /// <item>游戏：content 整段透传 <c>short_video_point</c>（账号相关字段不能本地重建）</item>
    /// <item>标记万物：<c>{"id":"…","type":13093,"content":"{\"anchor_id\":\"…\"}"}</c></item>
    /// <item>小程序：content = 搜索结果的 <c>extra</c> 字符串（bundle <c>content: anchorExtra</c>）</item>
    /// </list>
    /// 参数不完整（空 ID / 不支持的 type / 游戏或小程序缺 content）→ 返回 null（发布体不出现 anchor）。
    /// </summary>
    public static string? BuildAnchorJson(int anchorType, string anchorId, string? content)
    {
        if (string.IsNullOrWhiteSpace(anchorId) || !DouyinAnchorTypes.IsSupported(anchorType))
            return null;

        string? payloadContent = anchorType switch
        {
            DouyinAnchorTypes.Movie => null,
            DouyinAnchorTypes.Game => string.IsNullOrWhiteSpace(content) ? null : content,
            DouyinAnchorTypes.Spu => JsonSerializer.Serialize(
                new DouyinSpuAnchorContent { AnchorId = anchorId.Trim() }, AnchorJsonOptions),
            // 小程序的 content = 搜索结果的 extra（含 Token 等挂载信息），不能本地重建
            DouyinAnchorTypes.Applet => string.IsNullOrWhiteSpace(content) ? null : content,
            _ => null,
        };

        // 游戏缺 short_video_point / 小程序缺 extra 就组不出来（不是「content 留空」——页面从不发空串）
        if (anchorType is DouyinAnchorTypes.Game or DouyinAnchorTypes.Applet
            && payloadContent is null)
            return null;

        return JsonSerializer.Serialize(new DouyinAnchorPayload
        {
            Id = anchorId.Trim(),
            Type = anchorType,
            Content = payloadContent,
        }, AnchorJsonOptions);
    }

    /// <summary>
    /// 小程序发布体的第三个键 <c>item.anchor.anchor_content</c>（页面 bundle 逐字）：
    /// <c>anchor_content = JSON.stringify(anchorExtra)</c> —— 对已经是 JSON 字符串的 extra
    /// **再包一层引号**。非小程序返回 null（该键不下发）。
    /// 不带它会缺少小程序挂载所需的 <c>anchor_business_type=4</c> 配套结构。
    /// </summary>
    public static string? BuildAnchorContentJson(int anchorType, string? content)
    {
        if (anchorType != DouyinAnchorTypes.Applet || string.IsNullOrWhiteSpace(content))
            return null;
        return JsonSerializer.Serialize(content, AnchorJsonOptions);
    }

    /// <summary>发布体 <c>{"id","type","content"}</c> —— 属性顺序即序列化顺序，勿调整。</summary>
    private sealed class DouyinAnchorPayload
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("type")] public int Type { get; init; }
        [JsonPropertyName("content")] public string? Content { get; init; }
    }

    private sealed class DouyinSpuAnchorContent
    {
        [JsonPropertyName("anchor_id")] public string AnchorId { get; init; } = "";
    }

    /// <summary>中文不转义（浏览器 <c>JSON.stringify</c> 同款）；wire 语义相同，回读更可读。</summary>
    private static readonly JsonSerializerOptions AnchorJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ═══ 公共 ═══

    /// <summary>creator 域名的查询串：msToken 本地生成、a_bogus 本地计算（aid=1128，同热点/音乐）。</summary>
    private string BuildSignedQuery(Dictionary<string, string>? extra = null)
    {
        extra ??= [];
        var env = _options.Environment;
        var userAgent = env?.UserAgent ?? _options.UserAgent;
        var baseParams = PlatformHttpClient.BuildQueryString(extra, env: env);
        var msToken = MsTokenGenerator.Generate();
        var aBogus = ABogusGenerator.Generate(baseParams, userAgent, aid: 1128);
        return PlatformHttpClient.BuildQueryString(extra, msToken, aBogus, env);
    }

    private PlatformHttpClient BuildClient(string cookie)
    {
        var logging = new Core.Handlers.LoggingHandler(null);
        HttpMessageHandler handler = logging;
        if (!string.IsNullOrWhiteSpace(cookie))
            handler = new Core.Handlers.CookieAuthHandler(cookie) { InnerHandler = logging };
        return new PlatformHttpClient(handler, _options, null);
    }

    /// <summary>平台业务码非 0 一律抛（调用方统一按「这次拉取失败」处理）。</summary>
    private static void EnsureStatusOk(JsonElement root, string api)
    {
        if (!root.TryGetProperty("status_code", out var sc)) return;
        if (sc.ValueKind != JsonValueKind.Number || sc.TryGetInt32(out var code) && code == 0) return;
        throw new DouyinAnchorException($"{api} 返回 status_code={(sc.TryGetInt32(out var c) ? c : -1)}");
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int? GetInt32(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out var v)
            ? v
            : null;

    /// <summary>取 <c>{"url_list":["…"]}</c> 的第一个 URL。</summary>
    private static string? FirstUrl(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
            return null;
        if (!el.TryGetProperty("url_list", out var urls) || urls.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var u in urls.EnumerateArray())
            if (u.ValueKind == JsonValueKind.String) return u.GetString();
        return null;
    }

    /// <summary>字符串数组 → 「A · B」（空数组返回 null）。</summary>
    private static string? JoinStrings(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;
        var parts = el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}

/// <summary>抖音标签接口失败（业务码非 0 / 响应形状不对）—— 由 UI 层转成状态行文案。</summary>
public sealed class DouyinAnchorException(string message) : Exception(message);
