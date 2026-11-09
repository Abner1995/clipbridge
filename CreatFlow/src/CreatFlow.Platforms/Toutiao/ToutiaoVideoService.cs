namespace CreatFlow.Platforms.Toutiao;

/// <summary>
/// 头条视频作品删除（2026-11-07 接入）。
///
/// 抓包（<c>docs/toutiao/20261105/requests/</c>）里横版 / 竖版是**两条不同端点**，
/// 归档记录里没有视频类型，所以按「先横版、失败再竖版」的顺序尝试：
///
/// <code>
/// 横版：POST https://mp.toutiao.com/xigua/api/video/articledelete/
///       {"ItemId":"…","SourceType":2,"DelSlaveId":"","Reason":""}
/// → {"status":0,"message":"SUCCESS","data":{"Code":0,"Msg":"SUCCESS","BaseResp":{…}}}
///
/// 竖版：GET  https://mp.toutiao.com/ugc/publish/video/v1/delete/?item_id=…
/// → {"err_code":0,"err_no":0,"message":"Success"}
/// </code>
///
/// 两条都**不需要验证码**（与 B站不同）；写接口的头由 <see cref="ToutiaoApiClient"/> 统一处理
/// （secsdk csrf + x-csrf-token / xigua_csrf_token）。
/// </summary>
public sealed class ToutiaoVideoService
{
    /// <summary>横版视频删除路径</summary>
    public const string ArticleDeletePath = "/xigua/api/video/articledelete/";
    /// <summary>竖版小视频删除路径（GET + item_id）</summary>
    public const string SmallDeletePath = "/ugc/publish/video/v1/delete/";
    /// <summary>
    /// 视频详情（编辑页初始化）路径 —— 抓包 <c>docs/toutiao/20261105/requests/video_detail.json</c>。
    /// 只读：回读 <c>HashtagList</c> / <c>VideoInfo.Vid</c> 等字段（诊断探针用）。
    /// </summary>
    public const string EditItemPath = "/xigua/api/upload/EditItem/";
    /// <summary>横版删除的 Referer（抓包原文）</summary>
    public const string ArticleDeleteReferer = "https://mp.toutiao.com/profile_v4/xigua/content-manage-v2";
    /// <summary>竖版删除的 Referer（抓包原文）</summary>
    public const string SmallDeleteReferer = "https://mp.toutiao.com/profile_v4/xigua/small-video";

    private readonly PlatformOptions _options;

    public ToutiaoVideoService(PlatformOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 删除一条头条视频作品。先按横版删，失败再按竖版删。
    /// 返回 true = 至少一个端点确认成功；false = 两个都失败（调用方保留本地记录）。
    /// </summary>
    public async Task<bool> DeleteAsync(
        string cookie, string itemId, string? csrfToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookie) || string.IsNullOrWhiteSpace(itemId))
            return false;

