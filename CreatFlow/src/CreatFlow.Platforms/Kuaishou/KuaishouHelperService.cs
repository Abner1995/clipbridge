using CreatFlow.Platforms.Kuaishou.Requests;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手辅助功能服务 — 标签搜索、POI 位置搜索、附近地点、推荐标签、变现入口。
///
/// 对标 Python examples/kuaishou/helper/ 下的各 API 示例。
/// </summary>
public sealed class KuaishouHelperService
{
    private readonly PlatformOptions _options;
    private readonly ILogger<KuaishouHelperService>? _logger;

    public KuaishouHelperService(PlatformOptions options, ILogger<KuaishouHelperService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    // ═══ 标签搜索 ═══

    /// <summary>搜索话题标签（发布页「添加标签」）。</summary>
    public async Task<List<KuaishouTagResult>> SearchTagsAsync(
        string cookie, string keyword, int count = 15, int page = 0,
        string? apiPh = null, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient(cookie, apiPh);
            // 关键词后追加   (U+00A0) 与浏览器一致（对标 tag.py 的 '大海 '）
            var body = new { count, keyword = keyword + " ", page = page.ToString() };
            var resp = await client.PostAsync<KuaishouTagSearchResponse>(
                "/rest/cp/works/v2/video/pc/tag/search", body, ct);

            // 首条是关键词回显（id=0），过滤掉无 ID 的条目
            return resp?.Tags?
                .Where(t => t.Tag?.Id > 0)
                .Select(t => new KuaishouTagResult(
                    Id: t.Tag!.Id,
                    Name: t.Tag.Name ?? "",
                    ViewCount: t.ViewCount)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            // 话题建议失败不影响其他平台的结果展示，静默降级为空列表
            Serilog.Log.Warning(ex, "Kuaishou tag search failed: keyword={Keyword}", keyword);
            return [];
        }
    }

    /// <summary>获取上传后推荐标签。</summary>
    public async Task<List<KuaishouTagResult>> GetRecTagsAsync(
        string cookie, string editSessionId, string? apiPh = null, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie, apiPh);
        var body = new { editSessionId };
        var resp = await client.PostAsync<KuaishouRecTagResponse>(
            "/rest/cp/works/v2/video/pc/upload/recTag", body, ct);

        return resp?.Tags?.Select(t => new KuaishouTagResult(
            Id: t.Tag?.Id ?? 0,
            Name: t.Tag?.Name ?? "",
            ViewCount: t.ViewCount)).ToList() ?? [];
    }

    // ═══ 位置搜索 ═══

    /// <summary>搜索 POI 位置（发布页「添加位置」搜索）。
    /// 参考: examples/kuaishou/helper/search.py — 需要 kpn/subBiz/kpf 作为 URL query 参数。</summary>
    public async Task<List<KuaishouPoiResult>> SearchPoiAsync(
        string cookie, string cityName, string keyword, int count = 20,
        string? pcursor = null, string? apiPh = null, CancellationToken ct = default)
        => (await SearchPoiPageAsync(cookie, cityName, keyword, count, pcursor, apiPh, ct)).Items;

    /// <summary>
    /// 同 <see cref="SearchPoiAsync"/>，但把**下一页游标**一起带出来 —— 发布页候选列表的
    /// 「加载更多」要用它（快手是 pcursor 游标分页，不是页码；之前这个字段被读掉扔了，
    /// 于是翻页无从谈起）。
    /// </summary>
    public async Task<(List<KuaishouPoiResult> Items, string? NextCursor)> SearchPoiPageAsync(
        string cookie, string cityName, string keyword, int count = 20,
        string? pcursor = null, string? apiPh = null, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie, apiPh);
        var body = new { cityName, count, keyword, pcursor = pcursor ?? "" };
        // kpn/subBiz/kpf are required base query params (sig4 computes against them).
        // api_ph must also be in the URL query for sig4, in addition to the JSON body.
        // Use Uri.EscapeDataString to match Python's URL-encoded format exactly.
        var qs = $"kpn=kuaishou_cp&subBiz={Uri.EscapeDataString("CP/CREATOR_PLATFORM")}&kpf=PC_WEB&kuaishou.web.cp.api_ph={apiPh ?? ""}";
        var path = $"/rest/zt/location/wi/poi/search?{qs}";
        var resp = await client.PostRawAsync<KuaishouPoiSearchResponse>(path, body, ct);

