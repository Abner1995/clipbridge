namespace CreatFlow.Platforms.Bilibili;

/// <summary>
/// B站投稿（<c>POST /x/vu/web/add/v3</c>）—— 阶段 4.4。
///
/// 请求体按 2026-11-04 抓包 <c>requests/add_v3.json</c> 逐字段对齐；
/// 字段口径见接口说明书 §2.8（<c>copyright</c> / <c>creation_statement.id</c> /
/// <c>is_only_self</c> / <c>watermark.state</c> 等）。
///
/// ⚠️ 未抓到的字段（提交时页面没选话题 / 没开定时）：
///   · <c>topic_id</c> / <c>mission_id</c> —— 按页面结构推断，首跑真账号探针确认；
///   · 定时 <c>dtime</c> —— 2026-11-04 抓包时「定时发布」开关是关的，所以请求体里没有这个键；
///     2026-11-07 用户实测投稿页有定时开关（最早 ≥5 分钟 / 最晚 ≤15 天）。
///     字段名与语义取自平台自身模型：视频详情/列表 <c>archive.dtime</c>（0 = 立即）、
///     专栏接口 <c>dtime: releaseType===1?0:time</c> —— Unix 秒，首跑真账号探针确认。
/// </summary>
public sealed class BilibiliPublishService
{
    /// <summary>投稿路径（抓包 <c>POST /x/vu/web/add/v3</c>）</summary>
    public const string AddV3Path = "/x/vu/web/add/v3";

    private readonly PlatformOptions _options;

