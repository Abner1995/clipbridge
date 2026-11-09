using System.Globalization;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 发布页「长尾字段」的文本 ↔ 模型转换（纯函数，自检钉死）。
///
/// 这些字段（章节 / 活动 ID / 封面帧 / 裁剪尺寸）是平台专有的低频设置，
/// 不值得为每个都做一套复杂的列表编辑器；用一行/多行文本表达，再在这里统一
/// 解析成模型。**解析规则只放在这一处** —— UI 与 params.txt 将来复用同一份，
/// 免得出现「页面上能写、params.txt 里写法不一样」的漂移（话题双写的旧伤）。
/// </summary>
public static class PublishFieldText
{
    // ═══ 封面帧（模型毫秒，界面秒）═══

    /// <summary>毫秒 → 秒的展示文本（空 = 平台默认）</summary>
    public static string FormatMsAsSeconds(long? ms) =>
        ms is { } v ? (v / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) : "";

    /// <summary>秒文本 → 毫秒；空/非法/负数返回 null（= 平台默认）</summary>
    public static long? ParseSecondsToMs(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
               && seconds >= 0 && !double.IsNaN(seconds) && !double.IsInfinity(seconds)
            ? (long)Math.Round(seconds * 1000)
            : null;
    }

    // ═══ 正整数（封面裁剪尺寸 / 合集排序）═══

    /// <summary>空/非法/负数返回 null；0 是合法值（合集排序 0 = 按发布时间顺序）</summary>
    public static int? ParseInt(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v
            : null;
    }

    public static string FormatInt(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    // ═══ ID 列表（官方活动 / @ 用户等）═══

    /// <summary>逗号串（中英文逗号、顿号、分号、空白都算分隔符）→ 去空去重列表</summary>
    public static List<string> ParseIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<string>();
        foreach (var part in text.Split(
                     [',', '，', '、', ';', '；', ' ', '\t', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(part)) rows.Add(part);
        }
        return rows;
    }

    public static string JoinIds(IEnumerable<string> ids) => string.Join(", ", ids);

    // ═══ 章节（每行：mm:ss 标题）═══

    /// <summary>
    /// 解析章节文本：每行 <c>mm:ss 标题</c> / <c>hh:mm:ss 标题</c> / <c>秒数 标题</c>。
    /// 非法行跳过（页面上的提示写明格式）；按时间升序、同一时间点只留第一条。
    /// </summary>
    public static List<PlatformChapter> ParseChapters(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var rows = new List<PlatformChapter>();
        var seen = new HashSet<long>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split([' ', '\t'], 2,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            if (!TryParseTime(parts[0], out var ms)) continue;
            if (!seen.Add(ms)) continue;

            rows.Add(new PlatformChapter(ms, parts[1].Trim()));
        }

        return [.. rows.OrderBy(r => r.StartMs)];
    }

    /// <summary>章节 → 文本（<c>m:ss 标题</c>；≥1 小时用 <c>h:mm:ss</c>）</summary>
    public static string FormatChapters(IEnumerable<PlatformChapter> chapters) =>
        string.Join("\n", chapters.Select(c =>
        {
            var total = Math.Max(0, c.StartMs) / 1000;
            var h = total / 3600;
            var m = total % 3600 / 60;
            var s = total % 60;
            var time = h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
            return $"{time} {c.Title}";
        }));

    /// <summary><c>mm:ss</c> / <c>hh:mm:ss</c> / 纯秒数 → 毫秒</summary>
    internal static bool TryParseTime(string token, out long ms)
    {
        ms = 0;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var parts = token.Split(':');
        if (parts.Length is 1)
        {
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs)
                || secs < 0) return false;
            ms = secs * 1000L;
            return true;
        }

        if (parts.Length is not (2 or 3)) return false;
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out numbers[i]) || numbers[i] < 0)
                return false;
        }

        // 秒位必须 < 60；分位在 hh:mm:ss 里也要 < 60（mm:ss 的分位不限，支持 90:00 这种长视频）
        if (numbers[^1] >= 60) return false;

        long totalSeconds = parts.Length == 2
            ? numbers[0] * 60L + numbers[1]
            : numbers[0] * 3600L + numbers[1] * 60L + numbers[2];
        if (parts.Length == 3 && numbers[1] >= 60) return false;

        ms = totalSeconds * 1000L;
        return true;
    }
}
