using System.Globalization;
using System.Text.Json;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书「添加路线」wire 层（**2026-11-15 探针期**，edith host，x-s 签名）。
///
/// 页面 bundle 逐字（<c>project-publish-components.a9d200ce.js</c>，2026-06-17）：
/// <code>
///   POI 搜索  POST /api/sns/v1/mappoi/web_api/query/poi/by/keyword
///             { keyword, pageNo:1, pageSize:50, longitude, latitude, scene:0 }
///             → { poiList: [{ poiId, poiName, address, distance, … }] }
///   POI 定位  POST /api/sns/v1/mappoi/web_api/simple/markers
///             { markers:[{ id: poiId, type:"poi" }], userCenter:"lng,lat" }
///             → { markers:[{ latitude, longitude, poi:{…}, style:{…} }] }
///   算路     POST /api/sns/v1/mappoi/web_api/route/v2
///             { origin:"lng,lat", destination:"lng,lat", userCenter:"lng,lat",
///               waypoints:[], routeTypes:["drivingRoute","walkingRoute",
///               "bicyclingRoute","transitRoute"], source:"travelRoute" }
///   建路线   POST /api/sns/v1/mappoi/web_api/save/travel
///             { saveType:2 /* Finish；1=Save 草稿 */, travelId:"",
///               travelType:"singleRoute"|"multiRoute", travelName, userId,
///               plans:[{ planName, planImg:"", planMarkerImg:"",
///                        elements:[{ id:poiId, type:"poi", lon, lat,
///                                    selectedRouteType }] }],
///               polygon:[{ longitude, latitude, time:0 }, …], travelImg:"" }
///             → { travelId: "…" }
///   回读     POST /api/sns/v1/mappoi/web_api/travel/detail
///   风险     POST /api/sns/v1/mappoi/web_api/travel/riskinfo
/// </code>
///
/// 发布侧（<c>project-publish-vue*.js</c> 的 <c>travelSaved</c> 处理）只落两处：
///   · <c>hash_tag</c> 追加 <c>{ id: travelId, name: routeName, type:"map_traver" }</c>；
///   · <c>common.biz_relations</c> 追加
///     <c>{ biz_type:"POI_MAP_TRAVEL", biz_id: travelId, extra_info: JSON{ travelId,
///       travelType, title:"&lt;名&gt; 包含N条路线", icon:"",
///       link:"xhsdiscover://rn/poi/map?pageMode=travel_publish",
///       mapTravels:[{ travelId, travelType }], capaPoiRich:0 } }</c>。
///
/// 真账号探针（2026-11-15，知新万象）已确认：账号可建路线；<c>polygon</c> 可为空、
/// <c>selectedRouteType</c> 可省（正式实现**不接 route/v2**）。**wire 全是 snake_case**
/// （camelCase 会被 <c>-9000 pageNo invalid</c> 拒收）。发布侧落点见
/// <see cref="XhsPublishService.BuildRouteBizRelation"/>。
/// </summary>
public sealed class XhsRouteService
{
    private const string PoiSearchPath = "/api/sns/v1/mappoi/web_api/query/poi/by/keyword";
    private const string MarkersPath = "/api/sns/v1/mappoi/web_api/simple/markers";
    private const string RoutePath = "/api/sns/v1/mappoi/web_api/route/v2";
    private const string SaveTravelPath = "/api/sns/v1/mappoi/web_api/save/travel";
    private const string TravelDetailPath = "/api/sns/v1/mappoi/web_api/travel/detail";

    /// <summary>页面四种交通方式（route/v2 的 <c>routeTypes</c>；元素里叫 selectedRouteType）。</summary>
    public static readonly string[] TransportTypes =
        ["drivingRoute", "walkingRoute", "bicyclingRoute", "transitRoute"];

    /// <summary>页面拿不到定位时的 fallback 中心（北京）。地点搜索只用来做关键词检索。</summary>
    public const double DefaultCenterLng = 116.397027;
    public const double DefaultCenterLat = 39.91799;

    /// <summary><c>user_center</c> / <c>simple/markers</c> 的 <c>"lng,lat"</c>（固定中心）。</summary>
    public static string DefaultUserCenter =>
        string.Create(CultureInfo.InvariantCulture, $"{DefaultCenterLng},{DefaultCenterLat}");

    private readonly PlatformOptions _options;
    private readonly ILogger<XhsRouteService>? _logger;

