namespace CreatFlow.Platforms.Douyin;

/// <summary>
/// 抖音自主声明选项 — 静态映射自 user_declaration.json。
///
/// 来源: GET /aweme/v3/user_declaration/suggestion/
///   响应: examples/douyin/user_declaration/user_declaration.json
///
/// 用途: 发布页自主声明 RadioButton 组的数据源。
///   v1 使用静态映射（选项稳定不变），v2 可改为调 API 动态获取。
/// </summary>
public static class DouyinDeclarationOptions
{
    /// <summary>声明选项列表 — UI 渲染 RadioButton 组用</summary>
    public static IReadOnlyList<DouyinDeclarationOption> All { get; } =
    [
        new("aigc",              "内容由AI生成",        "涉及AI技术生成内容，可能与现实存在差异"),
        new("personal_opinion",  "内容为个人观点或见解",  "涉及解读时事政治、社会热点或专业内容时"),
        new("repost",            "内容为转载信息",       "涉及非个人原创的转载内容，需注明来源",
            HasSourceOption: true),
        new("marketing",         "含营销推广信息",       null),
        new("only_fun_new",      "虚构演绎仅供娱乐",     "涉及虚构情节，可能被误解为真实事件"),
    ];

    /// <summary>
    /// 将 choose_value 映射为声明对象。
    /// API 格式:
    ///   {"choose_value":"xxx"}
    ///   {"choose_value":"repost","source":"站外"}
    ///   {"choose_value":"repost","source":"抖音作品","from_item_id":"765..."}
    /// </summary>
    public static string BuildUserDeclareInfo(
        string chooseValue, string? repostSource = null, string? fromItemId = null)
    {
        var obj = new Dictionary<string, string>
        {
            ["choose_value"] = chooseValue,
        };
        if (!string.IsNullOrWhiteSpace(repostSource))
            obj["source"] = repostSource;
        if (!string.IsNullOrWhiteSpace(fromItemId))
            obj["from_item_id"] = fromItemId;
        return JsonSerializer.Serialize(obj);
    }
}

/// <summary>
/// 单个声明选项 — 对应 user_declaration.json options[] 中的一条。
/// </summary>
/// <param name="ChooseValue">API 字段 choose_value</param>
/// <param name="Text">UI 标签</param>
/// <param name="SubText">UI 辅助说明（null = 无副标题）</param>
/// <param name="HasSourceOption">
///   是否有来源子选项（repost 特有）。
///   show_type = "source_option" → 选转载时需额外填写来源。
/// </param>
public sealed record DouyinDeclarationOption(
    string ChooseValue,
    string Text,
    string? SubText,
    bool HasSourceOption = false
);
