using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CreatFlow.Platforms.Download;

/// <summary>下载路径配置（根目录 + 相对路径模板）。模板**只能是相对路径**，盘符/UNC 由根目录决定。</summary>
public sealed record OutputPathOptions
{
    public required string RootDirectory { get; init; }

    /// <summary>默认按平台/作者分层；文件名固定带 [id] —— 同一作品重复下载天然幂等，不会出现 (1)(2)</summary>
    public string Template { get; init; } = OutputPathResolver.DefaultTemplate;

    /// <summary>根目录缺失时的兜底（%USERPROFILE%\Videos\CreatFlow，取不到则文档目录）</summary>
    public static string DefaultRoot()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (!string.IsNullOrWhiteSpace(videos)) return Path.Combine(videos, "CreatFlow");
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(string.IsNullOrWhiteSpace(docs) ? "." : docs, "CreatFlow");
    }
}

/// <summary>
/// 路径模板渲染与 Windows 文件名清洗 —— **纯函数**，--selftest 直接断言。
///
/// 两条硬约束（docs/视频下载引擎.md 第 5 节）：
///  · 模板里的任何一段都可能来自平台数据（标题/作者），**逐段清洗**后再拼；
///  · 渲染结果必须仍在根目录之下 —— 标题里出现 `..` 或绝对路径时不能越界。
/// </summary>
public static class OutputPathResolver
{
    public const string DefaultTemplate = @"{platform}\{author}\{title} [{id}].{ext}";

    /// <summary>单段最大长度（按 Rune 计）。NTFS 单段上限 255，留余量给整条路径不超 260。</summary>
    private const int MaxSegmentRunes = 120;

    /// <summary>
    /// 标题/作者这类**平台长文本**在渲染阶段先裁一刀，留出 `[{id}].{ext}` 的位置
    /// （19 位抖音 ID + 括号 + `.mp4` ≈ 26 码点 → 90 + 26 刚好卡在 120 以内）。
    ///
    /// 不裁的话 120 码点的段上限会从右往左把 **id 和扩展名一起砍掉** ——
    /// 抖音的标题动辄上百字，2026-10-23 用户实测落盘成了**没有后缀**的文件（「下载的格式不对」），
    /// 同时 `[id]` 也没了（那是重下幂等/去重的键）。
    /// </summary>
    private const int MaxTokenRunes = 90;

    /// <summary>像扩展名的最长尾巴（`.mp4` / `.webm` 都在这之内）</summary>
    private const int MaxExtensionChars = 10;

    private static readonly Regex TokenPattern = new(@"\{(\w+)(?::([^}]+))?\}", RegexOptions.Compiled);

