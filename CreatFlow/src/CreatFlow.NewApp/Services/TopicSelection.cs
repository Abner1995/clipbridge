using CreatFlow.Platforms.Topics;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 发布页「话题」块的纯函数（2.6.3）—— 平台候选 → <see cref="TopicRef"/>、芯片展示文案。
///
/// 为什么单独一份：同一套规则同时被三处用到（平台卡搜索/推荐结果、手动输入、params.txt 导入），
/// 各写一遍必然漂移；尤其「讨论量文案」这种小规则，漂移了没人看得出来。
/// 全部无状态、不碰网络，自检直接钉住。
/// </summary>
internal static class TopicSelection
{
    /// <summary>一次最多展示多少个候选 —— 平台偶尔返回几百条，UI 不该为它卡住</summary>
    public const int MaxSuggestions = 50;

    /// <summary>
    /// 规范化用户输入的话题名：去首尾空白、去开头的 #、压成一行。
    /// 空串 = 用户没输（调用方据此提示，不静默丢弃）。
    /// </summary>
    public static string NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var flat = string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.TrimStart('#').Trim();
    }

    /// <summary>平台候选 → 结构化话题引用（<paramref name="source"/> 用 <see cref="TopicRefSources"/>）</summary>
    public static TopicRef ToRef(TopicSuggestion suggestion, string source) => new()
    {
        Id = string.IsNullOrWhiteSpace(suggestion.PlatformId) ? null : suggestion.PlatformId,
        Name = suggestion.Name,
        Type = suggestion.TopicType,
        MissionId = suggestion.MissionId,
        ExtraJson = suggestion.ExtraJson,
        Source = source,
        Metric = MetricText(suggestion),
    };

    /// <summary>手动输入 → 纯文本话题引用（没有平台 ID，发布时按平台降级）</summary>
    public static TopicRef Manual(string name) => new()
    {
        Name = name,
        Source = TopicRefSources.Manual,
    };

    /// <summary>
    /// 芯片/候选的讨论量文案：优先用平台原样文案（小红书「1.7亿浏览」），
    /// 否则把播放/浏览量按中文计数法格式化；都是 0/空 → null（UI 不占位）。
    /// </summary>
    public static string? MetricText(TopicSuggestion suggestion)
    {
        if (!string.IsNullOrWhiteSpace(suggestion.DiscussText)) return suggestion.DiscussText.Trim();
        return FormatCount(suggestion.ViewCount);
    }

    /// <summary>中文计数法：12.3 亿 / 480 万 / 3200；0 或负数 → null</summary>
    public static string? FormatCount(long count)
    {
        return count switch
        {
            <= 0 => null,
            >= 100_000_000 => $"{count / 100_000_000.0:0.#} 亿",
            >= 10_000 => $"{count / 10_000.0:0.#} 万",
            _ => count.ToString(),
        };
    }

    // ═══════════════════════════════════════════════════════════════
    //  官方活动（2.6.4）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 平台活动 → 已选活动快照（<see cref="ActivityRef"/>）。
    /// 活动关联话题在这里一并转成 <see cref="TopicRef"/>（Source = activity），
    /// 选中后由 <see cref="Models.PlatformParams.UpsertActivity"/> 并进目标的话题列表 ——
    /// 「选活动自动带话题」的唯一映射点，UI 与自检共用。
    /// </summary>
    public static ActivityRef ToActivityRef(PlatformActivity activity) => new()
    {
        Id = activity.Id,
        // 活动名缺失时退回 ID 显示：芯片得有东西可看，不能是空白
        Name = string.IsNullOrWhiteSpace(activity.Name) ? activity.Id : activity.Name.Trim(),
        Cover = string.IsNullOrWhiteSpace(activity.CoverUrl) ? null : activity.CoverUrl,
        EndTime = activity.EndTime,
        Reward = string.IsNullOrWhiteSpace(activity.Reward) ? null : activity.Reward.Trim(),
        Topics = [.. Dedupe(activity.Topics)
            .Select(t => ToRef(t, TopicRefSources.Activity))],
    };

    /// <summary>
    /// 活动卡片上的热度文案：参与人数优先（快手 <c>number</c>），其次热度值（抖音
    /// <c>hot_score</c> / B站话题播放量最大值）；都没有 → null（UI 不占位）。
    /// </summary>
    public static string? ActivityMetricText(PlatformActivity activity)
    {
        if (activity.ParticipantCount > 0)
            return FormatCount(activity.ParticipantCount) is { } people ? $"{people}人参与" : null;
        if (activity.HotScore > 0)
            return FormatCount(activity.HotScore) is { } hot ? $"热度 {hot}" : null;
        return null;
    }

    /// <summary>
    /// 活动截止文案：平台没给 → null；已过 → 「已结束」；否则「MM-dd HH:mm 截止」（本地时间）。
    /// <paramref name="now"/> 显式传入 —— 自检要能钉住「过期」边界，不依赖运行时刻。
    /// </summary>
    public static string? ActivityDeadlineText(DateTimeOffset? end, DateTimeOffset now)
    {
        if (end is not { } e) return null;
        return e <= now ? "已结束" : $"{e.LocalDateTime:MM-dd HH:mm} 截止";
    }

    /// <summary>
    /// 候选去重（同名保留第一条；平台偶尔把同一个话题给两遍）。
    /// 带 ID 的优先 —— 同名一条有 ID 一条没 ID 时，结构化那条更有用。
    /// </summary>
    public static IReadOnlyList<TopicSuggestion> Dedupe(IEnumerable<TopicSuggestion> items)
    {
        var result = new List<TopicSuggestion>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var name = NormalizeName(item.Name);
            if (name.Length == 0) continue;

            var normalized = name == item.Name ? item : item with { Name = name };
            if (!index.TryGetValue(name, out var at))
            {
                index[name] = result.Count;
                result.Add(normalized);
                continue;
            }

            var existing = result[at];
            if (!existing.HasPlatformId && normalized.HasPlatformId) result[at] = normalized;
        }
        return result;
    }
}
