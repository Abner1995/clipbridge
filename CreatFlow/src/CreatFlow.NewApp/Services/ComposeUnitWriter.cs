using System.Globalization;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 合成产出 → 发布单元落盘（发布页设计文档 §5.1 的目录约定：一个子文件夹 = 一条视频）。
///
/// 合成页把 mp4 直接写进 <c>&lt;输出目录&gt;/&lt;标题&gt;/</c> 后调这里补两个文件：
///   · params.txt —— 标题（+ 可选的发布时间），格式必须能被 <see cref="PublishParamsFile"/>
///     零警告解析（--selftest 钉住；与 <c>make_publish_units.py</c> / PublishUnitTemplate 同一口径）
///   · cover.jpg —— 从成片抽首帧（已有 cover.* 就不动）
///
/// 产物立即可被发布页「导入文件夹」识别；后续「合成完自动进上传队列」也以这份落盘为输入。
/// </summary>
internal static class ComposeUnitWriter
{
    public const string CoverFileName = "cover.jpg";
    private static readonly string[] CoverExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>
    /// 新建 params.txt 的文本：头部说明 + 生效的「标题 / 发布时间」+ 全部可写参数的注释样板
    /// （<see cref="OptionalGuide"/>）—— 2026-10-11 用户反馈：只写标题，用户不知道还有哪些参数可填。
    /// 可选行一律注释掉（不写 = 跟随发布预设 / 默认值）；全文必须能被
    /// <see cref="PublishParamsFile"/> 零警告解析（--selftest 钉住）。
    /// </summary>
    internal static string BuildParamsText(string title, DateTime? scheduledAt = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 发布单元参数");
        sb.AppendLine("# 每行「字段名: 值」；中文冒号或英文冒号都行。");
        sb.AppendLine("# 行首 # 是注释；值后面跟的说明（如「可见性: 公开   # 公开 / 仅自己」）也算注释。");
        sb.AppendLine("# 由「合成视频」页生成 —— 可直接编辑，或交给发布页「导入文件夹」。");
        sb.AppendLine();
        sb.AppendLine($"标题: {OneLine(title)}");
        if (scheduledAt is { } at)
            sb.AppendLine($"发布时间: {FormatTime(at)}");
        sb.AppendLine();
        foreach (var line in OptionalGuide.Split('\n'))
            sb.AppendLine(line);
        return sb.ToString();
    }

    /// <summary>「可选参数」注释样板的定位标记 —— <see cref="WriteAsync"/> 用它判断旧文件要不要补样板</summary>
    private const string GuideMarker = "# ── 可选参数";

    /// <summary>
    /// 全部可写参数的注释样板 —— 口径与 <c>PublishUnitTemplate.ParamsText</c> 一致（改一处要同步另一处）。
    /// 一律注释掉：不写 = 跟随发布预设 / 默认值，去掉行首 # 才生效 —— 不改变导入语义。
    /// </summary>
    internal const string OptionalGuide = """
        # ── 可选参数（想用哪条，把行首的 # 去掉；不写 = 跟随发布预设 / 默认值）──
        # 发布时间: 2026-09-20 20:00    # 不写 = 立即发布
        # 简介: 记录夏天的第一场海
        # 话题: 夏日
        # 话题: 海边        # 一行一个，写多行累加；也可以写 话题: 夏日, 海边, 旅行
        # 话题ID: 抖音:家居=1658571727441931   # 「平台:话题名=平台话题ID」，只对该平台生效；一般不用写
        # 官方活动: 抖音:7702034615116174390   # 「平台:活动ID」或「平台:活动名=活动ID」；会自动带活动话题
        # 位置: 苏州姑苏     # 关键词；导入时四个平台各搜自己库里的 POI，搜不到的会标明
        # 合集: 苏州房产     # 写「合集」页里建好的名字，导入时按名字铺到各账号
        # 可见性: 公开       # 公开 / 仅自己 / 仅互关好友可见 / 只给谁看 / 不给谁看
        # 允许下载: 是       # 是 / 否
        # 内容声明: 不声明   # 不声明 / 内容由 AI 生成 / 虚构演绎 / 内容为个人观点 / 含营销推广 / 内容为转载 / 内容为自行拍摄
        # 原创: 是           # 小红书 / B站 / 头条（小红书视频时长要大于 10 秒）；不写 = 跟随发布预设
        # B站分区: 21        # B站一级分区（默认 21 家装房产）；只对 B站目标生效
        # B站二级分区: 1015  # B站二级分区（默认 1015 家装房产）
        # 头条赞赏: 是       # 仅头条目标；默认否
        # 头条模板: 模板名   # 仅头条目标，一般不用写
        # 头条滤镜: 滤镜名   # 仅头条目标，一般不用写
        # 话题不要写进「简介」：发布时话题会带 # 追加到正文末尾，简介里再写一个对不上的 # 会被抖音拒收
        """;

