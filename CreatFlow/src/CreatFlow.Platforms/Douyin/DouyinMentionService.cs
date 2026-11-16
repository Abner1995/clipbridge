namespace CreatFlow.Platforms.Douyin;

/// <summary>
/// 抖音 @好友 候选（2026-11-11 补抓：<c>GET /web/api/v2/discover/search/</c>，
/// 原始响应见 <c>docs/douyin/20261105/discover.json</c>）。
/// </summary>
/// <param name="Uid">用户 ID —— 发布体 <c>common.mentions</c> 数组里放的就是它</param>
/// <param name="Nickname">昵称 —— 正文补「@昵称」、text_extra 定位用</param>
/// <param name="UniqueId">抖音号（仅展示）</param>
/// <param name="ShortId">短 ID（仅展示，多数账号与抖音号不同）</param>
/// <param name="AvatarUrl">头像 URL（仅展示）</param>
/// <param name="Signature">简介（仅展示，用于同名账号辨认）</param>
public sealed record DouyinMentionUser(
    string Uid,
    string Nickname,
    string? UniqueId = null,
    string? ShortId = null,
    string? AvatarUrl = null,
    string? Signature = null);

/// <summary>
/// 抖音 @好友 搜索服务（2026-11-11 补抓，wire 见 <c>docs/douyin/wire-2026-11-11.md</c> §2.6）。
///
/// <code>
///   GET /web/api/v2/discover/search/
///       ?keyword=…&amp;cursor=0&amp;scene=1&amp;search_source=publish_web&amp;count=10
///       + 浏览器公共参数 + aid=1128 + msToken + a_bogus
/// </code>
///
/// 响应 <c>user_list[]</c> 的元素字段：<c>uid / nickname / unique_id / short_id /
/// avatar_thumb.url_list[] / signature</c>；<c>has_more/cursor</c> 用于翻页（v1 只取第一页）。
/// 签名口径与 <see cref="DouyinAnchorService"/> 相同：msToken / a_bogus 本地生成，Cookie 走
/// <c>CookieAuthHandler</c>；**不依赖 WebView2**。
///
/// 发布体落点（B1 抓包）：
/// <list type="bullet">
/// <item><c>item.common.mentions</c> = uid 的 **JSON 字符串数组**，如 <c>["3171…"]</c></item>
/// <item>正文（<c>common.text</c>/<c>common.caption</c>）里保留 <c>@昵称</c> 文本</item>
/// <item><c>common.text_extra</c> 追加 <c>{"type":0,"user_id":…,"at_source":"user_manual"}</c>
///   （正文与简介两套坐标）</item>
/// </list>
/// </summary>
public sealed class DouyinMentionService
{
    /// <summary>@好友候选搜索路径（creator 域）</summary>
    public const string DiscoverSearchPath = "/web/api/v2/discover/search/";

    private readonly PlatformOptions _options;
    private readonly ILogger<DouyinMentionService>? _logger;

    public DouyinMentionService(
        PlatformOptions options,
        ILogger<DouyinMentionService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 按关键词搜索可 @ 的用户。<paramref name="keyword"/> 为空时原样发（平台可能回推荐列表，
    /// 未抓到空词样本，不本地编造行为）。
    /// </summary>
    public async Task<IReadOnlyList<DouyinMentionUser>> SearchAsync(
        string cookie, string keyword, int cursor = 0, int count = 10,
        CancellationToken ct = default)
    {
        using var client = BuildClient(cookie);
        var url = $"{_options.DouyinCreatorHost}{DiscoverSearchPath}"
                + $"?{BuildSignedQuery(new Dictionary<string, string>
                {
                    ["keyword"] = keyword ?? "",
                    ["cursor"] = cursor.ToString(),
                    ["scene"] = "1",
                    ["search_source"] = "publish_web",
                    ["count"] = count.ToString(),
                })}";

        var json = await client.GetStringAsync(url, ct);
        var users = ParseSearch(json);
        _logger?.LogDebug("[DouyinMention] discover/search 「{Keyword}」→ {Count} 人",
            keyword, users.Count);
        return users;
    }

    /// <summary>
    /// 解析 <c>discover/search</c>。坏响应 / 业务码非 0 抛 <see cref="DouyinMentionException"/>。
    /// <c>uid</c> 在样本里是字符串（也按数字兜底，防平台改类型）；缺 uid/昵称的条目跳过。
    /// </summary>
    public static IReadOnlyList<DouyinMentionUser> ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        EnsureStatusOk(root);

        var list = new List<DouyinMentionUser>();
        if (!root.TryGetProperty("user_list", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var uid = GetUid(item);
            var nickname = GetString(item, "nickname");
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(nickname)) continue;

            list.Add(new DouyinMentionUser(
                uid!,
                nickname!,
                UniqueId: GetString(item, "unique_id"),
                ShortId: GetString(item, "short_id"),
                AvatarUrl: FirstUrl(item, "avatar_thumb"),
                Signature: GetString(item, "signature")));
        }
        return list;
    }

    // ═══ 公共 ═══

    /// <summary>creator 域名的查询串：msToken 本地生成、a_bogus 本地计算（aid=1128，同标签/热点）。</summary>
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

    private static void EnsureStatusOk(JsonElement root)
    {
        if (!root.TryGetProperty("status_code", out var sc)) return;
        if (sc.ValueKind != JsonValueKind.Number || sc.TryGetInt32(out var code) && code == 0) return;
        throw new DouyinMentionException(
            $"discover/search 返回 status_code={(sc.TryGetInt32(out var c) ? c : -1)}");
    }

    /// <summary>uid 兼容字符串 / 数字两种 JSON 类型（大整数按 long 处理）。</summary>
    private static string? GetUid(JsonElement obj)
    {
        if (!obj.TryGetProperty("uid", out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number when el.TryGetInt64(out var v) => v.ToString(),
            _ => null,
        };
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>取 <c>{"uri":…,"url_list":["…"]}</c> 的第一个 URL。</summary>
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
}

/// <summary>抖音 @好友搜索失败（业务码非 0 / 响应形状不对）—— 由 UI 层转成状态行文案。</summary>
public sealed class DouyinMentionException(string message) : Exception(message);
