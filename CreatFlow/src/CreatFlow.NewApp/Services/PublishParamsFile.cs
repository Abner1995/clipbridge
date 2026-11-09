using System.Globalization;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 发布单元的参数文件 <c>params.txt</c> 解析结果（设计文档 §5.1）。
///
/// **所有字段可空**：null 表示「文件里没写」，由调用方沿回退链取下一个来源
/// （标题：params → 目录名）。解析失败的值同样落 null —— 绝不能
/// 变成空串，否则「标题:」这种空行会把整批卡片变成空标题，预检逐条报错。
/// </summary>
internal sealed record UnitParams
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Topics { get; init; } = [];

    /// <summary>
    /// 结构化话题（<c>话题ID</c> 键）—— 「平台:话题名=平台话题ID」，只对指定平台生效。
    /// 与 <see cref="Topics"/> 并存：<c>话题</c> 给名字（导入后走搜索解析），
    /// <c>话题ID</c> 钉死某平台的话题 ID（脚本批量 / 活动话题），免去联网解析。
    /// </summary>
    public IReadOnlyList<UnitTopicRef> TopicRefs { get; init; } = [];

    /// <summary>
    /// 官方活动（<c>官方活动</c> 键）—— 「平台:活动ID」或「平台:活动名=活动ID」，
    /// 只对指定平台生效。导入后写进该平台的 <see cref="PlatformParams.Activities"/>。
    /// </summary>
    public IReadOnlyList<UnitActivityRef> Activities { get; init; } = [];

    /// <summary>
    /// 位置关键词（可选）。导入后走**联邦解析**：一次搜索 → 各平台各拿自己的 POI
    /// → 写进该平台所有账号（与编辑器里手搜一次等价）。
    /// </summary>
    public string? Location { get; init; }

    /// <summary>
    /// 合集**名字**（可选）。合集是软件里的逻辑概念：一个合集在各平台各有自己的
    /// collection id（抖音 mix_id / 快手 mixId / 小红书 / 视频号 event），映射存在服务端
    /// 「合集」页同步下来的数据里。所以这里写名字、导入时按名字查映射铺到各目标
    /// （与编辑器里点一次合集下拉等价），不是去平台搜。
    /// </summary>
    public string? Collection { get; init; }

    public PublishVisibility? Visibility { get; init; }
    public DeclarationKind? Declaration { get; init; }
    public bool? AllowDownload { get; init; }

    /// <summary>仅小红书：原创声明。落在账号级 <see cref="PlatformParams.IsOriginal"/> 上</summary>
    public bool? IsOriginal { get; init; }

    public bool? IsScheduled { get; init; }

    /// <summary>不变量：<see cref="IsScheduled"/> == false 时必为 null（预检会拦「定时却没选时间」）</summary>
    public DateTime? ScheduledAt { get; init; }

    /// <summary>B站一级分区（<c>tid</c>，默认 21 家装房产）—— <c>B站分区</c> 键，仅对有 B站目标的草稿生效</summary>
    public int? BilibiliTid { get; init; }

    /// <summary>B站二级分区（<c>human_type2</c>，默认 1015 家装房产）—— <c>B站二级分区</c> 键</summary>
    public int? BilibiliHumanType2 { get; init; }

    /// <summary>头条赞赏（<c>头条赞赏</c> 键，仅头条目标）</summary>
    public bool? ToutiaoPraise { get; init; }

    /// <summary>头条模板名（<c>头条模板</c> 键，仅头条目标）</summary>
    public string? ToutiaoTemplate { get; init; }

    /// <summary>头条滤镜名（<c>头条滤镜</c> 键，仅头条目标）</summary>
    public string? ToutiaoFilter { get; init; }

    /// <summary>没看懂的行 —— 不阻断导入，但要让用户看见（§5.1「不能装作全都成功」）</summary>
    public IReadOnlyList<ParamProblem> Problems { get; init; } = [];

    public static readonly UnitParams Empty = new();
}

