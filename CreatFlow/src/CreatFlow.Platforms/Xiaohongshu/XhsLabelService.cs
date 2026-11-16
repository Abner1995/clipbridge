using System.Text.Json;
using Serilog;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书视频标记的标签搜索 —— <c>POST /web_api/sns/v1/search/label</c>（edith 域）。
///
/// 请求体来自 2026-11-10 真账号抓包（发布页「添加组件 → 标记地点或标记朋友」弹窗）：
/// <code>
/// {"keyword":"","page":{"page_size":20,"page":1},"type":"user",
///  "geo_info":"{\"userGeo\":{\"latitude\":0,\"longitude\":0}}",
///  "search_id":"","source_enum":2}
/// </code>
/// **响应结构尚未抓到**（抓包只记录了请求；线上 bundle 0905aa3a 未入库）——
/// 本服务先只提供 raw 通道给探针排障，解析器等拿到真实样本再补，不猜字段。
/// </summary>
public sealed class XhsLabelService
{
    private const string SearchPath = "/web_api/sns/v1/search/label";

    /// <summary>标记朋友（抓包样本 type 值）。</summary>
    public const string UserType = "user";

    /// <summary>标记地点（页面同组入口；type 值未抓到，探针可传参试）。</summary>
    public const string PoiType = "poi";

    private readonly PlatformOptions _options;
    private readonly ILogger<XhsLabelService>? _logger;

    public XhsLabelService(PlatformOptions options, ILogger<XhsLabelService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 搜索可标记对象（人 / 地点）。返回原始响应 —— 形状未抓到，先给探针看原文。
    /// 失败向上抛（由页面写状态行）。
    /// </summary>
    public async Task<JsonElement> SearchRawAsync(
        string cookie, string keyword, string type = UserType, CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        var body = new Dictionary<string, object?>
        {
            ["keyword"] = keyword ?? "",
            ["page"] = new Dictionary<string, object?>
            {
                ["page_size"] = 20,
                ["page"] = 1,
            },
            ["type"] = type,
            ["geo_info"] = "{\"userGeo\":{\"latitude\":0,\"longitude\":0}}",
            ["search_id"] = "",
            ["source_enum"] = 2,
        };

        Log.Debug("[XhsLabel] search/label type={Type} keyword=「{Keyword}」", type, keyword);
        return await client.PostEdithRawAsync(SearchPath, body, ct);
    }

    /// <summary>
    /// 搜索可标记的**朋友**（<c>type=user</c>，2026-11-15 真账号探针实测）：
    /// <code>
    /// {"data":{"result":{"success":true},"users":[
    ///   {"type":"user","user_base_dto":{"user_id":"…","user_nickname":"…",
    ///     "red_id":"…","desc":"…","image":"…"}}]}}
    /// </code>
    /// 缺 user_id / 昵称的条目跳过。
    /// </summary>
    public async Task<List<XhsLabelItem>> SearchFriendsAsync(
        string cookie, string keyword, CancellationToken ct = default)
    {
        var root = await SearchRawAsync(cookie, keyword, UserType, ct);
        return ParseFriends(root);
    }

    /// <summary>解析 <c>search/label?type=user</c> 响应（纯函数，供自检钉字段映射）。</summary>
    public static List<XhsLabelItem> ParseFriends(JsonElement root)
    {
        var items = new List<XhsLabelItem>();
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("users", out var users)
            || users.ValueKind != JsonValueKind.Array) return items;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in users.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var dto = el.TryGetProperty("user_base_dto", out var nested)
                      && nested.ValueKind == JsonValueKind.Object ? nested : el;

            var id = Str(dto, "user_id", "userId", "userid", "id");
            var name = Str(dto, "user_nickname", "nickname", "userName", "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
            if (!seen.Add(id)) continue;

            items.Add(new XhsLabelItem(
                Id: id,
                Name: name,
                Type: (Str(el, "type") ?? UserType).ToUpperInvariant(),
                Desc: Str(dto, "desc", "description"),
                Avatar: Str(dto, "image", "image_size_large", "avatar")));
        }

        return items;
    }

    private static string? Str(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString();
        }
        return null;
    }
}

/// <summary>
/// 可标记对象（人 / 地点）—— 视频标记弹窗搜索的一条结果。
/// <see cref="Id"/> / <see cref="Name"/> 进发布体 <c>video_info.markers[].detail.event.value</c>。
/// </summary>
public sealed record XhsLabelItem(
    string Id,
    string Name,
    string Type,
    string? Desc = null,
    string? Avatar = null);
