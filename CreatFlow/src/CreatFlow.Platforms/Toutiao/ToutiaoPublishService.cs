namespace CreatFlow.Platforms.Toutiao;

/// <summary>
/// 头条视频发布（<c>POST /xigua/api/upload/PublishVideo</c>）—— 阶段 5.3。
///
/// 请求体按 2026-11-05 抓包逐字段对齐（<c>requests/video_publish_horizontal.json</c> /
/// <c>video_publish_vertical.json</c> / <c>video_publish_with_collection.json</c>）：
///   · 横版 <c>VideoType=3</c>：有 <c>Abstract</c>、可定时 <c>TimerTime</c>、可挂视频合集
///     <c>PrependPSeriesId</c>；竖版 <c>VideoType=6</c>：**没有**简介 / 定时 / 合集。
///   · <c>HideInfo.HideType</c> 0=公开 / 1=仅我 / 2=粉丝可见。
///   · 抓包 URL 带 <c>msToken</c> / <c>a_bogus</c>；v1 先不带，被拒时按错误原文再补
///     （抖音的 <c>ABogusGenerator</c> 可移植，aid=1231）。
///
/// ⚠️ <c>HashtagList</c> 元素结构来自详情回读（<c>{HashtagId,HashtagName}</c>），
/// PublishVideo 抓包样本没有该字段（提交时没选话题）—— 有值才发，首跑探针确认。
/// </summary>
public sealed class ToutiaoPublishService
{
    /// <summary>视频发布路径（抓包 <c>POST /xigua/api/upload/PublishVideo</c>）</summary>
    public const string PublishVideoPath = "/xigua/api/upload/PublishVideo";

    /// <summary>发布页 Referer（抓包原文）</summary>
    public const string PublishReferer = "https://mp.toutiao.com/profile_v4/xigua/upload-video";

    private readonly PlatformOptions _options;

