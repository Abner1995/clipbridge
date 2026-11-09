namespace CreatFlow.NewApp.Services;

/// <summary>
/// 展开后的一个候选单元 —— **还没变成 PublishDraft，没碰任何用户状态**。
/// 标题与封面已经按回退链算好，<see cref="Params"/> 是 params.txt 的完整解析结果。
/// </summary>
internal sealed record ImportCandidate(
    string VideoPath,
    string? SourceFolder,
    string Title,
    string? Description,
    string? CoverPath,
    UnitParams Params,
    bool MultipleVideos)
{
    public string FileName => Path.GetFileName(VideoPath);
}

/// <summary>
/// 一次展开的完整结果。<see cref="Candidates"/> 是**全部**候选 —— 一次能导几条由
/// 待导入队列决定（见 <see cref="PublishImportPlanner.TakeBatch"/>），展开阶段不做截断：
/// 截断放在这里的话，余量会在进队列之前就被丢掉（这正是第一版的 bug）。
/// </summary>
internal sealed record ImportPlan(
    IReadOnlyList<ImportCandidate> Candidates,
    int SkippedFolders,
    int MultipleVideoWarnings,
    int Duplicates,
    IReadOnlyList<string> Notices);

/// <summary>
/// 导入展开器（设计文档 §5.1）—— 把「用户选中的一堆路径」展开成候选单元列表。
///
/// 为什么单独成类且是**纯函数**（只读文件系统）：真正的导入会写用户的
/// <c>publish-drafts.json</c>，<c>--selftest</c> 不能为了验一条规则去动用户的草稿
/// —— 与 <c>PublishParamsFile</c> 同一条理由。副作用全部收在 VM 的落地那一步。
///
/// 标题只有两级：<c>params.txt</c> 的「标题:」→ 目录名；简介只认 <c>params.txt</c>。
/// 曾经兼容的 <c>title.txt</c> / <c>desc.txt</c> 已于 2026-09-21 移除 —— 两个固定文件各只能
/// 装一个字段，params.txt 覆盖面更大；留着旧写法只会让人以为「写了 title.txt 就有用」。
/// 封面仍按 <c>cover.jpg</c> 约定找（jpg/png/webp），找不到就抽首帧。
/// </summary>
internal static class PublishImportPlanner
{
    /// <summary>一次最多导入多少条（用户定的：对齐每批 10 条的发布节奏）</summary>
    internal const int ImportLimit = 10;

    internal static readonly string[] VideoExtensions =
        [".mp4", ".mov", ".mkv", ".avi", ".flv", ".wmv", ".m4v", ".webm"];

