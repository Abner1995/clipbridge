using System.Text.Json;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// 小红书「选择群聊」—— 群列表 + 发布体绑定（**2026-11-15 接线**）。
///
/// 端点（页面常量 <c>GET_GROUP_LIST</c>，edith host，x-s 签名）：
/// <code>
///   GET /api/im/web/nns/group_list?noteId=
/// </code>
/// 页面 bundle（<c>project-publish-vue.1428a0fb.js</c>）逐字：
/// <code>
///   GET_GROUP_LIST: "".concat(i, "/api/im/web/nns/group_list")
///   GET(c.S.GET_GROUP_LIST, { params: { noteId: e }, transform: true })
///   → 下拉项：{value: e.groupId, label: e.groupName, disabled: !e.linkable,
///              extra: { name: e.groupName, subName: e.desc, avatar: e.avatar }}
///   选中：state.groupBind.value = { groupId, groupName, desc, avatar }
/// </code>
/// 发布体：<c>business_binds.groupBind</c> = 上面那个对象（未选 = 空/null，页面同款）。
///
/// 证据边界：wire 形状来自仓库内 2026-06-17 bundle 逐字；响应键名（snake_case / camelCase）
/// 未抓到真实样本 —— 解析两种都认，<see cref="ParseGroups"/> 是纯函数可自检。
/// </summary>
public sealed class XhsGroupService
{
    private const string GroupListPath = "/api/im/web/nns/group_list";

    private readonly PlatformOptions _options;
    private readonly ILogger<XhsGroupService>? _logger;

    public XhsGroupService(PlatformOptions options, ILogger<XhsGroupService>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 拉账号可绑定的群聊。<paramref name="noteId"/> 是当前笔记 ID（新建笔记传空串，
    /// 页面同款 <c>params:{noteId}</c>）。返回的 <c>RawJson</c> 供探针排障。
    /// 失败抛 <see cref="HttpRequestException"/>（由页面写状态行）。
    /// </summary>
    public async Task<XhsGroupList> ListAsync(
        string cookie, string? noteId = null, CancellationToken ct = default)
    {
        using var client = new XhsApiClient(cookie, _options,
            _logger as ILogger<PlatformHttpClient>);

        var raw = await client.GetEdithRawAsync(
            GroupListPath,
            new Dictionary<string, string> { ["noteId"] = noteId ?? "" },
            ct);

        var (items, ok, message) = ParseGroupsWithStatus(raw.GetRawText());
        if (!ok)
            throw new HttpRequestException(
                $"小红书群聊列表被拒：{(string.IsNullOrWhiteSpace(message) ? "平台未给原因" : message)}");

        _logger?.LogInformation("XHS groups: {Count}", items.Count);
        return new XhsGroupList(items, raw.GetRawText());
    }

    // ═══ 解析（公开 static 供自检 / 排障钉字段映射）═══

    /// <summary>解析响应为条目列表；坏形状按空列表处理，不抛。</summary>
    public static IReadOnlyList<XhsGroupItem> ParseGroups(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseGroupsCore(doc.RootElement).Items;
    }

    /// <summary>解析 + 成功标志（<c>success=false</c> / <c>code≠0</c> 时由调用方报错）。</summary>
    public static (IReadOnlyList<XhsGroupItem> Items, bool Ok, string? Message)
        ParseGroupsWithStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseGroupsCore(doc.RootElement);
    }

    private static (IReadOnlyList<XhsGroupItem> Items, bool Ok, string? Message)
        ParseGroupsCore(JsonElement root)
    {
        var ok = true;
        if (root.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
            ok = false;
        if (root.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var codeValue) && codeValue != 0)
            ok = false;

        var message = Str(root, "msg") ?? Str(root, "message");

        var items = new List<XhsGroupItem>();
        var data = root.TryGetProperty("data", out var d)
                   && d.ValueKind == JsonValueKind.Object ? d : root;

        // data.group_list / data.admin_group_list（2026-11-10 抓包记录），
        // 以及页面 transform 后的 camelCase 变体 —— 两种都认。
        Collect(data, "group_list");
        Collect(data, "groupList");
        Collect(data, "admin_group_list");
        Collect(data, "adminGroupList");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return (items.Where(x => seen.Add(x.GroupId)).ToList(), ok, message);

        void Collect(JsonElement obj, string key)
        {
            if (!obj.TryGetProperty(key, out var arr)
                || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var id = (Str(el, "group_id") ?? Str(el, "groupId") ?? "").Trim();
                var name = (Str(el, "group_name") ?? Str(el, "groupName") ?? "").Trim();
                if (id.Length == 0 || name.Length == 0) continue;

                items.Add(new XhsGroupItem(
                    GroupId: id,
                    GroupName: name,
                    Desc: NullIfEmpty(Str(el, "desc") ?? Str(el, "description")),
                    Avatar: NullIfEmpty(Str(el, "avatar") ?? Str(el, "avatar_url")
                        ?? Str(el, "avatarUrl")),
                    Linkable: Bool(el, "linkable") ?? true));
            }
        }
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool? Bool(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt32(out var n) && n != 0,
            _ => null,
        };
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

/// <summary>群聊列表 + 原始响应（探针排障用；业务侧只用 <see cref="Items"/>）。</summary>
public sealed record XhsGroupList(IReadOnlyList<XhsGroupItem> Items, string RawJson);

/// <summary>
/// 一个可绑定的群聊。来源 <c>group_list</c>；<see cref="GroupId"/> 是发布体
/// <c>business_binds.groupBind.groupId</c> 的取值。
/// </summary>
/// <param name="GroupId">群 ID</param>
/// <param name="GroupName">群名称</param>
/// <param name="Desc">群描述（仅展示）</param>
/// <param name="Avatar">群头像 URL（仅展示）</param>
/// <param name="Linkable">平台是否允许绑定（false = 页面置灰「该群状态异常，暂不支持绑定」）</param>
public sealed record XhsGroupItem(
    string GroupId,
    string GroupName,
    string? Desc,
    string? Avatar,
    bool Linkable = true);