    public ToutiaoPublishService(PlatformOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 发布一条视频。返回 <see cref="ToutiaoPublishResult"/>（ItemId）；
    /// 平台业务错误 → <see cref="ToutiaoPublishException"/>。
    /// <paramref name="secsdkCsrfToken"/> = extra_data 里页面现签的
    /// <c>x-secsdk-csrf-token</c>（写接口必需）。
    /// </summary>
    public async Task<ToutiaoPublishResult> PublishAsync(
        string cookie, string? secsdkCsrfToken,
        ToutiaoPublishRequest request, CancellationToken ct = default)
    {
        using var client = new ToutiaoApiClient(
            cookie, _options, secsdkCsrfToken, PublishReferer);

        var url = _options.ToutiaoHost.TrimEnd('/') + PublishVideoPath;
        var json = BuildPublishBody(request);
        // PostJsonRawAsync 会再序列化一次 —— 传字符串会被双重转义，先解析成 JsonElement
        using var doc = JsonDocument.Parse(json);
        var body = await client.PostJsonRawAsync(url, doc.RootElement.Clone(), ct);
        return ParsePublishResponse(body);
    }

    // ═══ 纯函数（自检直接验 wire 形体） ═══

    /// <summary>
    /// 组装 PublishVideo 请求体。常量字段与抓包一致：
    /// <c>PublishType=1 / From=mp / EnterFrom=5 / IsNew=true / CreateSource=2 /
    /// TerminalType=01 / OsType=09 / DevicePlatform=pc</c>。
    /// </summary>
    public static string BuildPublishBody(ToutiaoPublishRequest r)
    {
        var portrait = r.VideoType == 6;

        var body = new Dictionary<string, object?>
        {
            ["ItemId"] = "",
            ["Title"] = r.Title,
            ["VideoInfo"] = new Dictionary<string, object?>
            {
                ["Vid"] = r.Vid,
                ["VName"] = r.VName,
                ["ThumbUri"] = r.ThumbUri,
                ["ThumbUrl"] = r.ThumbUrl ?? "",
                ["Duration"] = r.DurationSeconds,
                ["VideoWidth"] = r.Width,
                ["VideoHeight"] = r.Height,
            },
            ["ClaimOrigin"] = r.ClaimOrigin,
            ["SyncPlatform"] = r.SyncPlatform ?? EmptyObject(),
            ["Praise"] = r.Praise,
            ["PublishType"] = 1,
            ["From"] = "mp",
            ["EnterFrom"] = 5,
            ["IsNew"] = true,
            ["VideoType"] = r.VideoType,
            ["ExternalLink"] = r.ExternalLink ?? "",
            ["Label"] = r.Label ?? [],
            ["CompassVideo"] = r.CompassVideo ?? DefaultCompassVideo(),
            ["Commodity"] = r.Commodity ?? EmptyArray(),
            ["CreateSource"] = 2,
            ["HideInfo"] = new Dictionary<string, object?> { ["HideType"] = r.HideType },
            ["CoverTitles"] = r.CoverTitles ?? [],
            ["TemplateName"] = r.TemplateName ?? "",
            ["FilterName"] = r.FilterName ?? "",
            ["AttrsValueMap"] = r.AttrsValueMap ?? EmptyObject(),
            ["etData"] = r.EtData ?? EmptyObject(),
            ["TerminalType"] = "01",
            ["OsType"] = "09",
            ["SoftType"] = r.SoftType,
            ["DevicePlatform"] = "pc",
        };

        // 横版才有简介 / 定时 / 视频合集（竖版抓包样本三样都没有）
        if (!portrait)
        {
            if (!string.IsNullOrWhiteSpace(r.Abstract))
                body["Abstract"] = r.Abstract;
            if (r.TimerTime is { } timer)
                body["TimerTime"] = timer;
            if (!string.IsNullOrWhiteSpace(r.PrependPSeriesId))
                body["PrependPSeriesId"] = r.PrependPSeriesId;
        }

        // 结构化话题：有值才发，空表不出现。
        // ⚠️ 2026-11-09 真账号两轮实测（字符串 / 数字两种 HashtagId）：
        // PublishVideo 收下该字段但**不回显也不生效** —— 发布后 EditItem 与作品列表的
        // ArticleItem 都没有 HashtagList；同账号下较早由网页端编辑过的作品有（说明回读
        // 通道本身有效）。结论：PublishVideo 静默忽略 HashtagList，头条结构化话题只能
        // 靠「正文 #话题」文本（竖版没有正文 → 竖版话题无落点）。字段保留（无害），
        // 等抓到编辑页保存请求再看是否要改走编辑接口。
        if (r.Hashtags is { Count: > 0 })
        {
            body["HashtagList"] = r.Hashtags
                .Select(h => new Dictionary<string, object?>
                {
                    ["HashtagId"] = h.Id,
                    ["HashtagName"] = h.Name,
                })
                .ToList();
        }

        return JsonSerializer.Serialize(body, BodyJsonOptions);
    }

    /// <summary>
    /// 解析 PublishVideo 响应。成功形态（抓包）：
    /// <c>{"data":{"ItemId":"…","Code":0,"Message":"SUCCESS"},"status":0}</c>。
    /// 业务失败 → <see cref="ToutiaoPublishException"/>（带平台原文）。
    /// </summary>
    public static ToutiaoPublishResult ParsePublishResponse(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            throw new ToutiaoPublishException("头条发布：响应为空", bodyText);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bodyText);
        }
        catch (JsonException ex)
        {
            throw new ToutiaoPublishException(
                $"头条发布：响应不是 JSON（{ex.Message}），原文：{Snippet(bodyText)}", bodyText);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ToutiaoPublishException(
                    $"头条发布：响应结构认不出（根节点 {root.ValueKind}），原文：{Snippet(bodyText)}",
                    bodyText);

            var status = GetInt(root, "status") ?? 0;
            var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                ? d
                : default;
            var code = data.ValueKind == JsonValueKind.Object ? GetInt(data, "Code") ?? 0 : 0;
            var message = (data.ValueKind == JsonValueKind.Object ? GetString(data, "Message") : null)
                ?? GetString(root, "message")
                ?? "";

            if (status != 0 || code != 0)
            {
                var hint = code switch
                {
                    -1 => "（登录态失效，请打开头条账号窗口重新登录）",
                    _ => "",
                };
                throw new ToutiaoPublishException(
                    $"头条发布失败：status={status}，code={code}，message={message}{hint}",
                    bodyText);
            }

            if (data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("ItemId", out var idEl)
                || idEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idEl.GetString()))
            {
                throw new ToutiaoPublishException(
                    $"头条发布：响应里没有 data.ItemId，原文：{Snippet(bodyText)}", bodyText);
            }

