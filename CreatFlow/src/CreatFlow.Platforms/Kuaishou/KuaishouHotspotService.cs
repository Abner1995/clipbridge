using System.Text.Json;
using CreatFlow.Platforms.Kuaishou.Requests;

namespace CreatFlow.Platforms.Kuaishou;

/// <summary>
/// 快手「关联热点」列表（B3 抓包 2026-11-11）：发布页「关联热点」下拉的候选来源。
///
/// 端点（cp 域 POST，Cookie + api_ph + <c>__NS_sig3</c> 签名，与其它 cp 接口同一条 Handler 链）：
/// <code>
///   POST /rest/cp/works/v2/video/pc/hotspot/list
///   body: {"cursor":1,"keyword":"","caption":"","topics":[]}
///   → {"result":1, "data":{
///        "list":[{"hotspotId":6569413,"hotspotContent":"前10个月全国二手房交易增11.4%",
///                 "hotValue":"1175万","feedCount":1,"rising":false,…}],
///        "nextCursor":2}}
/// </code>
///
/// · 打开下拉先请求一次（<c>cursor:1</c>、空 keyword）→ 返回推荐热榜；输入关键词后再请求。
/// · 请求里的 <c>caption</c> / <c>topics</c> 是页面用于相关性推荐的当前作品文案与话题；
///   本服务允许调用方带上 caption（与发布体同一份），topics 未抓到元素结构，恒发 <c>[]</c>。
/// · 无更多时 <c>nextCursor = -2147483648</c>（int.MinValue）；本服务把它归一成「没有下一页」。
///
/// 落点在 submit / snapshot 的同名键：<c>hotspotId</c>（数字）+ <c>hotspotConnectType: 1</c>
/// （见 <see cref="KuaishouPublishService.BuildSubmitRequest"/>）。
/// 证据：<c>docs/kuaishou/wire-2026-11-11.md</c> §4（全程 snapshot/save，未真发布）。
/// </summary>
public sealed class KuaishouHotspotService
{
    private const string Path = "/rest/cp/works/v2/video/pc/hotspot/list";

    /// <summary>首屏游标（页面打开下拉时的值，抓包逐字）。</summary>
    public const long FirstCursor = 1;

    private readonly PlatformOptions _options;
    private readonly ILogger<KuaishouHotspotService>? _logger;

    public KuaishouHotspotService(
        PlatformOptions options, ILogger<KuaishouHotspotService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 拉一批热点候选：<paramref name="keyword"/> 为空 = 推荐热榜；非空 = 按关键词搜索。
    /// </summary>
    /// <param name="cookie">Cookie</param>
    /// <param name="keyword">搜索词（空 = 推荐热榜）</param>
    /// <param name="caption">当前作品文案（页面用于相关性推荐；可空）</param>
    /// <param name="cursor">游标（首屏 = <see cref="FirstCursor"/>；翻页用上一页响应的 NextCursor）</param>
    public async Task<KuaishouHotspotPage> QueryAsync(
        string cookie, string? keyword = null, string? caption = null,
        long cursor = FirstCursor, CancellationToken ct = default)
    {
        using var client = new KuaishouApiClient(cookie, _options,
            logger: _logger as ILogger<PlatformHttpClient>);

        // topics 元素结构未抓到 —— 按抓包恒发空数组，不猜
        var body = new
        {
            cursor,
            keyword = keyword ?? "",
            caption = caption ?? "",
            topics = Array.Empty<string>(),
        };
        var resp = await client.PostAsync<KuaishouHotspotListResponse>(Path, body, ct);
        return Normalize(resp);
    }

    // ═══ 解析（公开 static 供自检 / 排障钉字段映射）═══

    /// <summary>解析 <c>hotspot/list</c> 原始响应（含 envelope）。坏形状按空页处理，不抛。</summary>
    public static KuaishouHotspotPage ParseHotspots(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Long(root, "result") != 1) return KuaishouHotspotPage.Empty;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return KuaishouHotspotPage.Empty;

        var dto = new KuaishouHotspotListResponse
        {
            NextCursor = Long(data, "nextCursor"),
        };
        if (data.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in list.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                (dto.List ??= []).Add(new KuaishouHotspotItemDto
                {
                    HotspotId = Long(el, "hotspotId"),
                    HotspotContent = Str(el, "hotspotContent"),
                    HotValue = Str(el, "hotValue"),
                    FeedCount = Long(el, "feedCount"),
                    Rising = el.TryGetProperty("rising", out var rising)
                        && rising.ValueKind == JsonValueKind.True,
                });
            }
        }
        return Normalize(dto);
    }

    /// <summary>过滤空文案 / 非正 ID；<c>nextCursor ≤ 0</c>（含 int.MinValue 哨兵）归一成「没有下一页」。</summary>
    private static KuaishouHotspotPage Normalize(KuaishouHotspotListResponse? resp)
    {
        var items = new List<KuaishouHotspotResult>();
        foreach (var item in resp?.List ?? [])
        {
            var content = item.HotspotContent?.Trim();
            if (item.HotspotId <= 0 || string.IsNullOrEmpty(content)) continue;
            items.Add(new KuaishouHotspotResult(
                HotspotId: item.HotspotId,
                Content: content,
                HotValue: item.HotValue?.Trim() ?? "",
                FeedCount: item.FeedCount,
                Rising: item.Rising));
        }

        // 平台无更多时回 -2147483648；任何 ≤0 的游标都当作「没有下一页」
        var next = resp?.NextCursor ?? 0;
        return new KuaishouHotspotPage(items, next > 0 ? next : 0);
    }

    private static long Long(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n) ? n : 0;

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

/// <summary>
/// 一页热点候选 + 下一页游标。<see cref="NextCursor"/> = 0 表示没有更多
/// （平台的无更多哨兵 <c>-2147483648</c> 已在解析时归一）。
/// </summary>
public sealed record KuaishouHotspotPage(IReadOnlyList<KuaishouHotspotResult> Items, long NextCursor)
{
    public static KuaishouHotspotPage Empty { get; } = new([], 0);

    public bool HasMore => NextCursor > 0;
}

/// <summary>
/// 一条快手热点候选。来源 <c>hotspot/list</c>；选中后写
/// <c>submit.hotspotId</c> + <c>hotspotConnectType: 1</c>（<see cref="KuaishouPublishService"/>）。
/// </summary>
/// <param name="HotspotId">热点 ID（数字，wire 值）</param>
/// <param name="Content">热点文案（展示 + 快照）</param>
/// <param name="HotValue">热度文案，如 <c>1175万</c>（平台原样，仅展示）</param>
/// <param name="FeedCount">关联视频数（仅展示）</param>
/// <param name="Rising">是否上升热点（仅展示）</param>
public sealed record KuaishouHotspotResult(
    long HotspotId,
    string Content,
    string HotValue,
    long FeedCount,
    bool Rising);
