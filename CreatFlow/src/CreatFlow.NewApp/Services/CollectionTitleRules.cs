using CreatFlow.Shared;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 合集标题的本地规则（2026-10-22 用户需求：不能太长、不能含符号）。
///
/// 通用上限 20 字；各平台还有自己的更严口径（2026-11-05 抓包实测）：
///   - 快手：UI 上限 12 字；
///   - 今日头条：5~30 字（少于 5 字建不了）；
///   - 抖音 / B站 / 小红书：20 字；视频号：管理页 20 字（发表页内创建 10 字）。
/// 勾选多个平台时取交集：上限 = 各平台上限的最小值，下限 = 各平台下限的最大值。
///
/// 字符只允许**文字 / 数字 / 空格** —— 符号（# · - _ 括号 / emoji 等）一律不收：
/// 平台侧对合集名大多会过滤或直接拒，与其发出去失败，不如在本地就说清楚。
/// 规则收在这一处，表单实时提示与保存校验共用，避免两套口径。
/// </summary>
public static class CollectionTitleRules
{
    /// <summary>通用合集标题最大字数（未特别标注的平台）</summary>
    public const int MaxLength = 20;

    /// <summary>单个平台的标题上限（多平台取最小值）。</summary>
    public static int MaxLengthFor(string platformKey) => platformKey switch
    {
        PlatformNames.Kuaishou => 12,
        PlatformNames.Toutiao => 30,   // 平台 5~30，但通用 20 更严；单勾头条时才放宽到 30
        _ => MaxLength,
    };

    /// <summary>单个平台的标题下限（多平台取最大值）。</summary>
    public static int MinLengthFor(string platformKey) => platformKey switch
    {
        PlatformNames.Toutiao => 5,
        _ => 1,
    };

    /// <summary>勾选平台集合的生效上限；未勾选平台时按通用 20。</summary>
    public static int EffectiveMaxLength(IEnumerable<string>? platformKeys)
    {
        var keys = platformKeys?.Distinct().ToList();
        return keys is { Count: > 0 } ? keys.Min(MaxLengthFor) : MaxLength;
    }

    /// <summary>勾选平台集合的生效下限；未勾选平台时按 1。</summary>
    public static int EffectiveMinLength(IEnumerable<string>? platformKeys)
    {
        var keys = platformKeys?.Distinct().ToList();
        return keys is { Count: > 0 } ? keys.Max(MinLengthFor) : 1;
    }

    /// <summary>是否含不允许的字符（字母/数字之外的可见字符都算符号）</summary>
    public static bool HasInvalidChar(string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        foreach (var c in title)
        {
            if (!char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c)) return true;
        }
        return false;
    }

    /// <summary>首尾去空白、连续空白收成一个空格（校验、计数与保存共用同一份归一化）</summary>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        return string.Join(" ", title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// 校验：null = 合法；否则给用户看的原因（与表单红字同一文案）。
    /// <paramref name="platformKeys"/> 为本次同步勾选的平台；null/空 = 按通用 20 字校验。
    /// </summary>
    public static string? Validate(string? title, IEnumerable<string>? platformKeys = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return "标题不能为空";

        var text = Normalize(title);
        if (HasInvalidChar(text))
            return "合集标题不能用符号，只能使用文字、数字和空格";

        var max = EffectiveMaxLength(platformKeys);
        var min = EffectiveMinLength(platformKeys);
        if (text.Length > max)
            return $"合集标题最多 {max} 个字（当前 {text.Length} 个）";
        if (text.Length < min)
            return $"合集标题至少 {min} 个字（当前 {text.Length} 个）";
        return null;
    }
}