    internal static bool IsVideo(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// 展开用户选中的路径（文件夹与视频文件可以混着给），返回**全部**候选。
    /// <paramref name="excludeVideoPaths"/> = 已在工作台/待导入里的视频，用于去重。
    /// </summary>
    internal static ImportPlan Expand(
        IReadOnlyList<string> picked,
        IReadOnlyCollection<string>? excludeVideoPaths = null)
    {
        var scan = new Scan();
        foreach (var p in excludeVideoPaths ?? []) scan.Seen.Add(DedupeKey(p));

        foreach (var item in picked) ScanOne(item, scan);

        return new ImportPlan(scan.Found, scan.Skipped, scan.Multiple, scan.Duplicates, scan.Notices);
    }

    /// <summary>
    /// 从队列头部取一批（最多 <paramref name="limit"/> 条）并**把它从队列里移走**。
    ///
    /// 这是「每次最多导 N 条」的唯一落点，且是个纯函数 —— 抽出来才好在
    /// <c>--selftest</c> 里验（真正的导入会写用户的草稿文件，自检不能碰）。
    /// </summary>
    internal static List<T> TakeBatch<T>(List<T> queue, int limit = ImportLimit)
    {
        var count = Math.Min(limit, queue.Count);
        if (count <= 0) return [];

        var batch = queue.GetRange(0, count);
        queue.RemoveRange(0, count);
        return batch;
    }

    /// <summary>去重键：完整路径 + 大小写不敏感（Windows 文件系统语义）</summary>
    internal static string DedupeKey(string videoPath)
    {
        try { return Path.GetFullPath(videoPath); }
        catch { return videoPath; }
    }

    // ═══ 展开 ═══

    private static void ScanOne(string path, Scan scan)
    {
        try
        {
            if (Directory.Exists(path)) { ScanFolder(path, scan); return; }

            if (File.Exists(path))
            {
                if (IsVideo(path)) Keep(Loose(path), scan);
                else { scan.Skipped++; scan.Notices.Add($"{Path.GetFileName(path)} 不是视频文件"); }
                return;
            }

            scan.Skipped++;
            scan.Notices.Add($"找不到：{path}");
        }
        catch (Exception ex)
        {
            scan.Skipped++;
            scan.Notices.Add($"读不了：{Path.GetFileName(path)}");
            Log.Warning(ex, "[Publish] 展开失败 {Path}", path);
        }
    }

    /// <summary>
    /// 展开一个文件夹。规则（深度只到 1 层 —— 误选 <c>C:\</c> 时不能把整个盘扫一遍）：
    ///
    /// 1. 目录里有 <c>params.txt</c> 且里面有视频 → **它自己就是一条发布单元**，不再往下展开。
    ///    这是「我明确声明过这是一个单元」的出口（单元里恰好放了个素材子目录时用得上）。
    /// 2. 目录里有「装得下视频的子目录」→ 它们是发布单元，**散装视频不重复计**（见下）。
    /// 3. 否则 → 根下的散装视频各算一条（平铺素材目录）。
    /// </summary>
    private static void ScanFolder(string dir, Scan scan)
    {
        var declared = PublishParamsFile.TryRead(dir);
        var videos = FindVideos(dir);

        if (declared is not null && videos.Count > 0)
        {
            Keep(Unit(dir, videos, declared, scan), scan);
            return;
        }

        var units = 0;
        foreach (var sub in Sorted(Directory.GetDirectories(dir)))
        {
            var subVideos = FindVideos(sub);
            if (subVideos.Count == 0) { scan.Skipped++; continue; }
            // 子目录自己的 params.txt（没有就空 —— 全靠目录名回退）
            Keep(Unit(sub, subVideos, PublishParamsFile.TryRead(sub) ?? UnitParams.Empty, scan), scan);
            units++;
        }

        if (units > 0)
        {
            // 「子文件夹」与「散装视频」同时存在时**只认子文件夹**，但必须说出来。
            //
            // 为什么不是两样都收：`video/video/` 就是这种形状 —— 102 个单元文件夹 +
            // 102 个散装原件，两样都收会让每条视频进两次（路径不同，按路径去重挡不住）。
            // 只认子文件夹而不提示同样是错的：那才是 2026-09-19「导入 0 条」的老毛病。
            if (videos.Count > 0)
            {
                scan.Notices.Add($"{Path.GetFileName(dir)}\\ 里还有 {videos.Count} 个散装视频没导入" +
                                 "（这个目录有子文件夹，已按「一个子文件夹 = 一条」导入；" +
                                 "要导散装视频，直接选它们或放进单独的目录）");
            }
            return;
        }

        foreach (var file in videos) { Keep(Loose(file), scan); units++; }

        if (units == 0)
        {
            scan.Skipped++;
            scan.Notices.Add($"{Path.GetFileName(dir)}\\ 里没有视频");
        }
    }

    private static void Keep(ImportCandidate candidate, Scan scan)
    {
        if (!scan.Seen.Add(DedupeKey(candidate.VideoPath))) { scan.Duplicates++; return; }
        scan.Found.Add(candidate);
    }

    /// <summary>发布单元：标题 = params.txt → 目录名；封面 = cover.* （null = 抽首帧）</summary>
    private static ImportCandidate Unit(string dir, List<string> videos, UnitParams declared, Scan scan)
    {
        // params.txt 里没看懂的行**必须说出来**：用户写「发布时间: 2026-09-25 20:00  # 说明」
        // 这种行解析失败时，界面上只表现为「没生效」，跟没写一模一样。
        // （2026-09-19：Problems 一直在收，却没有任何地方读它 —— 等于白记）
        foreach (var p in declared.Problems)
            scan.Notices.Add($"{Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))}\\{p.Text}");

        if (videos.Count > 1)
        {
            scan.Multiple++;
            scan.Notices.Add($"{Path.GetFileName(dir)}\\ 里有 {videos.Count} 个视频，只用了第一个");
            Log.Warning("[Publish] 目录 {Dir} 有 {Count} 个视频，取第一个：{Video}",
                Path.GetFileName(dir), videos.Count, Path.GetFileName(videos[0]));
        }

        return new ImportCandidate(
            videos[0],
            dir,
            declared.Title ?? Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)),
            declared.Description,
            FindCover(dir),
            declared,
            videos.Count > 1);
    }

    /// <summary>散装视频：文件名当标题，没有 desc/cover（进编辑器手填）</summary>
    private static ImportCandidate Loose(string file) =>
        new(file, null, Path.GetFileNameWithoutExtension(file), null, null, UnitParams.Empty, false);

    /// <summary>
    /// 已知这一条就是发布单元、且知道确切视频路径时的单条入口（合成完自动发布用）——
    /// 不走目录展开：单元目录里恰好还有别的视频时，<see cref="Expand"/> 会按自然序选第一个，
    /// 而合成产物是哪一个是确定的（打从 <c>ComposeOutcome.OutputPath</c> 就有）。
    /// 回退链与 <see cref="Unit"/> 同一口径：标题 = params.txt → 目录名；封面 = cover.*（null = 抽首帧）。
    /// </summary>
    internal static ImportCandidate UnitFor(string dir, string videoPath)
    {
        var declared = PublishParamsFile.TryRead(dir) ?? UnitParams.Empty;
        // 展开路径靠 scan.Notices 把没看懂的行带给用户；自动发布没有提示条，落日志
        foreach (var p in declared.Problems)
            Log.Warning("[Publish] 发布单元 {Dir} 的 {Text}",
                Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)), p.Text);

        return new ImportCandidate(
            videoPath,
            dir,
            declared.Title ?? Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)),
            declared.Description,
            FindCover(dir),
            declared,
            false);
    }

    // ═══ 目录约定 ═══

    private static List<string> FindVideos(string dir)
    {
        try { return [.. Sorted(Directory.GetFiles(dir).Where(IsVideo))]; }
        catch { return []; }
    }

    private static IEnumerable<string> Sorted(IEnumerable<string> paths) =>
        paths.OrderBy(x => x, NaturalOrder);

    /// <summary>
    /// 目录/文件名的**自然序**：数字段按数值比（1, 2, …, 10, 100），不是字典序（1, 10, 100, 2）。
    ///
    /// 为什么较这个真：用户的素材就是按 1..102 命名的（`video/video/`），一次导一批 10 条时
    /// 字典序会给出「1, 10, 11, 12, …, 2」这种肉眼一看就不对的顺序。`make_publish_units.py`
    /// 那边用的是 `key=(len(stem), stem)`，同一个意图 —— 两边必须一致。
    /// </summary>
    private static readonly IComparer<string> NaturalOrder = Comparer<string>.Create(CompareNatural);

    private static int CompareNatural(string? a, string? b)
    {
        if (a is null || b is null) return string.CompareOrdinal(a, b);

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var si = i;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                var sj = j;
                while (j < b.Length && char.IsDigit(b[j])) j++;

                // 前导零不算数，长度不同就是数值不同（"02" == "2"）
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length - nb.Length;
                var cmp = string.CompareOrdinal(na, nb);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (cmp != 0) return cmp;
                i++;
                j++;
            }
        }
        return (a.Length - i) - (b.Length - j);
    }

    private static string? FindCover(string dir)
    {
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp" })
        {
            var file = Path.Combine(dir, "cover" + ext);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    /// <summary>展开过程中的累加器（代替一串 ref 参数）</summary>
    private sealed class Scan
    {
        public List<ImportCandidate> Found { get; } = [];
        public List<string> Notices { get; } = [];
        public HashSet<string> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Skipped;
        public int Multiple;
        public int Duplicates;
    }
}
