namespace CreatFlow.Platforms.Bilibili;

/// <summary>
/// 稿件详情回查（只读）—— <c>GET /x/vupre/web/archive/view</c>，阶段 4.7 / 2.6.10 的回显通道。
///
/// 用途：发布后确认 <c>add/v3</c> 下发的 <c>topic_id</c> / <c>mission_id</c> / <c>dtime</c>
/// 是否真的落到了稿件上（2026-11-09 实测：wire 已被平台接受，但挂载回显当时无通道）。
/// 字段口径见 <c>docs/bilibili/20261104/接口说明书.md</c> §4.2 与
/// <c>requests/video_detail.json</c>（响应 <c>data.archive</c> 含 topic_id / topic_name /
/// mission_id / mission_name / dtime / tag / creation_statement / is_only_self）。
///
/// **纯只读，不进发布链路**。
/// </summary>
public sealed class BilibiliArchiveService
{
    /// <summary>稿件详情路径（抓包：<c>GET /x/vupre/web/archive/view</c>）</summary>
    public const string ArchiveViewPath = "/x/vupre/web/archive/view";

    private readonly PlatformOptions _options;

    public BilibiliArchiveService(PlatformOptions options)
    {
        _options = options;
    }

    /// <summary>按 BV 号拉一次稿件详情。平台 <c>code≠0</c> / 缺 <c>data.archive</c> → <see cref="BilibiliApiException"/>。</summary>
    public async Task<BilibiliArchiveInfo> GetAsync(
        string cookie, string bvid, CancellationToken ct = default)
    {
        var url = BuildArchiveViewUrl(
            bvid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _options.BilibiliHost);
        using var client = new BilibiliApiClient(cookie, _options);
        var body = await client.GetStringAsync(url, BilibiliUploadService.VideoReferer, ct);
        return Parse(body);
    }

    // ═══ 纯函数（自检可直接验 wire 形体） ═══

    /// <summary>
    /// 详情 URL：<c>?topic_grey=1&amp;bvid=…&amp;t=…&amp;x-bili-locale-json=…&amp;x-bili-device-req-json=…</c>
    /// （与抓包 <c>接口说明书 §4.2</c> 同形；locale/device 用 axios 编码，与投稿接口同款）。
    /// </summary>
    public static string BuildArchiveViewUrl(string bvid, long timestampMs, string? host = null)
    {
        var baseHost = string.IsNullOrWhiteSpace(host)
            ? "https://member.bilibili.com"
            : host.TrimEnd('/');
        return $"{baseHost}{ArchiveViewPath}?topic_grey=1"
               + $"&bvid={Uri.EscapeDataString(bvid)}&t={timestampMs}"
               + $"&x-bili-locale-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.LocaleJson)}"
               + $"&x-bili-device-req-json={BilibiliUploadService.AxiosEscape(BilibiliUploadService.DeviceJson)}";
    }

    /// <summary>
    /// 解析详情响应：<c>code=0</c> + <c>data.archive</c> → 回显字段。
    /// <c>code≠0</c> → 报错（-101 追加登录过期引导）；缺 <c>data.archive</c> → 带原文报错。
    /// </summary>
    public static BilibiliArchiveInfo Parse(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            throw new BilibiliApiException("B站稿件详情：响应为空", -1, bodyText);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bodyText);
        }
        catch (JsonException ex)
        {
            throw new BilibiliApiException(
                $"B站稿件详情：响应不是 JSON（{ex.Message}），原文：{Snippet(bodyText)}",
                -1, bodyText);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new BilibiliApiException(
                    $"B站稿件详情：响应结构认不出（根节点 {root.ValueKind}），原文：{Snippet(bodyText)}",
                    -1, bodyText);

            var code = TryGetLong(root, "code") ?? -1;
            var message = TryGetString(root, "message");
            if (code != 0)
            {
                var hint = code == -101 ? "（B站登录已过期，请打开 B站账号窗口重新登录）" : "";
                throw new BilibiliApiException(
                    $"B站稿件详情失败：code={code}，message={message ?? "?"}{hint}",
                    (int)code, bodyText);
            }

            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("archive", out var archive)
                || archive.ValueKind != JsonValueKind.Object)
            {
                throw new BilibiliApiException(
                    $"B站稿件详情：响应里没有 data.archive，原文：{Snippet(bodyText)}",
                    -1, bodyText);
            }

            long? csId = null;
            string? csContent = null;
            if (archive.TryGetProperty("creation_statement", out var cs)
                && cs.ValueKind == JsonValueKind.Object)
            {
                csId = TryGetLong(cs, "id");
                csContent = TryGetString(cs, "content");
            }

            return new BilibiliArchiveInfo(
                Aid: TryGetLong(archive, "aid") ?? 0,
                Bvid: TryGetString(archive, "bvid") ?? "",
                Title: TryGetString(archive, "title") ?? "",
                Tid: TryGetLong(archive, "tid") ?? 0,
                TopicId: TryGetLong(archive, "topic_id") ?? 0,
                TopicName: TryGetString(archive, "topic_name"),
                MissionId: TryGetLong(archive, "mission_id") ?? 0,
                MissionName: TryGetString(archive, "mission_name"),
                Dtime: TryGetLong(archive, "dtime") ?? 0,
                Tag: TryGetString(archive, "tag"),
                IsOnlySelf: IsTruthy(archive, "is_only_self"),
                CreationStatementId: csId,
                CreationStatementContent: csContent);
        }
    }

    private static long? TryGetLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    private static string? TryGetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary><c>is_only_self</c> 抓包里是数字 0/1，但兼容 bool 形态。</summary>
    private static bool IsTruthy(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => el.TryGetInt64(out var n) && n != 0,
            JsonValueKind.String => el.GetString() is "1" or "true",
            _ => false,
        };
    }

    private static string Snippet(string text) =>
        text.Length > 300 ? text[..300] + "…" : text;
}

/// <summary>稿件详情回显（只保留回查关心的字段）。</summary>
public sealed record BilibiliArchiveInfo(
    long Aid,
    string Bvid,
    string Title,
    long Tid,
    long TopicId,
    string? TopicName,
    long MissionId,
    string? MissionName,
    long Dtime,
    string? Tag,
    bool IsOnlySelf,
    long? CreationStatementId,
    string? CreationStatementContent);