/// <summary>
/// params.txt 的 <c>话题ID</c> 条目 —— 平台在**解析期**就定死（存英文平台键），
/// 导入时按 target 的平台分发，不会把抖音的 ID 发给快手。
/// </summary>
internal sealed record UnitTopicRef(string PlatformKey, TopicRef Topic);

/// <summary>params.txt 的 <c>官方活动</c> 条目（同上，平台限定）。</summary>
internal sealed record UnitActivityRef(string PlatformKey, ActivityRef Activity);

/// <summary>params.txt 里一行没看懂。<paramref name="Line"/> 从 1 起，0 = 整文件级问题。</summary>
internal sealed record ParamProblem(int Line, string Key, string Reason)
{
    /// <summary>给用户看的一行（导入提示条直接用）</summary>
    public string Text => Line > 0 ? $"{PublishParamsFile.FileName} 第 {Line} 行：{Reason}" : Reason;
}

/// <summary>
/// <c>params.txt</c> 解析器（设计文档 §5.1）—— 键值对、中文键、<c>#</c> 注释。
///
/// 为什么是 txt 而不是 JSON：这个文件是**运营手写**的。键值对不用记引号、逗号、
/// 转义，中文键名直接对着编辑器里的标签写。
///
/// 解析策略：**元数据问题不该让整条导入失败**。
/// 认不出的键、缺冒号的行、非法枚举值一律记一条 <see cref="ParamProblem"/> 后跳过，
/// 其余字段照常生效 —— 将来模板里加新键，旧版本软件也不会炸。
/// </summary>
internal static class PublishParamsFile
{
    public const string FileName = "params.txt";

    /// <summary>纯函数：文本 → 结构。不碰磁盘，<c>--selftest</c> 可以直接喂字符串。</summary>
    internal static UnitParams Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return UnitParams.Empty;

        string? title = null, desc = null, location = null, collection = null;
        var topics = new List<string>();
        var topicRefs = new List<UnitTopicRef>();
        var activities = new List<UnitActivityRef>();
        PublishVisibility? visibility = null;
        DeclarationKind? declaration = null;
        bool? allowDownload = null, isOriginal = null, isScheduled = null;
        DateTime? scheduledAt = null;
        int? bilibiliTid = null, bilibiliHumanType2 = null;
        bool? toutiaoPraise = null;
        string? toutiaoTemplate = null, toutiaoFilter = null;
        var problems = new List<ParamProblem>();

        // 去 BOM（文件可能是记事本另存的 UTF-8 BOM）+ 统一换行
        var lines = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            // 只切**第一个**冒号：值里的 "https://…" 和 "20:00" 不能被切碎。
            // 中文冒号必须认 —— 中文输入法打出来就是这个。
            var sep = line.IndexOfAny([':', '：']);
            if (sep < 0)
            {
                problems.Add(new(lineNo, "", "缺少冒号（格式是「键: 值」），已忽略这一行"));
                continue;
            }

            var key = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim();
            if (key.Length == 0)
            {
                problems.Add(new(lineNo, "", "冒号前面没有键名，已忽略这一行"));
                continue;
            }

            var nk = Norm(key);

            // 尾注释：模板与迁移脚本给的示例行都写成「可见性: 公开   # 公开 / 仅自己 / …」，
            // 用户照着「取消注释即可」把行首的 # 删掉，尾注释就黏在值上 —— 不切掉这一行
            // 整个作废，而界面上只表现为「没生效」，跟没写一模一样。
            // （2026-09-19 实测：发布时间 / 内容声明 / 可见性 / 允许下载 四行全中。）
            // 只对**值里不可能出现 #** 的字段切 —— 标题/简介/话题一律原样收，
            // 切了会把用户写的 `#` 吃掉（简介里现在约定不写 #话题，但「新品 #1 上线」这种还是用户的字）。
            if (!IsProseKey(nk)) value = StripComment(value);

            // 空值一律「按没写处理」：绝不能变成空串往下走（「标题:」会把整批卡片
            // 变成空标题，预检逐条报错）。话题是唯一例外 —— 空话题本来就等于没有。
            if (value.Length == 0 && nk is not ("话题" or "topic" or "topics" or "tag" or "tags"))
            {
                problems.Add(new(lineNo, key, "值是空的，按没写处理"));
                continue;
            }