    private static readonly Regex ReservedNamePattern =
        new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>模板变量值（未知变量原样保留 —— 让用户看见拼写错误，不静默吞掉）</summary>
    public static string Render(
        string template,
        VideoInfo info,
        DateTime now,
        string extension,
        string? resolution = null)
    {
        var ext = NormalizeExtension(extension);
        return TokenPattern.Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            var arg = m.Groups[2].Success ? m.Groups[2].Value : null;
            return name switch
            {
                "platform" => string.IsNullOrWhiteSpace(info.Platform) ? "unknown" : info.Platform,
                // title/author 先按 token 上限裁 —— 见 MaxTokenRunes 注释
                "author" => ClipToken(string.IsNullOrWhiteSpace(info.Author) ? "未知作者" : info.Author!),
                "title" => ClipToken(string.IsNullOrWhiteSpace(info.Title) ? info.VideoId : info.Title),
                "id" => info.VideoId,
                "date" => now.ToString(string.IsNullOrWhiteSpace(arg) ? "yyyy-MM-dd" : arg,
                    CultureInfo.InvariantCulture),
                "ext" => ext,
                "resolution" => string.IsNullOrWhiteSpace(resolution) ? "" : resolution!,
                _ => m.Value,
            };
        });
    }

    /// <summary>
    /// 渲染 + 逐段清洗 + 越界校验，返回最终绝对路径。
    /// <paramref name="template"/> 里不含 `{ext}` 时自动补扩展名（防用户写出没有后缀的文件）。
    /// </summary>
    public static string Resolve(
        string rootDirectory,
        string template,
        VideoInfo info,
        DateTime now,
        string extension,
        string? resolution = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new DownloadException(DownloadErrorKind.AccessDenied, "下载目录未设置");

        var root = Path.GetFullPath(rootDirectory.Trim());
        var effectiveTemplate = ResolveTemplateWithExtension(template, extension);
        var rendered = Render(effectiveTemplate, info, now, extension, resolution);

        var segments = SplitSegments(rendered);
        if (segments.Count == 0)
            throw new DownloadException(DownloadErrorKind.AccessDenied, "路径模板渲染结果为空");

        var relative = string.Join(Path.DirectorySeparatorChar, segments);
        var full = Path.GetFullPath(Path.Combine(root, relative));

        if (!IsUnder(root, full))
            throw new DownloadException(DownloadErrorKind.AccessDenied,
                "路径模板渲染到了下载目录之外（标题可能包含 .. 或盘符），已拒绝");

        return full;
    }

    /// <summary>UI 预览：返回相对落点（不含根目录），渲染失败时给出错误文案而不是抛。</summary>
    public static string PreviewRelative(
        string template,
        VideoInfo info,
        DateTime now,
        string extension,
        string? resolution = null)
    {
        try
        {
            var effectiveTemplate = ResolveTemplateWithExtension(template, extension);
            var rendered = Render(effectiveTemplate, info, now, extension, resolution);
            var segments = SplitSegments(rendered);
            return segments.Count == 0 ? "(空路径)" : string.Join('\\', segments);
        }
        catch (Exception ex) when (ex is DownloadException or ArgumentException or PathTooLongException)
        {
            return $"(模板错误: {ex.Message})";
        }
    }

    /// <summary>
    /// Windows 单段清洗（纯函数）：
    /// 非法字符/控制字符 → `_`；段尾 `.` 与空格裁掉；保留名加前缀；`.`/`..` 置为 `_`；
    /// 空段 → `_`；按 Rune 截断（不能按 char，会切碎 emoji / 代理对）。
    /// </summary>
    public static string SanitizeSegment(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "_";

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch < 0x20 || ch == 0x7F || Array.IndexOf(InvalidChars, ch) >= 0)
                sb.Append('_');
            else
                sb.Append(ch);
        }

        var s = sb.ToString().Trim();
        s = s.TrimEnd('.', ' ');
        // 仅由点组成（"." / ".." / "..."）会变成相对路径语义 —— 一律替换
        if (s.Length > 0 && s.All(c => c == '.')) s = "_";
        if (s.Length == 0) s = "_";

        s = TruncateKeepingExtension(s, MaxSegmentRunes).TrimEnd('.', ' ');
        if (s.Length == 0) s = "_";

        // 保留名：带扩展名也算（CON.mp4 同样非法）—— 取第一个点之前的部分判断
        var dot = s.IndexOf('.');
        var stem = dot >= 0 ? s[..dot] : s;
        if (ReservedNamePattern.IsMatch(stem)) s = "_" + s;

        return s;
    }

    /// <summary>渲染结果按 `\` 与 `/` 拆段（模板里的分隔符两种都认）。</summary>
    public static List<string> SplitSegments(string rendered) =>
        [.. rendered
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeSegment)
            .Where(s => s.Length > 0)];

    public static string NormalizeExtension(string? extension)
    {
        var ext = (extension ?? "mp4").Trim().TrimStart('.');
        if (ext.Length == 0) return "mp4";
        // 扩展名同样可能来自平台（极少数情况），按段清洗但不能再含点
        ext = new string(ext.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());
        return ext.Length == 0 ? "mp4" : ext;
    }

    private static string ResolveTemplateWithExtension(string template, string extension)
    {
        var t = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template.Trim();
        return t.Contains("{ext}", StringComparison.OrdinalIgnoreCase)
            ? t
            : t + "." + NormalizeExtension(extension);
    }

    /// <summary>路径必须在根目录之下（含根目录本身）—— 防目录穿越。</summary>
    public static bool IsUnder(string rootDirectory, string candidate)
    {
        var root = Path.GetFullPath(rootDirectory);
        var full = Path.GetFullPath(candidate);
        var sep = Path.DirectorySeparatorChar;
        if (!root.EndsWith(sep)) root += sep;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(root, cmp);
    }

    /// <summary>平台文本（标题/作者）按 token 上限裁；未超长时原样返回。</summary>
    private static string ClipToken(string value) => TruncateByRunes(value, MaxTokenRunes);

    /// <summary>
    /// 超长时截断，但**保住末尾的扩展名**。
    /// 兜底用 —— 正常情况下 <see cref="Render"/> 已经把 title/author 裁到留得出
    /// `[{id}].{ext}`，走不到这里；但自定义模板可能把段写得更长。
    /// </summary>
    private static string TruncateKeepingExtension(string s, int maxRunes)
    {
        if (s.EnumerateRunes().Count() <= maxRunes) return s;

        var dot = s.LastIndexOf('.');
        if (dot > 0 && dot < s.Length - 1)
        {
            var tail = s[(dot + 1)..];
            if (tail.Length <= MaxExtensionChars && tail.All(char.IsLetterOrDigit))
            {
                var ext = s[dot..];
                var budget = maxRunes - ext.EnumerateRunes().Count();
                if (budget > 1)
                {
                    var stem = TruncateByRunes(s[..dot], budget).TrimEnd('.', ' ');
                    return (stem.Length == 0 ? "_" : stem) + ext;
                }
            }
        }

        return TruncateByRunes(s, maxRunes);
    }

    /// <summary>按 Unicode 码点截断，绝不切碎代理对 / emoji。</summary>
    public static string TruncateByRunes(string value, int maxRunes)
    {
        if (string.IsNullOrEmpty(value) || maxRunes <= 0) return "";
        var count = 0;
        var sb = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (count++ >= maxRunes) break;
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }
}
