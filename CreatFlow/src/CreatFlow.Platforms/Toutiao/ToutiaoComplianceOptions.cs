namespace CreatFlow.Platforms.Toutiao;

/// <summary>
/// 头条「作品声明」—— <c>PublishVideo.ComplianceInfo</c>。
///
/// wire 逐字来自 2026-11-10 交互式抓包
/// （<c>docs/官方发布页截图/头条/capture-2026-11-10T08-45-23.jsonl</c>）：
/// <code>
/// {"IsAIGC":false,"ComplianceType":2,"MaterialInfo":{"MaterialSource":2}}
/// </code>
/// 页面上的 7 个复选**实际是单选组** —— 发布体只能出现一个 <c>ComplianceType</c>。
///
/// 取值（bundle <c>infoSource.source_type</c> 枚举）：
///   1 = 自行拍摄（<c>CreativeInfo{City,CreativeTime(毫秒)}</c>）
///   2 = 取自站外（<c>MaterialInfo{MaterialSource:2}</c>）
///   3 = AI 生成（<c>IsAIGC:true</c>）
///   5 = 个人观点
///   6 = 虚构演绎
///   7 = 投资观点
///   8 = 健康医疗
///   9/10 = 独家精选 / 首发精选（灰度，v1 不暴露）
///
/// 「引用站内」（<c>ComplianceType:2 + MaterialSource:1 + UserID/Position</c>）的
/// <c>Position</c> 键名未抓全，v1 不开放。
/// </summary>
public static class ToutiaoComplianceOptions
{
    /// <summary>「不声明」—— 发布体不出现 <c>ComplianceInfo</c>（与「跟随全局且全局不声明」同义）。</summary>
    public const int NoneValue = 0;

    /// <summary>UI 可选的平台专有声明（不含「跟随全局」与「不声明」）。</summary>
    public static IReadOnlyList<ToutiaoComplianceOption> All { get; } =
    [
        new(3, "AI生成"),
        new(6, "虚构演绎"),
        new(5, "个人观点"),
        new(1, "自行拍摄", NeedsCreativeInfo: true),
        new(2, "取自站外"),
        new(7, "投资观点"),
        new(8, "健康医疗"),
    ];

    /// <summary>是不是一个可下发的 <c>ComplianceType</c>（0 不算 —— 0 = 不传）。</summary>
    public static bool IsKnownType(int value) => value is 1 or 2 or 3 or 5 or 6 or 7 or 8;

    /// <summary>
    /// 组 <c>ComplianceInfo</c> 对象（null = 本条不带声明）。
    /// <paramref name="type"/> = <see cref="NoneValue"/> 或未知值 → null。
    /// <paramref name="city"/> / <paramref name="timeMs"/> 只在自行拍摄（1）时有意义；
    /// 缺哪项就**不写哪项**（不编造空字符串 / 0 时间），预检会提示补全。
    /// </summary>
    public static Dictionary<string, object?>? BuildInfo(int? type, string? city, long? timeMs)
    {
        if (type is not { } t || !IsKnownType(t)) return null;

        var info = new Dictionary<string, object?>
        {
            // 抓包里只有 AI 是 true，其余全部 false（不是「缺省」）
            ["IsAIGC"] = t == 3,
            ["ComplianceType"] = t,
        };

        if (t == 1)
        {
            var creative = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(city)) creative["City"] = city.Trim();
            if (timeMs is not null) creative["CreativeTime"] = timeMs.Value;
            if (creative.Count > 0) info["CreativeInfo"] = creative;
        }
        else if (t == 2)
        {
            // 2 = 取自站外；「引用站内」是 MaterialSource:1（v1 不开放）
            info["MaterialInfo"] = new Dictionary<string, object?> { ["MaterialSource"] = 2 };
        }

        return info;
    }
}

/// <summary>头条作品声明选项。</summary>
/// <param name="Value"><c>ComplianceType</c> 值</param>
/// <param name="Label">UI 展示标签（官方页面文案）</param>
/// <param name="NeedsCreativeInfo">是否要展开「城市 + 时间」二级输入</param>
public sealed record ToutiaoComplianceOption(
    int Value,
    string Label,
    bool NeedsCreativeInfo = false);