            switch (nk)
            {
                case "标题" or "title":
                    title = value;
                    break;

                case "简介" or "desc" or "description":
                    desc = value;
                    break;

                case "话题" or "topic" or "topics" or "tag" or "tags":
                    // 追加语义 —— 「一行一个话题」才好用
                    topics.AddRange(SplitTopics(value));
                    break;

                case "话题id" or "topicid" or "topicids" or "topic_id" or "topic_ids":
                    foreach (var entry in SplitEntries(value))
                    {
                        if (!TrySplitPlatform(entry, out var platform, out var rest, out var why))
                        {
                            problems.Add(new(lineNo, key, why));
                            continue;
                        }

                        // 名字在前、ID 在后：`平台:话题名=话题ID`（名字里允许再带 =，按最后一个切）
                        var eq = rest.LastIndexOf('=');
                        if (eq <= 0 || eq == rest.Length - 1)
                        {
                            problems.Add(new(lineNo, key,
                                $"「{entry}」写法不对，要写成「平台:话题名=话题ID」，例如 抖音:家居=1658571727441931"));
                            continue;
                        }

                        var topicName = rest[..eq].Trim();
                        var topicId = rest[(eq + 1)..].Trim();
                        if (topicName.Length == 0 || topicId.Length == 0)
                        {
                            problems.Add(new(lineNo, key, $"「{entry}」缺话题名或话题 ID，已忽略"));
                            continue;
                        }

                        topicRefs.Add(new UnitTopicRef(platform, new TopicRef
                        {
                            Id = topicId,
                            Name = topicName,
                            Source = TopicRefSources.Params,
                        }));
                    }
                    break;

                case "官方活动" or "官方活動" or "活动" or "activity" or "activities":
                    foreach (var entry in SplitEntries(value))
                    {
                        if (!TrySplitPlatform(entry, out var platform, out var rest, out var why))
                        {
                            problems.Add(new(lineNo, key, why));
                            continue;
                        }

                        // `平台:活动ID` 或 `平台:活动名=活动ID` —— 只有 ID 时名字先按 ID 显示，
                        // 界面拉到活动详情后可再补（导入不联网，绝不能因此失败）
                        var eq = rest.LastIndexOf('=');
                        string activityName, activityId;
                        if (eq >= 0)
                        {
                            activityName = rest[..eq].Trim();
                            activityId = rest[(eq + 1)..].Trim();
                            if (activityName.Length == 0 || activityId.Length == 0)
                            {
                                problems.Add(new(lineNo, key, $"「{entry}」缺活动名或活动 ID，已忽略"));
                                continue;
                            }
                        }
                        else
                        {
                            activityId = rest.Trim();
                            activityName = activityId;
                        }

                        activities.Add(new UnitActivityRef(platform, new ActivityRef
                        {
                            Id = activityId,
                            Name = activityName,
                        }));
                    }
                    break;

                case "位置" or "location" or "poi" or "地点":
                    location = value;
                    break;

                case "合集" or "collection" or "合集名":
                    collection = value;
                    break;

                case "可见性" or "visibility":
                    if (TryWord(value, VisibilityWords, out var v)) visibility = v;
                    else problems.Add(new(lineNo, key, Unknown(value, VisibilityWords.Keys)));
                    break;

                case "内容声明" or "声明" or "declaration" or "decl":
                    if (TryWord(value, DeclarationWords, out var d)) declaration = d;
                    else problems.Add(new(lineNo, key, Unknown(value, DeclarationWords.Keys)));
                    break;

                case "允许下载" or "download" or "allowdownload":
                    if (TryBool(value, out var dl)) allowDownload = dl;
                    else problems.Add(new(lineNo, key, "认不出「是 / 否」，已忽略（默认允许下载）"));
                    break;

                case "原创" or "original" or "isoriginal":
                    if (TryBool(value, out var og)) isOriginal = og;
                    else problems.Add(new(lineNo, key, "认不出「是 / 否」，已忽略"));
                    break;

                // B站分区（阶段 4）：投稿接口要 tid（一级）+ human_type2（二级）。
                // 产品默认 21 / 1015（家装房产）；写错数字平台会拒，这里只收正整数。
                case "b站分区" or "bilibili分区" or "bilibilitid" or "bilibili_tid" or "tid":
                    if (int.TryParse(value, out var tid) && tid > 0) bilibiliTid = tid;
                    else problems.Add(new(lineNo, key, "分区 ID 应为正整数，已忽略（默认 21 家装房产）"));
                    break;

                case "b站二级分区" or "bilibili二级分区" or "bilihuman_type2"
                    or "bilibilihuman_type2" or "human_type2":
                    if (int.TryParse(value, out var type2) && type2 > 0) bilibiliHumanType2 = type2;
                    else problems.Add(new(lineNo, key, "二级分区 ID 应为正整数，已忽略（默认 1015 家装房产）"));
                    break;

                // 头条长尾（阶段 5.6）：赞赏 / 模板 / 滤镜 —— 只对头条目标生效
                case "头条赞赏" or "toutiao赞赏" or "toutiaopraise" or "toutiao_praise":
                    if (TryBool(value, out var ttPraise)) toutiaoPraise = ttPraise;
                    else problems.Add(new(lineNo, key, "认不出「是 / 否」，已忽略（默认不开启赞赏）"));
                    break;

                case "头条模板" or "toutiao模板" or "toutiaotemplate" or "toutiao_template":
                    toutiaoTemplate = value;
                    break;

                case "头条滤镜" or "toutiao滤镜" or "toutiaofilter" or "toutiao_filter":
                    toutiaoFilter = value;
                    break;

                case "发布时间" or "publishat" or "schedule" or "time" or "定时":
                    if (ParseSchedule(value, out var sched, out var at))
                    {
                        isScheduled = sched;
                        scheduledAt = at;
                    }
                    else
                    {
                        problems.Add(new(lineNo, key,
                            "认不出时间，已按「立即发布」处理（写法示例：2026-09-20 20:00）"));
                    }
                    break;

                default:
                    // 不报错：将来模板加新键时，旧版本软件读到不能炸
                    problems.Add(new(lineNo, key, "不是已知的字段，已忽略"));
                    break;
            }
        }

        return new UnitParams
        {
            Title = title,
            Description = desc,
            Location = location,
            Collection = collection,
            Topics = topics,
            TopicRefs = topicRefs,
            Activities = activities,
            Visibility = visibility,
            Declaration = declaration,
            AllowDownload = allowDownload,
            IsOriginal = isOriginal,
            IsScheduled = isScheduled,
            ScheduledAt = scheduledAt,
            BilibiliTid = bilibiliTid,
            BilibiliHumanType2 = bilibiliHumanType2,
            ToutiaoPraise = toutiaoPraise,
            ToutiaoTemplate = toutiaoTemplate,
            ToutiaoFilter = toutiaoFilter,
            Problems = problems,
        };
    }

    /// <summary>
    /// 读目录下的 <c>params.txt</c>。返回 null = **没有这个文件**（不是解析失败）；
    /// 文件存在但空 → <see cref="UnitParams.Empty"/>（非 null）—— 目录里有这个文件
    /// 本身就是「我声明过这是一条发布单元」的信号，见 <c>PublishImportPlanner</c>。
    /// </summary>
    internal static UnitParams? TryRead(string dir)
    {
        var path = Path.Combine(dir, FileName);
        try
        {
            if (!File.Exists(path)) return null;
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 读 {File} 失败，按没写处理", path);
            return UnitParams.Empty;
        }
    }

    // ═══ 取值词表 ═══
    //
    // 中文标签从 PublishMapping 取（**唯一来源**，编辑器里的单选项用的也是它），
    // 另外收手写常用的短别名 ——「内容为 AI 生成」太长，运营会写「AI生成」。

    internal static readonly IReadOnlyDictionary<string, PublishVisibility> VisibilityWords =
        BuildWords(VisibilityMapping.VisibilityLabel, new Dictionary<string, PublishVisibility>
        {
            ["public"] = PublishVisibility.Public,
            ["private"] = PublishVisibility.Private,
            ["仅互关好友"] = PublishVisibility.MutualFriends,
            ["互关好友"] = PublishVisibility.MutualFriends,
            ["mutual"] = PublishVisibility.MutualFriends,
            ["只给看"] = PublishVisibility.OnlyUsers,
            ["only"] = PublishVisibility.OnlyUsers,
            ["不给看"] = PublishVisibility.ExcludeUsers,
            ["exclude"] = PublishVisibility.ExcludeUsers,
            ["仅粉丝"] = PublishVisibility.FollowersOnly,
            ["粉丝可见"] = PublishVisibility.FollowersOnly,
            ["followers"] = PublishVisibility.FollowersOnly,
            ["fans"] = PublishVisibility.FollowersOnly,
        });

    internal static readonly IReadOnlyDictionary<string, DeclarationKind> DeclarationWords =
        BuildWords(DeclarationMapping.DeclarationLabel, new Dictionary<string, DeclarationKind>
        {
            ["不声明"] = DeclarationKind.None,
            ["无"] = DeclarationKind.None,
            ["none"] = DeclarationKind.None,
            ["ai"] = DeclarationKind.AiGenerated,
            ["aigenerated"] = DeclarationKind.AiGenerated,
            ["虚构"] = DeclarationKind.Fiction,
            ["fiction"] = DeclarationKind.Fiction,
            ["opinion"] = DeclarationKind.PersonalOpinion,
            ["营销"] = DeclarationKind.Marketing,
            ["营销推广"] = DeclarationKind.Marketing,
            ["marketing"] = DeclarationKind.Marketing,
            ["转载"] = DeclarationKind.Repost,
            ["repost"] = DeclarationKind.Repost,
            ["自行拍摄"] = DeclarationKind.SelfShot,
            ["selfshot"] = DeclarationKind.SelfShot,
        });

    /// <summary>枚举全集的标签 + 别名 → 词表。键统一归一（去空白、小写），查表时同样归一。</summary>
    private static Dictionary<string, T> BuildWords<T>(
        Func<T, string> label, Dictionary<string, T> aliases) where T : struct, Enum
    {
        var words = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in Enum.GetValues<T>()) words[Norm(label(v))] = v;
        foreach (var (key, v) in aliases) words[Norm(key)] = v;
        return words;
    }

    // ═══ 取值解析 ═══

    /// <summary>
    /// 归一：去掉**所有空白**（含中文输入法打出的全角空格）+ 转小写。
    /// 键名用它（英文键大小写不敏感），枚举/布尔的取值也用它 ——
    /// 所以「AI 生成」「ai生成」「AI　生成」都能命中同一条。标题简介不归一，保留原样。
    /// </summary>
    private static string Norm(string s) =>
        string.Concat(s.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();

    private static bool TryWord<T>(string value, IReadOnlyDictionary<string, T> words, out T result)
        => words.TryGetValue(Norm(value), out result!);

    private static string Unknown(string value, IEnumerable<string> known)
        => $"认不出「{value}」，已忽略（可写：{string.Join(" / ", known.Take(5))} …）";

    private static bool TryBool(string value, out bool result)
    {
        switch (Norm(value))
        {
            case "是" or "开" or "true" or "yes" or "y" or "1" or "允许": result = true; return true;
            case "否" or "关" or "false" or "no" or "n" or "0" or "不允许": result = false; return true;
            default: result = false; return false;
        }
    }

    /// <summary>
    /// 正文类字段（标题/简介/话题）—— 值里的 <c>#</c> 是**内容**，一律不许当注释切掉。
    /// 其余字段（可见性/声明/下载/原创/发布时间/位置）的取值里不可能出现 <c>#</c>，
    /// 切掉尾注释是安全的。
    /// </summary>
    private static bool IsProseKey(string nk) =>
        nk is "标题" or "title" or "简介" or "desc" or "description"
            or "话题" or "topic" or "topics" or "tag" or "tags";

    /// <summary>
    /// 切掉值后面的注释：<c>公开   # 公开 / 仅自己 / …</c> → <c>公开</c>。
    /// 没有 <c>#</c> 就原样返回（绝大多数文件里没有）。
    /// </summary>
    private static string StripComment(string value)
    {
        var i = value.IndexOf('#');
        return i < 0 ? value : value[..i].TrimEnd();
    }

    /// <summary>话题拆分：`#夏日 #海边` 与 `夏日, 海边` 等价；`#` 一律去掉。</summary>
    private static IEnumerable<string> SplitTopics(string value) =>
        value.Split([',', '，', '、', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().TrimStart('#').Trim())
            .Where(t => t.Length > 0);

    /// <summary>
    /// 切「话题ID / 官方活动」的多条值：逗号 / 顿号 / 分号分隔（一行一个也行，多行累加）。
    /// 这里**不按空格切** —— 活动名、话题名里可以有空格。
    /// </summary>
    private static IEnumerable<string> SplitEntries(string value) =>
        value.Split([',', '，', '、', ';', '；'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => e.Length > 0);

    /// <summary>
    /// 拆出「平台:内容」前缀。平台**必须写得出来**：猜错平台会把抖音的活动 ID 发给快手，
    /// 那种静默错发比直接报一行警告难查得多。
    /// </summary>
    private static bool TrySplitPlatform(
        string entry, out string platformKey, out string rest, out string error)
    {
        platformKey = "";
        rest = "";
        error = "";

        var text = entry.Trim();
        var sep = text.IndexOfAny([':', '：']);
        if (sep <= 0)
        {
            error = $"「{text}」缺少平台前缀，要写成「平台:…」，例如 抖音:家居=1658571727441931";
            return false;
        }

        var prefix = text[..sep].Trim();
        if (!PlatformWords.TryGetValue(Norm(prefix), out var key))
        {
            error = $"认不出平台「{prefix}」，可写："
                    + string.Join(" / ", PlatformNames.All.Select(PlatformNames.GetDisplayName));
            return false;
        }

        var body = text[(sep + 1)..].Trim();
        if (body.Length == 0)
        {
            error = $"「{text}」平台后面没有内容";
            return false;
        }

        platformKey = key;
        rest = body;
        return true;
    }

    /// <summary>平台前缀词表 —— 英文键 + 中文显示名 + 手写常用别名（查表前统一 <see cref="Norm"/>）。</summary>
    private static readonly IReadOnlyDictionary<string, string> PlatformWords = BuildPlatformWords();

    private static Dictionary<string, string> BuildPlatformWords()
    {
        var words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in PlatformNames.All)
        {
            words[Norm(key)] = key;
            words[Norm(PlatformNames.GetDisplayName(key))] = key;
        }

        // 手写别名（中文显示名已在上面；这里只补常见简写）
        words["b站"] = PlatformNames.Bilibili;
        words["bili"] = PlatformNames.Bilibili;
        words["哔哩哔哩"] = PlatformNames.Bilibili;
        words["哔哩"] = PlatformNames.Bilibili;
        words["今日头条"] = PlatformNames.Toutiao;
        words["头条号"] = PlatformNames.Toutiao;
        words["微信视频号"] = PlatformNames.WechatVideo;
        words["xhs"] = PlatformNames.Xiaohongshu;
        words["dy"] = PlatformNames.Douyin;
        words["ks"] = PlatformNames.Kuaishou;
        return words;
    }

    /// <summary>发布时间：`立即` 或 `2026-09-20 20:00`。返回 false = 认不出（调用方记警告、按没写处理）。</summary>
    private static bool ParseSchedule(string value, out bool isScheduled, out DateTime? at)
    {
        isScheduled = false;
        at = null;

        if (Norm(value) is "立即" or "立刻" or "马上" or "now" or "immediate") return true;

        string[] formats =
            ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy/M/d H:mm", "yyyy/M/d H:mm:ss",
             "yyyy-M-d H:mm", "yyyy-M-d HH:mm"];
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            isScheduled = true;
            at = parsed;
            return true;
        }
        return false;
    }
}