            return new ToutiaoPublishResult(idEl.GetString()!);
        }
    }

    /// <summary>
    /// 原始 JSON 串 → <see cref="JsonElement"/>（商品 / 懂车帝 / 同步 / 长尾属性原样透传）。
    /// 空串 = null；坏 JSON **在上传前**报错（别等传完几分钟才报 JSON 写错）。
    /// </summary>
    public static JsonElement? ParseOptionalJson(string? raw, string field)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ToutiaoPublishException($"{field}不是合法 JSON：{ex.Message}", raw);
        }
    }

    private static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();
    private static JsonElement EmptyArray() => JsonDocument.Parse("[]").RootElement.Clone();
    private static JsonElement DefaultCompassVideo() =>
        JsonDocument.Parse("""{"CompassVideoId":"","CompassVideoName":"","CompassVideoType":0}""")
            .RootElement.Clone();

    private static int? GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
        && el.TryGetInt32(out var v)
            ? v
            : null;

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string Snippet(string text) =>
        text.Length > 300 ? text[..300] + "…" : text;
}

/// <summary>头条 PublishVideo 入参（平台层不引用 NewApp 的 <c>ToutiaoPayload</c>）。</summary>
public sealed record ToutiaoPublishRequest
{
    public required string Title { get; init; }

    /// <summary>简介 —— <c>Abstract</c>（0~400 字，**仅横版**）</summary>
    public string? Abstract { get; init; }

    /// <summary>CommitUploadInner 返回的 Vid</summary>
    public required string Vid { get; init; }
    /// <summary>原始文件名 —— <c>VideoInfo.VName</c></summary>
    public string VName { get; init; } = "";
    /// <summary>封面 TOS URI —— <c>VideoInfo.ThumbUri</c>（必填）</summary>
    public required string ThumbUri { get; init; }
    /// <summary>封面 URL —— 页面同形是 <c>blob:https://mp.toutiao.com/…</c>（服务端不解析）</summary>
    public string? ThumbUrl { get; init; }
    /// <summary>时长（秒）—— <c>VideoInfo.Duration</c></summary>
    public double DurationSeconds { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>3 = 横版 / 6 = 竖版</summary>
    public int VideoType { get; init; } = 3;

    /// <summary>可见性 —— <c>HideInfo.HideType</c>（0 公开 / 1 仅我 / 2 粉丝可见）</summary>
    public int HideType { get; init; }

    /// <summary>定时（Unix 秒）—— <c>TimerTime</c>，仅横版</summary>
    public long? TimerTime { get; init; }

    public bool ClaimOrigin { get; init; }
    public bool Praise { get; init; }

    /// <summary>视频合集 AlbumId —— <c>PrependPSeriesId</c>（仅横版）</summary>
    public string? PrependPSeriesId { get; init; }

    public string? ExternalLink { get; init; }
    public List<string>? Label { get; init; }
    /// <summary>封面标题 —— <c>CoverTitles</c>（数组；载荷里只有单个标题时执行端包一层）</summary>
    public List<string>? CoverTitles { get; init; }
    public string? TemplateName { get; init; }
    public string? FilterName { get; init; }

    /// <summary>结构化话题（<c>{HashtagId,HashtagName}</c>；元素结构待探针）</summary>
    public List<ToutiaoHashtag>? Hashtags { get; init; }

    /// <summary>商品（原始 JSON 数组）</summary>
    public JsonElement? Commodity { get; init; }
    /// <summary>懂车帝视频结构（原始 JSON 对象）</summary>
    public JsonElement? CompassVideo { get; init; }
    /// <summary>同步平台（原始 JSON 对象）</summary>
    public JsonElement? SyncPlatform { get; init; }
    /// <summary>长尾属性（原始 JSON 对象）</summary>
    public JsonElement? AttrsValueMap { get; init; }
    /// <summary>埋点扩展（原始 JSON 对象）</summary>
    public JsonElement? EtData { get; init; }

    /// <summary>客户端 UA 串 —— 抓包 <c>chrome 156.0.0.0</c></summary>
    public string SoftType { get; init; } = "chrome 156.0.0.0";
}

/// <summary>头条话题项 —— <c>HashtagList[]</c> 元素。</summary>
public sealed record ToutiaoHashtag(string? Id, string Name);

/// <summary>发布结果：<c>data.ItemId</c>。</summary>
public sealed record ToutiaoPublishResult(string ItemId);

/// <summary>头条发布业务错误（HTTP 之外的平台壳错误）。</summary>
public sealed class ToutiaoPublishException : Exception
{
    public string? ResponseBody { get; }

    public ToutiaoPublishException(string message, string? responseBody = null)
        : base(message)
    {
        ResponseBody = responseBody;
    }
}