    /// <summary>
    /// 标题 → 安全的文件夹 / 文件名。去换行、换非法字符、裁长度；空了或只剩点就回退。
    /// </summary>
    internal static string ToSafeName(string? title, string fallback)
    {
        var flat = OneLine(title ?? "");
        foreach (var c in Path.GetInvalidFileNameChars())
            flat = flat.Replace(c, '_');
        flat = flat.Trim().TrimEnd('.');
        if (flat.Length == 0) return fallback;
        // 文件夹名只是给人看的，标题本体在 params.txt 里；60 字足够且避开路径长度限制
        return flat.Length <= 60 ? flat : flat[..60].TrimEnd().TrimEnd('.');
    }

    /// <summary>折成一行（params.txt 是一行一个字段，值里不能有换行；顺带把连续空白收成一个空格）</summary>
    internal static string OneLine(string s) =>
        string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// 就地改「键: 值」那一行；没有就插在**最后一个生效行之后**（追加到文件末尾会掉到注释块下面，
    /// 读起来像「注释说不写在这里，底下却写了一条」—— make_publish_units.py 的 2026-09-19 老坑）。
    /// </summary>
    internal static string UpsertParam(string text, string key, string value)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var s = lines[i].Trim();
            if (s.Length == 0 || s[0] == '#') continue;
            var sep = s.IndexOfAny([':', '：']);
            if (sep <= 0) continue;   // sep 为 0 = 冒号前没键名，不算生效行
            if (s[..sep].Trim() == key)
            {
                lines[i] = $"{key}: {value}";
                return JoinLines(lines);
            }
        }

        var last = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.Length > 0 && t[0] != '#' && t.IndexOfAny([':', '：']) > 0) last = i;
        }
        lines.Insert(last + 1, $"{key}: {value}");
        return JoinLines(lines);
    }

    /// <summary>缺「可选参数」样板时在末尾补上（已有则原样返回）—— 样板全注释，解析与已生效行都不受影响。</summary>
    internal static string EnsureGuide(string text) =>
        text.Contains(GuideMarker, StringComparison.Ordinal)
            ? text
            : text.TrimEnd('\n') + "\n\n" + OptionalGuide + "\n";

    /// <summary>
    /// 往单元目录补 params.txt + cover.jpg（在 mp4 已写好之后调用）。
    /// 返回写下的文件路径列表。
    ///
    /// 三条规矩（前两条与 <c>make_publish_units.py</c> 对齐）：
    ///   1. **已有 params.txt 只改「标题 / 发布时间」两行，绝不重建** —— 用户可能已在里面补了
    ///      简介/话题/位置，重建会把它们洗掉（2026-09-19 那次洗掉 102 条正文的事故）。
    ///   2. **已有 cover.* 就不动** —— 用户换过的封面比抽帧结果值钱。
    ///   3. 已有 params.txt 缺「可选参数」样板（<see cref="GuideMarker"/>）时在**末尾补上** ——
    ///      全是注释行，解析零影响、不碰用户写的内容；早先生成的单元也能看见有哪些参数（2026-10-11 用户反馈）。
    /// 封面抽帧失败不往外抛：发布页导入时没有封面也会自动抽首帧，合成结果不该因此翻车。
    /// </summary>
    internal static async Task<IReadOnlyList<string>> WriteAsync(
        string unitDir, string videoPath, string title,
        DateTime? scheduledAt = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(unitDir);
        var written = new List<string>();

        var paramsPath = Path.Combine(unitDir, PublishParamsFile.FileName);
        var text = File.Exists(paramsPath)
            ? await File.ReadAllTextAsync(paramsPath, ct).ConfigureAwait(false)
            : BuildParamsText(title, scheduledAt);
        text = UpsertParam(text, "标题", OneLine(title));
        if (scheduledAt is { } at)
            text = UpsertParam(text, "发布时间", FormatTime(at));
        text = EnsureGuide(text);   // 旧文件（旧版生成 / 手写）缺参数样板就补上 —— 全注释，不影响任何已生效的值
        await File.WriteAllTextAsync(paramsPath, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
        written.Add(paramsPath);

        if (FindCover(unitDir) is null)
        {
            try
            {
                var coverPath = Path.Combine(unitDir, CoverFileName);
                await FfmpegVideoComposer.ExtractCoverAsync(videoPath, coverPath, ct).ConfigureAwait(false);
                written.Add(coverPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "[Compose] 抽封面失败（不影响导入，发布页会兜底抽帧）{Dir}", unitDir);
            }
        }
        return written;
    }

    /// <summary>目录里已有的封面（cover.jpg / .jpeg / .png / .webp），没有返回 null</summary>
    internal static string? FindCover(string dir)
    {
        foreach (var ext in CoverExtensions)
        {
            var path = Path.Combine(dir, "cover" + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>发布时间文本：与 PublishParamsFile.ParseSchedule 认的格式一致</summary>
    private static string FormatTime(DateTime at) =>
        at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string JoinLines(List<string> lines) =>
        string.Join("\n", lines).TrimEnd('\n') + "\n";
}
