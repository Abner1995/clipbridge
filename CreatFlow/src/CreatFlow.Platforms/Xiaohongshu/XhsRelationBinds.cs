using System.Globalization;
using System.Text.Json;

namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>
/// <c>business_binds.optionRelationList</c> 的合并规则 —— 引用笔记（A5）+ 关联活动（A6）。
///
/// 这个数组是小红书的「业务关系」总入口：原创声明（<c>ORIGINAL_STATEMENT</c>，由
/// <see cref="XhsContentLabelOptions.BuildDeclBind"/> 产出）、引用笔记（<c>REF_POST</c>）、
/// 官方活动（<c>ACTIVITY_COMPONENT</c>）都追加到**同一个**数组里，元素形如
/// <c>{type, relationList:[{bizType, bizId, extraInfo}]}</c>。
///
/// wire 证据：
///   · <c>REF_POST</c> —— 仓库内 bundle 逐字
///     （<c>docs/xiaohongshu/上传/20260617/js/project-publish-vue.1428a0fb.js</c>）：
///     <code>
///     if (L.value?.noteId) { var x = L.value.noteId;
///       V.push({type:"REF_POST", relationList:[{bizType:"REF_POST", bizId:x,
///         extraInfo:JSON.stringify({ref_id:x, ref_type:"note", note_id:x})}]}) }
///     </code>
///     即 <c>bizId</c> = 笔记 ID，<c>extraInfo</c> 是**字符串化**的同值 JSON。
///   · <c>ACTIVITY_COMPONENT</c> —— 线上 bundle（<c>project-publish-vue.0905aa3a.js</c>，
///     2026-11-10 抓包时在网，**未存进仓库**）：
///     <code>
///     {"type":"ACTIVITY_COMPONENT","relationList":[{"bizType":"ACTIVITY_COMPONENT",
///       "bizId":&lt;id&gt;,"extraInfo":"{\"id\":...,\"name\":...,\"start_time\":...,\"end_time\":...}"}]}
///     </code>
///     <c>extraInfo</c> 各值的 JSON 类型按活动列表接口的同名字段推断（<c>activity_id</c> 数字、
///     <c>start_time/end_time</c> 毫秒），真账号回读仍待 P5。
///
/// 合并口径与页面一致：**先过滤同类型条目，再追加一条**（同类型只留一个）。
/// </summary>
public static class XhsRelationBinds
{
    /// <summary>引用笔记的 <c>type</c> / <c>bizType</c></summary>
    public const string RefPostType = "REF_POST";

    /// <summary>关联活动的 <c>type</c> / <c>bizType</c></summary>
    public const string ActivityType = "ACTIVITY_COMPONENT";

    /// <summary>小红书笔记 / 合集 ID 都是 24 位十六进制（抓包与页面同款）。</summary>
    public const int NoteIdLength = 24;

    /// <summary>
    /// 笔记 ID 是否可用（24 位十六进制）。只用来挡明显坏档 —— 正常路径的 ID
    /// 由平台笔记列表点选产生，不会手填。
    /// </summary>
    public static bool IsValidNoteId(string? id) =>
        id is { Length: NoteIdLength } && id.All(Uri.IsHexDigit);

    /// <summary>活动 ID 是否可下发（纯数字 —— 平台 <c>activity_id</c> 是数字）。</summary>
    public static bool IsValidActivityId(string? id) =>
        id is { Length: > 0 and <= 18 } && id.All(char.IsAsciiDigit);

    /// <summary>
    /// 把 <paramref name="baseEntries"/>（原创声明等已有条目）与引用笔记 / 活动合并成一个数组。
    /// 同一类型先过滤再追加；坏 ID 跳过（返回空数组 = 不追加）。
    /// </summary>
    public static List<object> Merge(
        IEnumerable<object>? baseEntries,
        string? referencedNoteId,
        IReadOnlyList<XhsActivityBind>? activities)
    {
        var list = baseEntries?.ToList() ?? [];

        var noteId = (referencedNoteId ?? "").Trim();
        list.RemoveAll(e => HasType(e, RefPostType));
        if (IsValidNoteId(noteId)) list.Add(BuildRefPostEntry(noteId));

        var validActivities = (activities ?? [])
            .Where(a => IsValidActivityId(a.Id))
            .ToList();
        list.RemoveAll(e => HasType(e, ActivityType));
        if (validActivities.Count > 0) list.Add(BuildActivityEntry(validActivities));

        return list;
    }

    /// <summary>引用笔记条目 —— <c>{type:"REF_POST", relationList:[{bizType,bizId,extraInfo}]}</c>。</summary>
    public static Dictionary<string, object?> BuildRefPostEntry(string noteId) => new()
    {
        ["type"] = RefPostType,
        ["relationList"] = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["bizType"] = RefPostType,
                ["bizId"] = noteId,
                // 页面是 JSON.stringify({ref_id, ref_type:"note", note_id}) —— 字符串化 JSON
                ["extraInfo"] = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["ref_id"] = noteId,
                    ["ref_type"] = "note",
                    ["note_id"] = noteId,
                }),
            },
        },
    };

    /// <summary>
    /// 关联活动条目 —— 多个活动塞进**同一个** <c>ACTIVITY_COMPONENT</c> 的 relationList。
    /// 缺名称 / 起止时间就**不写那一项**（不编造空值），预检会提示快照不全。
    /// </summary>
    public static Dictionary<string, object?> BuildActivityEntry(
        IReadOnlyList<XhsActivityBind> activities)
    {
        var relations = new List<object>();
        foreach (var a in activities)
        {
            var id = (a.Id ?? "").Trim();
            if (!IsValidActivityId(id)) continue;

            var info = new Dictionary<string, object?>
            {
                // activity_id 在平台列表里是数字；能解析就按数字下发（保真），否则原样字符串
                ["id"] = long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var numericId) ? numericId : id,
            };
            if (!string.IsNullOrWhiteSpace(a.Name)) info["name"] = a.Name.Trim();
            if (a.StartTimeMs is > 0) info["start_time"] = a.StartTimeMs.Value;
            if (a.EndTimeMs is > 0) info["end_time"] = a.EndTimeMs.Value;

            relations.Add(new Dictionary<string, object?>
            {
                ["bizType"] = ActivityType,
                ["bizId"] = id,
                ["extraInfo"] = JsonSerializer.Serialize(info),
            });
        }

        return new Dictionary<string, object?>
        {
            ["type"] = ActivityType,
            ["relationList"] = relations,
        };
    }

    private static bool HasType(object entry, string type) =>
        entry is Dictionary<string, object?> d
        && d.TryGetValue("type", out var value)
        && value is string text
        && text == type;
}
