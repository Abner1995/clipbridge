namespace CreatFlow.NewApp.Services;

/// <summary>文件夹批量的一个成片单元（子文件夹 = 一条）</summary>
public sealed record ComposeFolderUnit(
    string FolderPath,                     // 素材所在文件夹
    string Title,                          // 发布标题 = 文件夹名
    IReadOnlyList<string> MaterialPaths,   // 素材（文件名自然序）
    string? Text,                          // 资料.txt 内容（无则 null；有文案 = 文案配音 + 字幕）
    string? AudioPath,                     // 自备配音（仅「没有 资料.txt」时：第一个非 bgm 音频；这种没有字幕）
    string? BgmPath,                       // 背景音乐（有 资料.txt 时：bgm 前缀优先、否则第一个音频；可选）
    bool AudioAsBgm = false);              // 音频是被「有 资料.txt」规则收编成背景乐的（状态栏提示用）

/// <summary>扫描结果：成片单元 + 跳过的文件夹（附原因 —— 批量入口原样展示给人的话）</summary>
public sealed record ComposeFolderScanResult(
    IReadOnlyList<ComposeFolderUnit> Units,
    IReadOnlyList<(string Folder, string Reason)> Skipped);

/// <summary>
/// 文件夹批量扫描（纯函数，--selftest 直接断言）——
/// 约定（2026-10-10 定案；2026-10-11 修正配音归属 + 补 bgm / 注释行）：
/// 选中的每个文件夹先看下一层子文件夹，「带素材的子文件夹」每个 = 一条；
/// 子文件夹都没素材、而自己带素材 → 文件夹本身 = 一条。
/// **配音来源**（2026-10-11 用户实测反馈修正：音乐文件没按 bgm 命名时被当配音、
/// 静默顶掉 资料.txt、成片没字幕）：
///   · 有 资料.txt → **文案配音 + 字幕**；同文件夹的音频一律当背景音乐（bgm 前缀优先，否则第一个；
///     <see cref="ComposeFolderUnit.AudioAsBgm"/> 记下「被收编」的场合，给状态栏提示用）
///   · 没有 资料.txt → 第一个非 bgm 音频当自备配音（这种情况没有字幕）
///   · 两者都没有（只有 bgm 音频不算）→ 跳过并给出原因
/// 资料.txt 是 UTF-8；行首 # 为注释行（写说明用）。标题 = 文件夹名（画面叠加标题是另一回事，批量不设置）。
/// </summary>
public static class ComposeFolderScanner
{
    public const string TextFileName = "资料.txt";