        // result 码以前没人读：签名不符(500002)、缺城市(10001) 这类失败全都以「空列表」收场，
        // 上层只能显示「未找到匹配」——真相埋在日志里也看不出来。空结果时至少把码写进日志。
        var items = resp?.Locations?.Select(ToPoiResult).ToList() ?? [];
        if (items.Count == 0 && resp is not null && resp.Result != 1)
        {
            Serilog.Log.Warning("[Kuaishou] POI 搜索空结果 result={Result}（cityName=「{City}」keyword=「{Kw}」）",
                resp.Result, cityName, keyword);
        }
        return (items, string.IsNullOrWhiteSpace(resp?.Pcursor) ? null : resp!.Pcursor);
    }

    /// <summary>获取附近 POI（基于经纬度）。返回 (cityName, locations)。</summary>
    public async Task<(string? CityName, List<KuaishouPoiResult> Locations)> GetNearbyPoisAsync(
        string cookie, string latitude, string longitude, int count = 20,
        string? apiPh = null, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie, apiPh);
        var body = new { location = $"{latitude},{longitude}", count };
        var resp = await client.PostAsync<KuaishouNearbyResponse>(
            "/rest/cp/works/v2/common/pc/nearby", body, ct);

        var list = resp?.Locations?.Select(ToPoiResult).ToList() ?? [];
        return (resp?.CityName, list);
    }

    // ═══ 作者搜索 ═══

    /// <summary>搜索快手作者（素材来源于网络 → 快手平台时使用）。</summary>
    public async Task<List<KuaishouAuthorResult>> SearchAuthorAsync(
        string cookie, string keyword, int count = 20, int pageNo = 0,
        string? apiPh = null, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie, apiPh);
        var body = new { sessionId = "", keyword, count, pageNo };
        // Author search returns standard envelope {result:1, data:{authors:[...]}}
        var resp = await client.PostAsync<KuaishouAuthorSearchResponse>(
            "/rest/cp/works/v2/common/pc/author/search", body, ct);

        return resp?.List?.Select(a => new KuaishouAuthorResult(
            UserId: a.UserId,
            UserName: a.UserName ?? "",
            AvatarUrl: a.UserHead?.FirstOrDefault()?.Url,
            PhotoNum: a.PhotoNum,
            LikeNum: a.LikeNum)).ToList() ?? [];
    }

    // ═══ 变现入口 ═══

    /// <summary>获取变现入口（关联小程序等）。</summary>
    public async Task<List<KuaishouEntranceResult>> GetEntranceAsync(
        string cookie, string? apiPh = null, CancellationToken ct = default)
    {
        using var client = CreateClient(cookie, apiPh);
        var body = new { movieId = "" };
        var resp = await client.PostAsync<KuaishouEntranceResponse>(
            "/rest/cp/works/v2/video/pc/realize/entrance", body, ct);

        return resp?.Items?.Select(e => new KuaishouEntranceResult(
            Name: e.Name ?? "", Type: e.Type ?? "", BizType: e.BizType ?? "")).ToList() ?? [];
    }

    // ═══ Private ═══

    private KuaishouApiClient CreateClient(string cookie, string? apiPh = null) =>
        new(cookie, _options, apiPh, logger: _logger as ILogger<PlatformHttpClient>);

    private static KuaishouPoiResult ToPoiResult(KuaishouPoiItem item) => new(
        Id: item.Id, IdString: item.IdString, Title: item.Title ?? "",
        Address: item.Address ?? "", City: item.City ?? "", Category: item.Category,
        Latitude: item.Latitude, Longitude: item.Longitude);
}

// ═══ 公共结果类型 ═══

public sealed record KuaishouTagResult(long Id, string Name, long ViewCount);

public sealed record KuaishouPoiResult(
    long Id, string? IdString, string Title, string Address,
    string City, int Category, double Latitude, double Longitude);

public sealed record KuaishouAuthorResult(
    long UserId, string UserName, string? AvatarUrl,
    int PhotoNum, int LikeNum);

public sealed record KuaishouEntranceResult(string Name, string Type, string BizType);
