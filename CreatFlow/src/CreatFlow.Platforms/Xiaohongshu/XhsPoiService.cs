using Serilog;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书 POI/位置搜索服务。
///
/// 对标 Python examples/xiaohongshu/helper/search.py。
/// POST /web_api/sns/v1/local/poi/creator/search (on edith host)
/// </summary>
public sealed class XhsPoiService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<XhsPoiService>? _logger;

    public XhsPoiService(PlatformOptions options, ILogger<XhsPoiService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 搜索位置/POI。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="keyword">搜索关键词</param>
    /// <param name="page">页码（1-based）</param>
    /// <param name="size">每页条数</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>POI 条目列表</returns>
    public async Task<List<XhsPoiItem>> SearchAsync(
        string cookie,
        string keyword,
        int page = 1,
        int size = 50,
        CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        try
        {
            var body = new Dictionary<string, object?>
            {
                ["latitude"] = 0,
                ["longitude"] = 0,
                ["keyword"] = keyword,
                ["page"] = page,
                ["size"] = size,
                ["source"] = "WEB",
                ["type"] = 3,
            };

            var resp = await client.PostEdithRawAsync(
                "/web_api/sns/v1/local/poi/creator/search", body, ct);

            var respText = resp.GetRawText();
            Serilog.Log.Debug("[XhsPoi] raw response len={Len} json={Json}",
                respText.Length, respText.Length > 2000 ? respText[..2000] : respText);

            var pois = new List<XhsPoiItem>();

            // Response: { "data": { "poi_list": [...] } }
            if (resp.TryGetProperty("data", out var data) &&
                data.TryGetProperty("poi_list", out var poisArr) &&
                poisArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in poisArr.EnumerateArray())
                {
                    var poiId = TryGetString(p, "poi_id") ?? "";
                    var name = TryGetString(p, "name") ?? "";
                    // API 返回 address / full_address，没有 subname
                    var subname = TryGetString(p, "address") ?? TryGetString(p, "full_address");
                    var poiType = TryGetInt(p, "poi_type") ?? 12;

                    if (!string.IsNullOrWhiteSpace(poiId))
                        pois.Add(new XhsPoiItem(poiId, poiType, name, subname));
                }
            }

            _logger?.LogInformation("XHS POI search: kw={Kw}, found={Count}",
                keyword, pois.Count);
            return pois;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "XHS POI search failed: kw={Kw}", keyword);
            return [];
        }
    }

    private static string? TryGetString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    private static int? TryGetInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetInt32();
        return null;
    }
}