    public static ComposeFolderScanResult Scan(IReadOnlyList<string> folders)
    {
        var units = new List<ComposeFolderUnit>();
        var skipped = new List<(string Folder, string Reason)>();

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                skipped.Add((folder, "文件夹不存在"));
                continue;
            }
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(folder); }
            catch (Exception ex)
            {
                skipped.Add((folder, "无法读取：" + ex.Message));
                continue;
            }

            var subsWithMedia = subdirs.Where(d => ListMedia(d).Count > 0)
                .OrderBy(d => Path.GetFileName(d) ?? "", NaturalFileNameComparer.Instance).ToList();
            if (subsWithMedia.Count > 0)
            {
                foreach (var sub in subsWithMedia) AddUnit(sub, units, skipped);
            }
            else
            {
                AddUnit(folder, units, skipped);
            }
        }
        return new(units, skipped);
    }

    private static void AddUnit(string folder, List<ComposeFolderUnit> units, List<(string, string)> skipped)
    {
        var media = ListMedia(folder);
        if (media.Count == 0)
        {
            skipped.Add((folder, "没有素材（图片/视频）"));
            return;
        }

        string? text = null;
        var txtPath = Path.Combine(folder, TextFileName);
        if (File.Exists(txtPath))
        {
            string raw;
            try { raw = File.ReadAllText(txtPath); }
            catch (Exception ex)
            {
                skipped.Add((folder, $"资料.txt 读取失败：{ex.Message}"));
                return;
            }
            text = StripComments(raw);
            if (text is not null && text.Contains('�'))
            {
                skipped.Add((folder, "资料.txt 不是 UTF-8 编码（读出乱码）—— 请另存为 UTF-8"));
                return;
            }
        }

        // 配音归属（2026-10-11 修正，用户实测：音乐文件被当配音 → 资料.txt 被顶掉 → 没字幕）：
        //   有 资料.txt → 文案配音 + 字幕；音频一律收编成背景乐（bgm 前缀优先，否则自然序第一个）
        //   没有 资料.txt → 第一个非 bgm 音频当自备配音（没有字幕）
        var audios = ListAudio(folder);
        var bgm = audios.FirstOrDefault(IsBgmFile);
        var firstOther = audios.FirstOrDefault(a => !IsBgmFile(a));
        string? audio = null;
        var audioAsBgm = false;
        if (text is not null)
        {
            if (bgm is null && firstOther is not null)
            {
                bgm = firstOther;
                audioAsBgm = true;   // 被「有文案」规则收编成背景乐的（状态栏提示用）
            }
        }
        else
        {
            audio = firstOther;
        }
        if (text is null && audio is null)
        {
            skipped.Add((folder, "缺配音：放 资料.txt（文案）或一个音频文件（mp3/wav/m4a…）"));
            return;
        }

        var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        units.Add(new ComposeFolderUnit(trimmed, Path.GetFileName(trimmed), media, text, audio, bgm, audioAsBgm));
    }

    /// <summary>bgm 前缀（忽略大小写）= 背景音乐（命名约定与模板 说明.txt / 队列页提示一致）</summary>
    private static bool IsBgmFile(string path) =>
        Path.GetFileName(path).StartsWith("bgm", StringComparison.OrdinalIgnoreCase);

    /// <summary>资料.txt 正文：丢掉行首 # 的注释行与空白行，其余原样保留（换行本身是断句信号）</summary>
    private static string? StripComments(string raw)
    {
        var text = string.Join('\n', raw.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Where(l => l.TrimStart().Length > 0 && l.TrimStart()[0] != '#'));
        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    private static List<string> ListMedia(string dir) =>
        ListFiles(dir, MediaFiles.IsMedia)
            .OrderBy(f => Path.GetFileName(f) ?? "", NaturalFileNameComparer.Instance).ToList();

    private static List<string> ListAudio(string dir) =>
        ListFiles(dir, MediaFiles.IsAudio)
            .OrderBy(f => Path.GetFileName(f) ?? "", NaturalFileNameComparer.Instance).ToList();

    private static List<string> ListFiles(string dir, Func<string, bool> match)
    {
        try { return [.. Directory.GetFiles(dir).Where(match)]; }
        catch { return []; }
    }
}

/// <summary>文件名自然序（"2.jpg" 排在 "10.jpg" 前 —— 素材顺序就是轮播顺序，纯字典序会把 10 排到 2 前面）</summary>
public sealed class NaturalFileNameComparer : IComparer<string>
{
    public static NaturalFileNameComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        x ??= "";
        y ??= "";
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                var sj = j;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var nx = x[si..i].TrimStart('0');
                var ny = y[sj..j].TrimStart('0');
                if (nx.Length != ny.Length) return nx.Length - ny.Length;   // 位数多的数值大
                var cmp = string.CompareOrdinal(nx, ny);
                if (cmp != 0) return cmp;
                if (i - si != j - sj) return (i - si) - (j - sj);           // 数值一样："01" 在 "1" 前
            }
            else
            {
                var cx = char.ToLowerInvariant(x[i]);
                var cy = char.ToLowerInvariant(y[j]);
                if (cx != cy) return cx.CompareTo(cy);
                i++;
                j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
