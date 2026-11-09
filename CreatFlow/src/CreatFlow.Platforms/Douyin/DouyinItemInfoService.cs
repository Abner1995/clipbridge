namespace CreatFlow.Platforms.Douyin;

using CreatFlow.Platforms.Core;

/// <summary>
/// 抖音作品详情（创作中心版）—— <c>GET /web/api/media/item/info/?item_id=…</c>。
///
/// **只读诊断/回查**，不进发布链路：2026-11-09 验证「官方活动是否真的挂到作品上」时，
/// Web 端 <c>aweme/detail</c> 只回视频流、活动字段为空；创作中心详情接口的响应
/// （抓包 <c>docs/douyin/20261105/requests/video_detail.json</c>）带顶层
/// <c>activity_list</c> 与 <c>aweme</c> 对象，是唯一可能回读活动参与情况的通道。
///
/// 签名与 <see cref="DouyinHashtagService"/> / <c>DouyinTopicApi</c> 同一条路：
/// 本地生成 msToken + a_bogus（aid=1128），Cookie 走 CookieAuthHandler。
/// </summary>
public sealed class DouyinItemInfoService
{
    /// <summary>作品详情路径（创作中心）</summary>
    public const string ItemInfoPath = "/web/api/media/item/info/";

    private readonly PlatformOptions _options;

    public DouyinItemInfoService(PlatformOptions options)
    {
        _options = options;
    }

    public async Task<DouyinItemInfoResult> GetAsync(
        string cookie, string itemId, CancellationToken ct = default)
    {
        var extra = new Dictionary<string, string>
        {
            ["item_id"] = itemId,
            ["aid"] = "1128",
        };
        var baseParams = PlatformHttpClient.BuildQueryString(extra, env: _options.Environment);
        var msToken = MsTokenGenerator.Generate();
        var aBogus = ABogusGenerator.Generate(
            baseParams, _options.Environment?.UserAgent ?? _options.UserAgent, aid: 1128);

        var qs = PlatformHttpClient.BuildQueryString(
            extra, msToken: msToken, aBogus: aBogus, env: _options.Environment);
        var url = $"{_options.DouyinCreatorHost}{ItemInfoPath}?{qs}";

        using var client = BuildClient(cookie);
        var body = await client.GetStringAsync(url, ct);
        return Parse(body, itemId);
    }

    private PlatformHttpClient BuildClient(string cookie)
    {
        var loggingHandler = new Core.Handlers.LoggingHandler(null);
        var cookieHandler = new Core.Handlers.CookieAuthHandler(cookie);
        cookieHandler.InnerHandler = loggingHandler;
        return new PlatformHttpClient(cookieHandler, _options, null);
    }

    /// <summary>
    /// 解析详情：<c>status_code=0</c> 后，把**顶层**与 <c>aweme</c> 对象里所有名字含
    /// <c>activity</c> 的字段原样收出来（结构没抓到过的字段不猜语义，先看原文）。
    /// </summary>
    public static DouyinItemInfoResult Parse(string bodyText, string itemId)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            throw new InvalidOperationException("抖音作品详情：响应为空");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bodyText);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"抖音作品详情：响应不是 JSON（{ex.Message}），原文：{Snippet(bodyText)}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(
                    $"抖音作品详情：响应结构认不出（根节点 {root.ValueKind}），原文：{Snippet(bodyText)}");

            var status = root.TryGetProperty("status_code", out var sc)
                         && sc.ValueKind == JsonValueKind.Number
                         && sc.TryGetInt32(out var code)
                ? code
                : -1;
            if (status != 0)
            {
                var msg = root.TryGetProperty("status_msg", out var m)
                          && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null;
                throw new InvalidOperationException(
                    $"抖音作品详情失败：status_code={status}，message={msg ?? "?"}，原文：{Snippet(bodyText)}");
            }

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            CollectActivityFields(root, "", fields);

            if (root.TryGetProperty("aweme", out var aweme) && aweme.ValueKind == JsonValueKind.Object)
                CollectActivityFields(aweme, "aweme.", fields);

            var desc = aweme.ValueKind == JsonValueKind.Object && aweme.TryGetProperty("desc", out var d)
                       && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;

            return new DouyinItemInfoResult(itemId, desc, fields);
        }
    }

    /// <summary>收集对象里名字含 activity 的字段（原样 JSON 文本）。</summary>
    private static void CollectActivityFields(
        JsonElement obj, string prefix, Dictionary<string, string> sink)
    {
        if (obj.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Contains("activity", StringComparison.OrdinalIgnoreCase))
                sink[prefix + prop.Name] = prop.Value.GetRawText();
        }
    }

    private static string Snippet(string text) =>
        text.Length > 300 ? text[..300] + "…" : text;
}

/// <summary>作品详情回显：desc + 所有 activity 相关字段（原样 JSON）。</summary>
public sealed record DouyinItemInfoResult(
    string ItemId,
    string? Desc,
    IReadOnlyDictionary<string, string> ActivityFields);
