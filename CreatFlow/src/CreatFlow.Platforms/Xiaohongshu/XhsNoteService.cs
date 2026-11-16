using System.Text.Json;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书笔记详情读取 —— 发布后的**回读通道**（诊断/核对用，不参与发布链路）。
///
/// API: <c>GET /web_api/sns/capa/postgw/note/detail?note_id=…&amp;source=web&amp;edit_mode=1</c>
/// （edith host；抓包样本 docs/xiaohongshu/20261105/requests/note_detail_edit.json）。
///
/// 响应 <c>data</c> 里带 <c>hash_tag</c>（话题数组，元素 <c>{id,name,type}</c>）、<c>privacy.type</c>、
/// <c>post_time</c>、<c>post_locs</c>、<c>title</c>/<c>name</c>/<c>desc</c>、<c>cover.fileid</c>、
/// <c>video_info</c> 等 —— 回答「发布体下发的 hash_tag / 可见性 / 定时是否真的落到笔记上」。
///
/// 注意：该接口的 query 用 **snake_case** <c>note_id</c>；而 <c>note/delete</c> 的 body 也是
/// snake_case（平台不一致，见 XhsVideoService 注释）。GET 参数走字典，不受 CamelCase 命名策略影响。
///
/// x-s 签名需要 App 的 WebView 宿主（XhsPlatform.SetSigner），纯 HTTP 进程会 406。
/// </summary>
public sealed class XhsNoteService
{
    private const string DetailPath = "/web_api/sns/capa/postgw/note/detail";

    private readonly PlatformOptions _options;
    private readonly ILogger<XhsNoteService>? _logger;

    public XhsNoteService(PlatformOptions options, ILogger<XhsNoteService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 拉「已发布笔记」列表，返回响应根 JSON 原文（诊断用：区分「真的没有笔记」与
    /// 「接口拒了 / 形状变了」—— typed 版 <see cref="XhsVideoService.ListAsync"/> 解析失败时
    /// 也只是一份空列表，看不出原因）。
    /// GET /api/galaxy/v2/creator/note/user/posted（creator host）。
    /// </summary>
    public async Task<JsonElement> ListPostedRawAsync(
        string cookie, int tab = 0, int page = 0, CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        return await client.GetCreatorRawAsync(
            "/api/galaxy/v2/creator/note/user/posted",
            new Dictionary<string, string>
            {
                ["tab"] = tab.ToString(),
                ["page"] = page.ToString(),
            }, ct);
    }

    /// <summary>
    /// 拉「已发布笔记」候选列表（A5 引用笔记选择器）。同
    /// <see cref="ListPostedRawAsync"/> 的端点，但解析成轻量条目 ——
    /// 发布页只需要「ID + 标题 + 封面」。
    ///
    /// 抓包样本（<c>docs/xiaohongshu/接口/20260616/posted.json</c>）：
    /// <c>data.notes[]</c> 的键是 <c>id</c>（没有 <c>note_id</c>）、<c>display_title</c>、
    /// <c>images_list[0].url</c>（封面）、<c>type</c>、<c>time</c>（字符串）。
    /// 失败（success=false / code≠0）抛 <see cref="HttpRequestException"/>，由页面写状态行。
    /// </summary>
    public async Task<IReadOnlyList<XhsNoteItem>> ListPostedAsync(
        string cookie, int tab = 1, int page = 0, CancellationToken ct = default)
    {
        var root = await ListPostedRawAsync(cookie, tab, page, ct);

        var ok = true;
        if (root.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
            ok = false;
        if (root.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var codeValue) && codeValue != 0)
            ok = false;
        if (!ok)
        {
            var msg = root.TryGetProperty("msg", out var m) ? m.ToString() : "";
            throw new HttpRequestException(
                $"小红书笔记列表被拒：{(string.IsNullOrWhiteSpace(msg) ? "平台未给原因" : msg)}");
        }

        var notes = new List<XhsNoteItem>();
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("notes", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return notes;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in arr.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Object) continue;
            // 样本只有 id；note_id 是历史/列表接口的另一种叫法，两个都认
            var id = (Str(n, "note_id") ?? Str(n, "id") ?? "").Trim();
            if (id.Length == 0 || !seen.Add(id)) continue;

            var title = (Str(n, "display_title") ?? Str(n, "title") ?? "").Trim();

            string? cover = null;
            if (n.TryGetProperty("images_list", out var images)
                && images.ValueKind == JsonValueKind.Array
                && images.GetArrayLength() > 0
                && images[0].ValueKind == JsonValueKind.Object)
            {
                cover = Str(images[0], "url");
            }

            var type = Str(n, "type");
            var time = n.TryGetProperty("time", out var tv)
                ? (tv.ValueKind == JsonValueKind.String ? tv.GetString() : tv.ToString())
                : null;

            notes.Add(new XhsNoteItem(id, title,
                string.IsNullOrWhiteSpace(cover) ? null : cover,
                string.IsNullOrWhiteSpace(type) ? null : type,
                string.IsNullOrWhiteSpace(time) ? null : time));
        }

        _logger?.LogInformation("XHS posted notes: tab={Tab}, count={Count}", tab, notes.Count);
        return notes;
    }

    /// <summary>
    /// 拉笔记详情，返回响应根 JSON（<c>{code, success, msg, data}</c>）——
    /// 调用方可直接读 <c>data.hash_tag</c> 等字段。业务失败抛 <see cref="HttpRequestException"/>。
    /// </summary>
    public async Task<JsonElement> GetDetailAsync(
        string cookie, string noteId, CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        var root = await client.GetEdithRawAsync(DetailPath, new Dictionary<string, string>
        {
            ["note_id"] = noteId,
            ["source"] = "web",
            ["edit_mode"] = "1",
        }, ct);

        // 成功判定：code == 0 且 success == true（同 delete 口径 —— 没有 result 字段）
        var ok = true;
        if (root.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
        {
            ok = false;
        }
        if (root.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var codeValue) && codeValue != 0)
        {
            ok = false;
        }

        if (!ok)
        {
            var msg = root.TryGetProperty("msg", out var m) ? m.ToString() : "";
            _logger?.LogWarning("XHS note detail rejected: noteId={NoteId}, msg={Msg}", noteId, msg);
            throw new HttpRequestException(
                $"小红书笔记详情被拒：{(string.IsNullOrWhiteSpace(msg) ? "平台未给原因" : msg)}");
        }

        _logger?.LogInformation("XHS note detail: noteId={NoteId}", noteId);
        return root;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

/// <summary>
/// 引用笔记候选（A5，2026-11-11）—— 已发布笔记的轻量快照。
/// <paramref name="Id"/> 是 24 位十六进制笔记 ID（<c>REF_POST.bizId</c>）；
/// <paramref name="Cover"/>/<paramref name="Type"/>/<paramref name="TimeText"/> 只供 UI 辨认，不进 wire。
/// </summary>
public sealed record XhsNoteItem(
    string Id,
    string Title,
    string? Cover = null,
    string? Type = null,
    string? TimeText = null);