    public XhsRouteService(PlatformOptions options, ILogger<XhsRouteService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>POI 关键词搜索。页面在「路线」页传当前位置作中心；探针用固定中心。</summary>
    public async Task<JsonElement> SearchPoiRawAsync(
        string cookie, string keyword, double longitude, double latitude,
        CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        var raw = await client.PostEdithRawAsync(PoiSearchPath, new Dictionary<string, object?>
        {
            // ⚠️ 页面 HTTP 层 `transform:true` 会把 camelCase 递归转 snake_case
            // （与 PK 封面同一层，2026-11-15 实测 camelCase 写 pageNo → -9000 pageNo invalid）
            ["keyword"] = keyword,
            ["page_no"] = 1,
            ["page_size"] = 50,
            ["longitude"] = longitude,
            ["latitude"] = latitude,
            ["scene"] = 0,
        }, ct);

        _logger?.LogInformation("XHS route POI search: keyword={Keyword}", keyword);
        return raw;
    }

    /// <summary>用 poiId 换真实坐标（页面添加地点的必经一步：simple/markers 回 lat/lng）。</summary>
    public async Task<JsonElement> MarkersRawAsync(
        string cookie, string poiId, string userCenter, CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        return await client.PostEdithRawAsync(MarkersPath, new Dictionary<string, object?>
        {
            ["markers"] = new[] { new Dictionary<string, object?> { ["id"] = poiId, ["type"] = "poi" } },
            ["user_center"] = userCenter,
        }, ct);
    }

    /// <summary>两点之间算四种路线（页面逐段调用，只用来画线/取 polygon）。</summary>
    public async Task<JsonElement> RouteRawAsync(
        string cookie, string origin, string destination, string userCenter,
        CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        return await client.PostEdithRawAsync(RoutePath, new Dictionary<string, object?>
        {
            ["origin"] = origin,
            ["destination"] = destination,
            ["user_center"] = userCenter,
            ["waypoints"] = Array.Empty<object>(),
            ["route_types"] = TransportTypes,
            ["source"] = "travelRoute",
        }, ct);
    }

    /// <summary>建/改路线。响应里 <c>travelId</c> 有值才算成功（页面同口径）。</summary>
    public async Task<JsonElement> SaveTravelRawAsync(
        string cookie, object body, CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        return await client.PostEdithRawAsync(SaveTravelPath, body, ct);
    }

    /// <summary>回读路线详情（编辑已有路线用；探针拿来确认 save 真的落库）。</summary>
    public async Task<JsonElement> TravelDetailRawAsync(
        string cookie, string travelId, CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        return await client.PostEdithRawAsync(TravelDetailPath,
            new Dictionary<string, object?> { ["travel_id"] = travelId }, ct);
    }

    // ═══ 业务层（发布页「添加路线」）═══

    /// <summary>
    /// POI 关键词搜索。失败抛 <see cref="HttpRequestException"/>（页面写状态行）。
    /// 中心用页面 fallback（北京）；实测关键词是主角，搜索结果 nationwide。
    /// </summary>
    public async Task<IReadOnlyList<XhsRoutePoi>> SearchPoiAsync(
        string cookie, string keyword, CancellationToken ct = default)
    {
        var raw = await SearchPoiRawAsync(
            cookie, keyword, DefaultCenterLng, DefaultCenterLat, ct);
        var (items, ok, message) = ParsePoiListWithStatus(raw.GetRawText());
        if (!ok)
            throw new HttpRequestException(
                $"小红书地点搜索被拒：{(string.IsNullOrWhiteSpace(message) ? "平台未给原因" : message)}");
        return items;
    }

    /// <summary>用 poiId 换经纬度（<c>simple/markers</c>）；平台没给坐标就抛错。</summary>
    public async Task<XhsRoutePoint> ResolvePointAsync(
        string cookie, string poiId, string fallbackName, CancellationToken ct = default)
    {
        var raw = await MarkersRawAsync(cookie, poiId, DefaultUserCenter, ct);
        return ParseMarkerPoint(raw.GetRawText(), poiId, fallbackName)
            ?? throw new HttpRequestException("小红书地点定位失败：平台没有返回经纬度");
    }

    /// <summary>
    /// 建路线（<c>save_type=2</c> = 完成）。至少 2 个地点；成功返回 <c>travelId</c>。
    /// 发布时用 <see cref="XhsRouteSaved"/> 的四个字段拼 hash_tag + biz_relations。
    /// </summary>
    public async Task<XhsRouteSaved> SaveRouteAsync(
        string cookie, string userId, string routeName,
        IReadOnlyList<XhsRoutePoint> points, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new HttpRequestException(
                "缺少小红书用户 ID（Cookie 里没有 x-user-id-creator）—— 重新登录账号后再试");
        if (points is null || points.Count < 2)
            throw new ArgumentException("路线至少需要 2 个地点", nameof(points));

        var name = string.IsNullOrWhiteSpace(routeName) ? "给路线起个名字吧" : routeName.Trim();
        var body = BuildSaveTravelBody(userId, name, points);
        var raw = await SaveTravelRawAsync(cookie, body, ct);
        var travelId = ParseTravelId(raw.GetRawText());
        if (string.IsNullOrWhiteSpace(travelId))
            throw new HttpRequestException(
                $"小红书建路线被拒：{Summarize(raw.GetRawText())}");

        _logger?.LogInformation("XHS route saved: travelId={TravelId} name={Name} points={Count}",
            travelId, name, points.Count);
        return new XhsRouteSaved(travelId, name, "singleRoute", 1, raw.GetRawText());
    }

    // ═══ 纯函数（自检 / 排障钉字段映射）═══

    /// <summary>
    /// 建路线的请求体（snake_case，页面 transform 后的 wire）。单天 = <c>singleRoute</c>；
    /// 探针已验：<c>polygon:[]</c> 与不填 <c>selected_route_type</c> 都被接受。
    /// </summary>
    public static Dictionary<string, object?> BuildSaveTravelBody(
        string userId, string travelName, IReadOnlyList<XhsRoutePoint> points)
    {
        var elements = new List<object?>();
        foreach (var p in points)
        {
            elements.Add(new Dictionary<string, object?>
            {
                ["id"] = p.PoiId,
                ["type"] = "poi",
                ["lon"] = p.Lon,
                ["lat"] = p.Lat,
            });
        }

        return new Dictionary<string, object?>
        {
            ["save_type"] = 2, // Finish（1=Save 草稿）
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
            // 探针已验：空 polygon 被接受；不接 route/v2 就不必算 bbox。
            ["polygon"] = Array.Empty<object>(),
            ["travel_img"] = "",
        };
    }

    /// <summary>解析 POI 搜索响应（<c>data.poi_list</c> / <c>poi_list</c>；camel 兼容）。</summary>
    public static (IReadOnlyList<XhsRoutePoi> Items, bool Ok, string? Message)
        ParsePoiListWithStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var ok = true;
        if (root.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False) ok = false;
        if (root.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var codeNum) && codeNum != 0) ok = false;
        var message = root.TryGetProperty("msg", out var msg)
            && msg.ValueKind == JsonValueKind.String ? msg.GetString() : null;