        // ── 横版 ──
        try
        {
            using var client = new ToutiaoApiClient(
                cookie, _options, csrfToken, ArticleDeleteReferer);
            var text = await client.PostJsonRawAsync(
                _options.ToutiaoHost + ArticleDeletePath, BuildArticleDeleteBody(itemId), ct);
            if (ParseArticleDelete(text)) return true;
            Serilog.Log.Warning(
                "[Toutiao] 横版删除未成功（可能这条是竖版），改试竖版端点：{Body}", Snippet(text));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Toutiao] 横版删除请求失败，改试竖版端点 item={ItemId}", itemId);
        }

        // ── 竖版 ──
        try
        {
            using var client = new ToutiaoApiClient(
                cookie, _options, csrfToken, SmallDeleteReferer);
            var url = _options.ToutiaoHost + SmallDeletePath
                + "?item_id=" + Uri.EscapeDataString(itemId);
            var text = await client.GetStringAsync(url, ct);
            if (ParseSmallDelete(text)) return true;
            Serilog.Log.Warning("[Toutiao] 竖版删除未成功：{Body}", Snippet(text));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Toutiao] 竖版删除请求失败 item={ItemId}", itemId);
        }

        return false;
    }

    // ═══ 详情回查（只读，诊断探针） ═══

    /// <summary>EditItem 的 Referer（抓包原文）。</summary>
    public static string EditItemReferer(string itemId) =>
        "https://mp.toutiao.com/profile_v4/xigua/upload-video?from=edit&index=0&pgc_id=" + itemId;

    /// <summary>
    /// EditItem 请求 URL（抓包原文：<c>?params={"ItemId":"…","CreateSource":2}</c>，query 整体 URL 编码）。
    /// 纯函数，自检可直接钉 wire 形体。
    /// </summary>
    public static string BuildEditItemUrl(string host, string itemId)
    {
        var paramsJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["ItemId"] = itemId,
            ["CreateSource"] = 2,
        });
        return host + EditItemPath + "?params=" + Uri.EscapeDataString(paramsJson);
    }

    /// <summary>
    /// 只读回查：视频详情（编辑页初始化）。**诊断用，不进业务路径** —— 返回原始 JSON 文本，
    /// 由探针自行解析（头条字段多且会变，不为此建 DTO）。
    /// </summary>
    public async Task<string> GetEditItemRawAsync(
        string cookie, string itemId, string? csrfToken, CancellationToken ct = default)
    {
        using var client = new ToutiaoApiClient(
            cookie, _options, csrfToken, EditItemReferer(itemId));
        return await client.GetStringAsync(BuildEditItemUrl(_options.ToutiaoHost, itemId), ct);
    }

    // ═══ 作品列表（内容管理）—— HashtagList 只在列表里回显 ═══

    /// <summary>
    /// 作品列表路径（抓包 <c>docs/toutiao/20261105/接口说明书.md</c> §3）。
    /// **HashtagList 只在这个列表的 <c>itemCell.extra.origin_content</c> 里**，
    /// EditItem 详情反而不带（2026-11-09 实测）。
    /// </summary>
    public const string MpFeedPath = "/api/feed/mp_provider/v1/";

    /// <summary>
    /// 作品列表 URL（参数取自抓包原文；<c>genre_type_switch</c> / <c>client_extra_params</c>
    /// 是 URL 编码的 JSON 字符串）。纯函数，自检可钉 wire 形体。
    /// </summary>
    public static string BuildMpFeedUrl(
        string host, string userId, string category, string keyword,
        int offset, int count, int status = 0)
    {
        const string genreSwitch =
            "{\"repost\":1,\"small_video\":1,\"toutiao_graphic\":1,\"weitoutiao\":1,\"xigua_video\":1}";
        var extra = "{\"category\":\"" + category + "\",\"real_app_id\":\"1231\","
                    + "\"need_forward\":\"true\",\"offset_mode\":\"1\",\"page_index\":\"1\","
                    + "\"status\":" + status + ",\"source\":\"all\"}";

        var query = string.Join("&",
        [
            "provider_type=mp_provider",
            "aid=13",
            "app_name=news_article",
            "category=" + Uri.EscapeDataString(category),
            "channel=",
            "stream_api_version=88",
            "genre_type_switch=" + Uri.EscapeDataString(genreSwitch),
            "device_platform=pc",
            "platform_id=0",
            "visited_uid=" + Uri.EscapeDataString(userId),
            "offset=" + offset,
            "count=" + count,
            "keyword=" + Uri.EscapeDataString(keyword),
            "client_extra_params=" + Uri.EscapeDataString(extra),
            "app_id=1231",
        ]);
        return host + MpFeedPath + "?" + query;
    }

    /// <summary>当前登录用户的 <c>user_id</c>（作品列表的 <c>visited_uid</c> 要用）。</summary>
    public async Task<string?> GetUserIdAsync(
        string cookie, string? csrfToken, CancellationToken ct = default)
    {
        using var client = new ToutiaoApiClient(
            cookie, _options, csrfToken, "https://mp.toutiao.com/profile_v4/index");
        return await client.GetUserIdAsync(ct);
    }

    /// <summary>作品列表原始 JSON（诊断用；调用方自己找行、解析 <c>origin_content</c>）。</summary>
    public async Task<string> GetMpFeedRawAsync(
        string cookie, string? csrfToken, string userId, string category,
        string keyword = "", int offset = 0, int count = 20, int status = 0,
        CancellationToken ct = default)
    {
        using var client = new ToutiaoApiClient(
            cookie, _options, csrfToken, "https://mp.toutiao.com/profile_v4/index");
        return await client.GetStringAsync(
            BuildMpFeedUrl(_options.ToutiaoHost, userId, category, keyword, offset, count, status), ct);
    }

    // ═══ 纯函数（自检直接喂抓包响应） ═══

    /// <summary>
    /// 横版删除请求体（抓包原文：<c>{"ItemId":"…","SourceType":2,"DelSlaveId":"","Reason":""}</c>）。
    /// 用字典而不是 DTO —— 混淆打包时不需要额外挂 <c>[JsonPropertyName]</c>。
    /// </summary>
    public static Dictionary<string, object?> BuildArticleDeleteBody(string itemId) => new()
    {
        ["ItemId"] = itemId,
        ["SourceType"] = 2,
        ["DelSlaveId"] = "",
        ["Reason"] = "",
    };

    /// <summary>
    /// 横版删除响应：<c>status=0</c> 且 <c>data.Code=0</c> 且 <c>BaseResp.StatusCode=0</c>。
    /// 任何一层非 0 / 结构认不出 → false（外层会再试竖版）。
    /// </summary>
    public static bool ParseArticleDelete(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText)) return false;
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (IntOf(root, "status") is { } status && status != 0) return false;

            // **必须看到显式的 data.Code=0** —— 竖版视频误投横版端点时，平台可能回一个
            // status=0 但没有 data.Code 的壳；把「没报错」当成功会误删本地记录、视频仍在平台上
            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || IntOf(data, "Code") is not 0)
            {
                return false;
            }

            if (data.TryGetProperty("BaseResp", out var baseResp)
                && baseResp.ValueKind == JsonValueKind.Object
                && IntOf(baseResp, "StatusCode") is { } statusCode && statusCode != 0)
                return false;

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>竖版删除响应：<c>err_code=0</c> 且 <c>err_no=0</c>。</summary>
    public static bool ParseSmallDelete(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText)) return false;
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var errCode = IntOf(root, "err_code") ?? -1;
            var errNo = IntOf(root, "err_no") ?? -1;
            return errCode == 0 && errNo == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? IntOf(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var value)) return value;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static string Snippet(string text) =>
        text.Length > 300 ? text[..300] + "…" : text;
}