    public BilibiliPublishService(PlatformOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// 投稿。成功返回 <c>aid</c> + <c>bvid</c>；平台 <c>code≠0</c> / 缺字段 →
    /// <see cref="BilibiliApiException"/>（带平台 message 与原始响应）。
    /// </summary>
    public async Task<BilibiliPublishResult> PublishAsync(
        string cookie, BilibiliPublishRequest request, CancellationToken ct = default)
    {
        var csrf = BilibiliUploadService.RequireCsrf(cookie);
        var json = BuildAddV3Body(request);

        using var client = new BilibiliApiClient(cookie, _options);
        var url = BuildAddV3Url(
            csrf, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _options.BilibiliHost);
        var body = await client.PostJsonAsync(
            url, json, BilibiliUploadService.VideoReferer, ct);
        return ParseAddV3Response(body);
    }

    // ═══ 纯函数（自检直接验 wire 形体） ═══

    /// <summary>
    /// 投稿 URL：<c>?web_location=333.1024&amp;t=…&amp;csrf=…&amp;x-bili-locale-json=…&amp;x-bili-device-req-json=…</c>
    /// （参数顺序与封面接口同款 axios 编码，见 <see cref="BilibiliUploadService.AxiosEscape"/>）。
    /// </summary>
    public static string BuildAddV3Url(string csrf, long timestampMs, string? host = null)
    {
        var baseHost = string.IsNullOrWhiteSpace(host)
            ? "https://member.bilibili.com"
            : host.TrimEnd('/');
        return $"{baseHost}{AddV3Path}?web_location=333.1024&t={timestampMs}"
               + $"&csrf={BilibiliUploadService.AxiosEscape(csrf)}"
               + $"&x-bili-locale-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.LocaleJson)}"
               + $"&x-bili-device-req-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.DeviceJson)}";
    }

    /// <summary>
    /// 组装 add/v3 请求体（字段顺序与抓包样本一致）。
    ///
    /// <c>up_close_reply</c> / <c>up_close_danmu</c> 是**关闭**语义 ——
    /// 统一语义「允许评论/弹幕」在这里取反；null = 沿用页面默认（允许 → false）。
    /// <c>topic_id</c> / <c>mission_id</c> 只在有值时出现（抓包样本没有这两个键）。
    /// </summary>
    public static string BuildAddV3Body(BilibiliPublishRequest r)
    {
        var body = new Dictionary<string, object?>
        {
            ["cover"] = r.Cover ?? "",
            ["cover43"] = r.Cover43 ?? "",
            ["ai_cover"] = r.AiCover ?? 0,
            ["is_ab_cover"] = r.IsAbCover ?? 0,
            ["ab_cover_info"] = r.AbCoverInfo,
            ["title"] = r.Title,
            ["copyright"] = r.Copyright,
            ["creation_statement"] = new Dictionary<string, object?>
            {
                ["id"] = r.CreationStatementId ?? 0,
            },
            ["human_type2"] = r.HumanType2,
            ["tid"] = r.Tid,
            ["tag"] = r.Tag,
            ["desc"] = r.Description,
            ["dynamic"] = r.Dynamic ?? "",
            ["recreate"] = 0,
            ["interactive"] = r.Interactive ? 1 : 0,
            ["videos"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["filename"] = r.Filename,
                    ["title"] = r.Title,
                    ["desc"] = r.Description,
                    ["cid"] = r.Cid,
                },
            },
            ["act_reserve_create"] = r.ActReserve ? 1 : 0,
            ["act_reserve_create_title"] = r.ActReserveTitle ?? "",
            ["no_disturbance"] = 0,
            ["is_only_self"] = r.IsOnlySelf ? 1 : 0,
            ["space_hidden"] = r.SpaceHidden ?? 2,
            ["watermark"] = new Dictionary<string, object?>
            {
                ["state"] = r.Watermark ? 1 : 0,
            },
            ["subtitle"] = new Dictionary<string, object?>
            {
                ["open"] = r.SubtitleOpen ? 1 : 0,
                ["lan"] = r.SubtitleLan ?? "",
            },
            ["no_reprint"] = r.NoReprint ? 1 : 0,
            ["up_selection_reply"] = r.UpSelectionReply,
            ["up_close_reply"] = r.AllowComment is false,
            ["up_close_danmu"] = r.AllowDanmaku is false,
            ["dolby"] = r.Dolby ? 1 : 0,
            ["lossless_music"] = r.LosslessMusic ? 1 : 0,
            ["web_os"] = 1,
        };

        // 立即发布**不下发** dtime（与抓包样本同形）；定时才出现（Unix 秒）
        if (r.Dtime is > 0) body["dtime"] = r.Dtime.Value;
        if (r.TopicId is > 0) body["topic_id"] = r.TopicId.Value;
        if (r.MissionId is > 0) body["mission_id"] = r.MissionId.Value;

        // 未选创作声明（None）时**不下发该键**：抓包样本带的是 id=1，平台对 id=0 回
        // 21517「选择的创作声明不存在」整条拒稿（2026-11-09 真账号实测）。None ≠ 0 号声明。
        if (r.CreationStatementId is null or 0) body.Remove("creation_statement");

        // 页面发的是 UTF-8 原文（中文标签不转义）—— 与抓包同形，避免平台侧按原字节校验
        // 时对 \uXXXX 转义产生差异
        return JsonSerializer.Serialize(body, BodyJsonOptions);
    }

    private static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 原始 JSON 串 → <see cref="JsonElement"/>（<c>ab_cover_info</c> 这类复杂结构原样透传）。
    /// 空串 = null；坏 JSON **在上传前**报错（传完几分钟才报「JSON 写错」太伤）。
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
            throw new BilibiliApiException($"{field}不是合法 JSON：{ex.Message}", -1, raw);
        }
    }

    /// <summary>
    /// 解析 add/v3 响应：<c>code=0</c> + <c>data.aid</c> / <c>data.bvid</c>。
    /// <c>code≠0</c> → 报错（-101 追加登录过期引导）；缺字段 → 带原文报错。
    /// </summary>
    public static BilibiliPublishResult ParseAddV3Response(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            throw new BilibiliApiException("B站投稿：响应为空", -1, bodyText);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bodyText);
        }
        catch (JsonException ex)
        {
            throw new BilibiliApiException(
                $"B站投稿：响应不是 JSON（{ex.Message}），原文：{Snippet(bodyText)}",
                -1, bodyText);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new BilibiliApiException(
                    $"B站投稿：响应结构认不出（根节点 {root.ValueKind}），原文：{Snippet(bodyText)}",
                    -1, bodyText);

            var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : -1;
            var message = root.TryGetProperty("message", out var m)
                          && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;

            if (code != 0)
            {
                var hint = code switch
                {
                    -101 => "（B站登录已过期，请打开 B站账号窗口重新登录）",
                    -400 => "（投稿参数被平台拒绝：检查标题 / 标签 / 分区）",
                    _ => "",
                };
                throw new BilibiliApiException(
                    $"B站投稿失败：code={code}，message={message ?? "?"}{hint}",
                    code, bodyText);
            }

            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("aid", out var aidEl)
                || aidEl.ValueKind != JsonValueKind.Number
                || !aidEl.TryGetInt64(out var aid)
                || !data.TryGetProperty("bvid", out var bvidEl)
                || bvidEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(bvidEl.GetString()))
            {
                throw new BilibiliApiException(
                    $"B站投稿：响应里没有 data.aid / data.bvid，原文：{Snippet(bodyText)}",
                    -1, bodyText);
            }

            return new BilibiliPublishResult(aid, bvidEl.GetString()!);
        }
    }

    private static string Snippet(string text) =>
        text.Length > 300 ? text[..300] + "…" : text;
}

