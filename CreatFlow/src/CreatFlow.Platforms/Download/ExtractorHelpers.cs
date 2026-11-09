using System.Net;
using System.Text.RegularExpressions;

namespace CreatFlow.Platforms.Download;

/// <summary>提取器共用的纯工具（正则取值 / 标题清洗 / 稳定 ID）—— 无网络、可单测。</summary>
internal static class ExtractorHelpers
{
    /// <summary>取第一个捕获组；没匹配返回 null。</summary>
    public static string? Match(string input, string pattern, RegexOptions options = RegexOptions.None)
    {
        if (string.IsNullOrEmpty(input)) return null;
        var m = Regex.Match(input, pattern, options);
        return m.Success && m.Groups.Count > 1 ? m.Groups[1].Value : null;
    }

    public static List<string> MatchAll(string input, string pattern)
    {
        if (string.IsNullOrEmpty(input)) return [];
        return [.. Regex.Matches(input, pattern)
            .Select(m => m.Groups.Count > 1 ? m.Groups[1].Value : m.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))];
    }

    /// <summary>HTML 实体解码（标题里的 &amp; / &#x27; 等）。</summary>
    public static string DecodeHtml(string? value) =>
        string.IsNullOrEmpty(value) ? "" : WebUtility.HtmlDecode(value).Trim();

    /// <summary>去掉「 - 小红书」「 - 快手」这类站点后缀。</summary>
    public static string CleanTitle(string? raw, params string[] suffixes)
    {
        var title = DecodeHtml(raw);
        foreach (var suffix in suffixes)
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                title = title[..^suffix.Length].Trim();
                break;
            }
        }
        return title;
    }

    /// <summary>
    /// 无法从 URL 取到平台 ID 时的稳定兜底：SHA256 前 16 位十六进制。
    /// 不能用 string.GetHashCode —— .NET 的字符串哈希每个进程随机化，重启后同一链接会变成另一条任务。
    /// </summary>
    public static string StableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>把 JSON 里常见的 `/`、`\/` 还原（部分平台把 URL 转义后塞在 HTML 里）。</summary>
    public static string UnescapeUrl(string value) =>
        value.Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
             .Replace("\\u002f", "/", StringComparison.OrdinalIgnoreCase)
             .Replace("\\/", "/", StringComparison.Ordinal)
             .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);

    /// <summary>从 JSON 数组字符串里安全取字符串列表（坏 JSON 按空处理）。</summary>
    public static List<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>按扩展名判断视频直链（去掉查询串后看路径）。</summary>
    public static bool LooksLikeVideoUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var path = uri.AbsolutePath.ToLowerInvariant();
        return path.EndsWith(".mp4") || path.EndsWith(".m4v") || path.EndsWith(".mov");
    }
}
