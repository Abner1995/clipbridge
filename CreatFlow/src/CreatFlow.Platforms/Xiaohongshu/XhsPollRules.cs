namespace CreatFlow.Platforms.Xiaohongshu;

/// <summary>小红书投票配置的状态（<see cref="XhsPollRules.Analyze"/>）。</summary>
public enum XhsPollState
{
    /// <summary>用户没配置投票（问题与选项都空）—— 不创建 widget、发布体不带 hash_tag 条目。</summary>
    None,

    /// <summary>配置了一半（缺问题 / 选项为空 / 数量不在 2~5）—— 预检提示、发布时跳过投票。</summary>
    Invalid,

    /// <summary>可以创建投票。</summary>
    Ready,
}

/// <summary>
/// 小红书「添加投票」（互动组件，2026-11-11 A4）—— 规则与 wire 常量。
///
/// 链路（2026-11-10 真账号抓包 <c>capture-2026-11-10T08-33-18.jsonl</c> +
/// 线上 bundle <c>project-publish-vue.0905aa3a.js</c>）：
/// <code>
/// ① POST https://creator.xiaohongshu.com/api/galaxy/v2/creator/note/widget/create_interact
///    {"widget_info":{"widget_id":"","widget_name":"问题","widget_type":"interact_vote",
///      "options":[{"option_id":1,"option_name":"A"}],"is_creator":true}}
///    → {"data":{"widget_id":"138189749100365837"},"code":0,"success":true}
/// ② 发布体 common.hash_tag **末尾追加**
///    {"id":&lt;widget_id&gt;,"name":"问题","type":"INTERACT_VOTE"}
///    （页面 hashTag 合并函数：先过滤同类型，再 concat）
/// </code>
///
/// widget 是平台侧对象：**每次发布新建一个**（页面同款；草稿复用不回收 ——
/// 没挂上笔记的孤儿 widget 在平台侧不可见）。
/// 规则：2~5 个选项、问题/选项不能为空。
/// </summary>
public static class XhsPollRules
{
    /// <summary>create_interact 的 <c>widget_type</c>（抓包逐字）</summary>
    public const string CreateWidgetType = "interact_vote";

    /// <summary>发布体 hash_tag 的 <c>type</c>（bundle 逐字）</summary>
    public const string HashTagType = "INTERACT_VOTE";

    public const int MinOptions = 2;
    public const int MaxOptions = 5;

    /// <summary>
    /// 归一化后的投票计划。
    /// <see cref="Question"/> / <see cref="Options"/> 都是**去空白后的可执行值**
    /// （<see cref="State"/> = None/Invalid 时不要拿去建 widget）。
    /// </summary>
    public readonly record struct Plan(
        XhsPollState State, string Question, IReadOnlyList<string> Options, string? Error)
    {
        public bool HasVote => State == XhsPollState.Ready;
    }

    /// <summary>
    /// 把存档里的原始输入（问题 + 可能带空行的选项）归一成可执行计划。
    /// **永不抛** —— 预检拿它出 Warning、发布端拿它决定「跳过投票」；
    /// 投票是可选组件，不能因此炸掉整个发布。
    /// </summary>
    public static Plan Analyze(string? question, IEnumerable<string>? options)
    {
        var q = (question ?? "").Trim();
        var cleaned = (options ?? [])
            .Select(o => (o ?? "").Trim())
            .Where(o => o.Length > 0)
            .ToList();

        if (q.Length == 0 && cleaned.Count == 0)
            return new Plan(XhsPollState.None, "", [], null);
        if (q.Length == 0)
            return new Plan(XhsPollState.Invalid, "", cleaned, "没问题文字");
        if (cleaned.Count == 0)
            return new Plan(XhsPollState.Invalid, q, cleaned, "没有选项");
        if (cleaned.Count < MinOptions)
            return new Plan(XhsPollState.Invalid, q, cleaned,
                $"选项不足 {MinOptions} 个（当前 {cleaned.Count} 个）");
        if (cleaned.Count > MaxOptions)
            return new Plan(XhsPollState.Invalid, q, cleaned,
                $"选项超过 {MaxOptions} 个（当前 {cleaned.Count} 个）");

        return new Plan(XhsPollState.Ready, q, cleaned, null);
    }

    /// <summary>
    /// create_interact 请求体（抓包逐字形状；<c>option_id</c> 从 1 连续编号，
    /// 问题/选项的校验由 <see cref="Analyze"/> 负责）。
    /// </summary>
    public static Dictionary<string, object?> BuildCreateBody(
        string question, IReadOnlyList<string> options)
    {
        var list = new List<Dictionary<string, object?>>(options.Count);
        for (var i = 0; i < options.Count; i++)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["option_id"] = i + 1,
                ["option_name"] = options[i],
            });
        }

        return new Dictionary<string, object?>
        {
            ["widget_info"] = new Dictionary<string, object?>
            {
                ["widget_id"] = "",
                ["widget_name"] = question,
                ["widget_type"] = CreateWidgetType,
                ["options"] = list,
                ["is_creator"] = true,
            },
        };
    }

    /// <summary>
    /// 从 create_interact 响应里取 <c>data.widget_id</c>。
    /// <c>code≠0</c> / <c>success=false</c> / 缺 id / 空白一律 null
    /// （调用方据此记 Warning 并跳过投票，不拦发布）。
    /// </summary>
    public static string? ReadWidgetId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (root.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var codeValue) && codeValue != 0)
            return null;

        if (root.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
            return null;

        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
            return null;

        if (!data.TryGetProperty("widget_id", out var id)) return null;

        var widgetId = id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.ToString(),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(widgetId) ? null : widgetId;
    }
}