        var list = FindArray(root, "poi_list") ?? FindArray(root, "poiList");
        var items = new List<XhsRoutePoi>();
        if (list is { } arr)
        {
            foreach (var it in arr.EnumerateArray())
            {
                var poiId = Str(it, "poi_id") ?? Str(it, "poiId") ?? Str(it, "id");
                if (string.IsNullOrWhiteSpace(poiId)) continue;
                items.Add(new XhsRoutePoi(
                    poiId,
                    Str(it, "poi_name") ?? Str(it, "poiName") ?? Str(it, "name") ?? "",
                    Str(it, "address") ?? Str(it, "full_address") ?? Str(it, "fullAddress") ?? ""));
            }
        }

        return (items, ok, message);
    }

    /// <summary>解析 <c>simple/markers</c> 响应里的第一个地点（latitude/longitude + poi）。</summary>
    public static XhsRoutePoint? ParseMarkerPoint(
        string json, string fallbackId, string fallbackName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var markers = FindArray(root, "markers");
        if (markers is not { } arr || arr.GetArrayLength() == 0) return null;

        var m = arr[0];
        if (!TryNumber(m, "longitude", out var lon) || !TryNumber(m, "latitude", out var lat))
            return null;

        var poi = m.TryGetProperty("poi", out var p) && p.ValueKind == JsonValueKind.Object
            ? p : default;
        var name = poi.ValueKind == JsonValueKind.Object
            ? Str(poi, "poi_name") ?? Str(poi, "poiName") ?? Str(poi, "name") ?? fallbackName
            : fallbackName;
        var id = poi.ValueKind == JsonValueKind.Object
            ? Str(poi, "poi_id") ?? Str(poi, "poiId") ?? fallbackId
            : fallbackId;
        return new XhsRoutePoint(id ?? fallbackId, name ?? fallbackName, lon, lat);
    }

    /// <summary>从 save/travel 响应取 <c>travel_id</c>（<c>data.travel_id</c> 兼容）。</summary>
    public static string? ParseTravelId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Str(root, "travel_id") is { Length: > 0 } id) return id;
        if (Str(root, "travelId") is { Length: > 0 } idCamel) return idCamel;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (Str(data, "travel_id") is { Length: > 0 } nested) return nested;
            if (Str(data, "travelId") is { Length: > 0 } nestedCamel) return nestedCamel;
        }
        return null;
    }

    private static JsonElement? FindArray(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var direct)
            && direct.ValueKind == JsonValueKind.Array)
            return direct;
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(name, out var nested)
            && nested.ValueKind == JsonValueKind.Array)
            return nested;
        return null;
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool TryNumber(JsonElement obj, string key, out double value)
    {
        value = 0;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v))
            return false;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static string Summarize(string json) =>
        json.Length > 500 ? json[..500] + "…" : json;
}

/// <summary>地点搜索候选（发布页「添加路线」）。</summary>
public sealed record XhsRoutePoi(string PoiId, string Name, string Address);

/// <summary>路线的一个点（poiId + 名称 + 真实经纬度；经纬度来自 simple/markers）。</summary>
public sealed record XhsRoutePoint(string PoiId, string Name, double Lon, double Lat);

/// <summary>建路线成功的结果 —— 发布体 hash_tag / biz_relations 的四个字段来源。</summary>
public sealed record XhsRouteSaved(
    string TravelId,
    string RouteName,
    string TravelType,
    int PlanAmount,
    string RawJson);