/// <summary>
/// 投稿入参（平台层不引用 NewApp 的 <c>BilibiliPayload</c>，由管线逐字段映射）。
/// </summary>
public sealed record BilibiliPublishRequest
{
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    /// <summary>逗号分隔的标签串（1~10 个，<c>add/v3.tag</c>）</summary>
    public string Tag { get; init; } = "";
    /// <summary>UPOS 上传返回的 filename（不含扩展名）</summary>
    public required string Filename { get; init; }
    /// <summary>archive/types/upload 返回的 cid</summary>
    public required long Cid { get; init; }

    public string? Cover { get; init; }       // 16:9
    public string? Cover43 { get; init; }     // 4:3

    public int Tid { get; init; } = 21;
    public int HumanType2 { get; init; } = 1015;
    public int Copyright { get; init; } = 3;
    public int? CreationStatementId { get; init; }
    public string? Dynamic { get; init; }
    public bool NoReprint { get; init; }
    public bool Watermark { get; init; } = true;
    public bool SubtitleOpen { get; init; }
    public string? SubtitleLan { get; init; }

    /// <summary>允许评论；null = 沿用页面默认（允许）。执行端取反成 <c>up_close_reply</c>。</summary>
    public bool? AllowComment { get; init; }
    /// <summary>允许弹幕；null = 沿用页面默认（允许）。执行端取反成 <c>up_close_danmu</c>。</summary>
    public bool? AllowDanmaku { get; init; }
    public bool UpSelectionReply { get; init; }

    public bool ActReserve { get; init; }
    public string? ActReserveTitle { get; init; }
    public int? AiCover { get; init; }
    public int? IsAbCover { get; init; }
    public JsonElement? AbCoverInfo { get; init; }
    public bool Interactive { get; init; }
    public int? SpaceHidden { get; init; }
    public bool Dolby { get; init; }
    public bool LosslessMusic { get; init; }

    /// <summary>
    /// 定时发布 —— <c>dtime</c>（Unix 秒）。null / ≤0 = 立即发布，不发该字段。
    /// 抓包时定时开关是关的（<c>add_v3.json</c> 无此键）；字段名取自平台模型
    /// <c>archive.dtime</c>，窗口 5 分钟 ~ 15 天（2026-11-07 页面实测）。
    /// </summary>
    public long? Dtime { get; init; }

    /// <summary>仅自己可见（<c>is_only_self</c> 1/0，由统一可见性映射）</summary>
    public bool IsOnlySelf { get; init; }

    /// <summary>结构化话题 ID（wire 名待真账号探针；有值才发）</summary>
    public long? TopicId { get; init; }
    /// <summary>活动任务号（wire 名待真账号探针；有值才发）</summary>
    public long? MissionId { get; init; }
}

/// <summary>投稿结果：<c>aid</c> + <c>bvid</c>。</summary>
public sealed record BilibiliPublishResult(long Aid, string Bvid);
