using System.Globalization;
using CreatFlow.Platforms.Xiaohongshu;

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
    /// 平台标签（<c>标签</c> 键，可多行/逗号分隔）—— 落到各目标的 <see cref="PlatformParams.Tags"/>。
    /// B站投稿**至少需要 1 个标签**（预检 Error）、头条的 <c>Label</c> 同源；其余平台不读这个字段。
    /// 2026-11-10 新增：没有它，params/自动发布这条路永远发不了 B站（除非预设里配过标签）。
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

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

    /// <summary>原创声明（小红书 / B站 / 头条 / 视频号）。落在账号级
    /// <see cref="PlatformParams.IsOriginal"/> 上；其余平台没有这个字段</summary>
    public bool? IsOriginal { get; init; }

    public bool? IsScheduled { get; init; }

    /// <summary>不变量：<see cref="IsScheduled"/> == false 时必为 null（预检会拦「定时却没选时间」）</summary>
    public DateTime? ScheduledAt { get; init; }

    /// <summary>B站一级分区（<c>tid</c>，默认 21 家装房产）—— <c>B站分区</c> 键，仅对有 B站目标的草稿生效</summary>
    public int? BilibiliTid { get; init; }

    /// <summary>B站二级分区（<c>human_type2</c>，默认 1015 家装房产）—— <c>B站二级分区</c> 键</summary>
    public int? BilibiliHumanType2 { get; init; }

    /// <summary>
    /// B站字幕文件（<c>B站字幕</c> 键，仅 B站目标）：本地 <c>.srt</c>。
    /// **相对路径按发布单元的文件夹解析**（与「抖音横版封面」同口径）。
    /// 执行端上传后落 <c>videos[0].pre_subtitle</c>。
    /// </summary>
    public string? BilibiliSubtitleFile { get; init; }

    /// <summary>B站字幕语言（<c>B站字幕语言</c> 键；zh / en / ja…，默认 zh）</summary>
    public string? BilibiliSubtitleLan { get; init; }

    /// <summary>
    /// B站互动引导卡片（<c>B站互动引导</c> 键，仅 B站目标）：
    /// 是 / 三连+关注 / 仅三连 / 仅关注 → true（对应样式见
    /// <see cref="BilibiliGuideCardFollowType"/>）；否 → false。
    /// </summary>
    public bool? BilibiliGuideCard { get; init; }

    /// <summary>互动引导样式（2 = 三连+关注 / 1 = 仅三连 / 0 = 仅关注）</summary>
    public int? BilibiliGuideCardFollowType { get; init; }

    /// <summary>卡片起始时间（<c>B站引导时间</c> 键，秒）</summary>
    public int? BilibiliGuideCardStartSeconds { get; init; }

    // ── 卡片 v2：关联视频 / 投票弹幕（2026-11-15 B5.2，wire §4）──

    /// <summary>关联视频目标 BVID（<c>B站关联视频</c> 键；非空 = 建 type=2 卡片）</summary>
    public string? BilibiliRelatedVideo { get; init; }

    /// <summary>关联视频描述（<c>B站关联视频描述</c> 键 → create 体的 <c>msg</c>）</summary>
    public string? BilibiliRelatedVideoMsg { get; init; }

    /// <summary>关联视频卡片起始时间（<c>B站关联视频时间</c> 键，秒）</summary>
    public int? BilibiliRelatedVideoStartSeconds { get; init; }

    /// <summary>投票问题（<c>B站投票</c> 键；非空 = 建 type=9 卡片）</summary>
    public string? BilibiliVoteQuestion { get; init; }

    /// <summary>投票选项（<c>B站投票选项</c> 键；分号 / 竖线分隔，2~4 个）</summary>
    public List<string>? BilibiliVoteOptions { get; init; }

    /// <summary>投票卡片起始时间（<c>B站投票时间</c> 键，秒）</summary>
    public int? BilibiliVoteStartSeconds { get; init; }

    /// <summary>投票持续时间（<c>B站投票时长</c> 键，秒；默认 7）</summary>
    public int? BilibiliVoteDurationSeconds { get; init; }

    /// <summary>头条赞赏（<c>头条赞赏</c> 键，仅头条目标）</summary>
    public bool? ToutiaoPraise { get; init; }

    /// <summary>头条模板名（<c>头条模板</c> 键，仅头条目标）</summary>
    public string? ToutiaoTemplate { get; init; }

    /// <summary>头条滤镜名（<c>头条滤镜</c> 键，仅头条目标）</summary>
    public string? ToutiaoFilter { get; init; }

    /// <summary>
    /// 头条视频生成图文（<c>头条视频生成图文</c> 键，仅头条目标）：
    /// 0 = 不生成；1 = 与视频同时发布；2 = 仅保存草稿。
    /// </summary>
    public int? ToutiaoVideoTransArticle { get; init; }

    /// <summary>
    /// 抖音横版 4:3 封面（<c>抖音横版封面</c> 键，仅抖音目标）：本地图片路径。
    /// **相对路径按发布单元的文件夹解析** —— 图和 params.txt 放同一个文件夹时只写
    /// <c>横版.jpg</c> 就够（绝对路径也认）。落到 <see cref="DouyinExtras.CoverLandscapePath"/>，
    /// 执行端上传后写 <c>cover.horizontal_custom_cover_image_uri</c>。
    /// </summary>
    public string? DouyinLandscapeCover { get; init; }

    /// <summary>
    /// 快手封面文字样式（<c>快手封面文字样式</c> 键，仅快手目标）—— <c>coverTitleStyle</c>。
    /// **取值未验证**：该字段只在作品编辑提交里观测到（恒为空串），官方发布页 2026-11-15
    /// 的 submit 不带它。留空 = 不下发（与页面行为一致）；填了才原样透传。
    /// </summary>
    public string? KuaishouCoverTitleStyle { get; init; }

    /// <summary>
    /// 视频章节（<c>章节</c> 键，可写多行累加，每行 <c>mm:ss 标题</c>）——
    /// 落到抖音 / 快手 / 小红书共用的 <see cref="PlatformParams.Chapters"/>。
    /// 解析复用编辑器同一个 <see cref="PublishFieldText.ParseChapters"/>（两套口径必然漂移）。
    /// </summary>
    public IReadOnlyList<PlatformChapter> Chapters { get; init; } = [];

    /// <summary>
    /// 视频号 @好友（<c>视频号好友</c> 键，逗号/顿号分隔多个昵称）—— 仅视频号目标。
    /// 平台只认昵称（2026-11-11 B2）：落 <see cref="WechatVideoExtras.MentionUsers"/>，
    /// 发布时 <c>objectDesc.mentionedUser=[{nickname}]</c> + 正文末尾补 <c>@昵称 </c>。
    /// </summary>
    public IReadOnlyList<string> WechatMentionUsers { get; init; } = [];

    /// <summary>
    /// 小红书 @用户（<c>小红书@用户</c> 键，仅小红书目标）—— 每条 <c>昵称=userId</c>，
    /// 分号 / 竖线分隔、可多行累加。落到 <see cref="XhsExtras.Ats"/>（发布体 <c>common.ats</c>，
    /// 页面 bundle 逐字 <c>{nickname,userId,userid}</c>），正文末尾由 <c>XhsCaption</c>
    /// 按同一份用户补「@昵称」。<c>userId</c> 是平台用户 ID（24 位十六进制），不是小红书号。
    /// </summary>
    public IReadOnlyList<XhsMentionUser> XhsMentions { get; init; } = [];

    /// <summary>
    /// 快手「专属领域」（<c>快手领域</c> 键，仅快手目标）—— submit 的 <c>domain</c>（一级领域名，
    /// 如「舞蹈」）。取值来自 <c>--probe-kuaishou-domains</c> / 发布页的领域列表，原样下发。
    /// </summary>
    public string? KuaishouDomain { get; init; }

    /// <summary>
    /// 快手「二级领域」（<c>快手二级领域</c> 键，仅快手目标）—— submit 的 <c>secondDomain</c>
    /// （二级领域名，如「舞蹈民族舞」）。与 <see cref="KuaishouDomain"/> 配套使用。
    /// </summary>
    public string? KuaishouSecondDomain { get; init; }

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
        var tags = new List<string>();
        var topicRefs = new List<UnitTopicRef>();
        var activities = new List<UnitActivityRef>();
        PublishVisibility? visibility = null;
        DeclarationKind? declaration = null;
        bool? allowDownload = null, isOriginal = null, isScheduled = null;
        DateTime? scheduledAt = null;
        int? bilibiliTid = null, bilibiliHumanType2 = null;
        string? bilibiliSubtitleFile = null, bilibiliSubtitleLan = null;
        bool? bilibiliGuideCard = null;
        int? bilibiliGuideCardFollowType = null, bilibiliGuideCardStartSeconds = null;
        string? bilibiliRelatedVideo = null, bilibiliRelatedVideoMsg = null;
        int? bilibiliRelatedVideoStartSeconds = null;
        string? bilibiliVoteQuestion = null;
        List<string>? bilibiliVoteOptions = null;
        int? bilibiliVoteStartSeconds = null, bilibiliVoteDurationSeconds = null;
        bool? toutiaoPraise = null;
        string? toutiaoTemplate = null, toutiaoFilter = null;
        int? toutiaoVideoTransArticle = null;
        string? douyinLandscapeCover = null;
        string? kuaishouCoverTitleStyle = null;
        var chapterLines = new List<string>();
        var wechatMentions = new List<string>();
        var xhsMentions = new List<XhsMentionUser>();
        string? kuaishouDomain = null, kuaishouSecondDomain = null;
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

                case "标签" or "标签列表" or "平台标签" or "taglist" or "platformtags":
                    // 追加语义；B站必填 ≥1（预检 Error），头条落到 Label
                    tags.AddRange(SplitTopics(value));
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

                // B站字幕 / 互动引导卡片（B5，2026-11-15）：只对 B站目标生效。
                // 字幕是内容级（每条视频一份 .srt），所以放 params 而不是账号预设。
                case "b站字幕" or "bilibili字幕" or "bilibilisubtitle" or "bilibili_subtitle":
                    bilibiliSubtitleFile = value;
                    break;

                case "b站字幕语言" or "bilibilisubtitlelan" or "bilibili_subtitle_lan":
                    bilibiliSubtitleLan = value;
                    break;

                case "b站互动引导" or "bilibili互动引导" or "bilibiliguidecard"
                    or "bilibili_guide_card":
                    switch (Norm(value))
                    {
                        case "是" or "开启" or "yes" or "true" or "三连+关注" or "三连关注":
                            bilibiliGuideCard = true;
                            bilibiliGuideCardFollowType = 2;
                            break;
                        case "仅三连" or "三连":
                            bilibiliGuideCard = true;
                            bilibiliGuideCardFollowType = 1;
                            break;
                        case "仅关注" or "关注":
                            bilibiliGuideCard = true;
                            bilibiliGuideCardFollowType = 0;
                            break;
                        case "否" or "关闭" or "no" or "false":
                            bilibiliGuideCard = false;
                            break;
                        default:
                            problems.Add(new(lineNo, key,
                                "认不出「是 / 否 / 三连+关注 / 仅三连 / 仅关注」，已忽略"));
                            break;
                    }
                    break;

                case "b站引导时间" or "bilibili引导时间" or "bilibiliguidecardstart"
                    or "bilibili_guide_card_start":
                    if (PublishFieldText.TryParseTime(value, out var guideMs))
                        bilibiliGuideCardStartSeconds = (int)(guideMs / 1000);
                    else
                        problems.Add(new(lineNo, key,
                            "认不出时间（写法示例：00:30 或 30），已忽略（默认视频开头）"));
                    break;

                // 卡片 v2（2026-11-15 B5.2）：关联视频 / 投票弹幕 —— 同样只对 B站目标生效。
                // 关联视频填目标 BVID（「仅自己」稿件会被平台回 79025，见 wire §4.3）。
                case "b站关联视频" or "bilibili关联视频" or "bilibilirelatedvideo"
                    or "bilibili_related_video":
                    bilibiliRelatedVideo = value;
                    break;

                case "b站关联视频描述" or "bilibili关联视频描述"
                    or "bilibilirelatedvideomsg" or "bilibili_related_video_msg":
                    bilibiliRelatedVideoMsg = value;
                    break;

                case "b站关联视频时间" or "bilibili关联视频时间"
                    or "bilibilirelatedvideostart" or "bilibili_related_video_start":
                    if (PublishFieldText.TryParseTime(value, out var relatedMs))
                        bilibiliRelatedVideoStartSeconds = (int)(relatedMs / 1000);
                    else
                        problems.Add(new(lineNo, key,
                            "认不出时间（写法示例：00:30 或 30），已忽略（默认视频开头）"));
                    break;

                case "b站投票" or "bilibili投票" or "bilibilivote" or "bilibili_vote":
                    bilibiliVoteQuestion = value;
                    break;

                case "b站投票选项" or "bilibili投票选项" or "bilibilivoteoptions"
                    or "bilibili_vote_options":
                    // 选项不能含分号（平台规则）—— 用分号 / 竖线分隔没有歧义
                    bilibiliVoteOptions = value
                        .Split([';', '；', '|'], StringSplitOptions.RemoveEmptyEntries
                            | StringSplitOptions.TrimEntries)
                        .ToList();
                    if (bilibiliVoteOptions.Count < 2)
                    {
                        problems.Add(new(lineNo, key,
                            "投票选项至少要 2 个（用分号或竖线分隔），当前不足"));
                    }
                    break;

                case "b站投票时间" or "bilibili投票时间" or "bilibilivotestart"
                    or "bilibili_vote_start":
                    if (PublishFieldText.TryParseTime(value, out var voteStartMs))
                        bilibiliVoteStartSeconds = (int)(voteStartMs / 1000);
                    else
                        problems.Add(new(lineNo, key,
                            "认不出时间（写法示例：00:30 或 30），已忽略（默认视频开头）"));
                    break;

                case "b站投票时长" or "bilibili投票时长" or "bilibili_vote_duration":
                    if (PublishFieldText.TryParseTime(value, out var voteDurationMs))
                        bilibiliVoteDurationSeconds = (int)(voteDurationMs / 1000);
                    else
                        problems.Add(new(lineNo, key,
                            "认不出时长（写法示例：7 或 00:07），已忽略（默认 7 秒）"));
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

                // 视频生成图文（A2）：0 / 1 / 2，也接受中文语义
                case "头条视频生成图文" or "头条生成图文" or "toutiaovideotransarticle"
                    or "toutiao_video_trans_article":
                    if (TryToutiaoTransArticle(value, out var transArticle))
                        toutiaoVideoTransArticle = transArticle;
                    else
                        problems.Add(new(lineNo, key,
                            "认不出「不生成 / 与视频同时发布 / 仅保存草稿」，已忽略"));
                    break;

                // 抖音横版封面（D1，2026-11-14）：竖版 3:4 用文件夹里的 cover.*，
                // 横版 4:3 是抖音的第二张图 —— 每条的图不一样，所以得有内容级键。
                case "抖音横版封面" or "douyin横版封面" or "douyinlandscapecover" or "douyin_landscape_cover":
                    douyinLandscapeCover = value;
                    break;

                // 快手封面文字样式（P3，2026-11-15）：取值未验证（编辑提交里恒为空串）；
                // 留空/不写 = 不下发（官方发布页就是不发）。
                case "快手封面文字样式" or "kuaishoucovertitlestyle" or "kuaishou_cover_title_style"
                    or "covertitlestyle":
                    kuaishouCoverTitleStyle = value.Length == 0 ? null : value;
                    break;

                // 章节（D1，2026-11-14）：一行一个（「00:15 户型」），多行累加 ——
                // 与「话题」同款追加语义。解析在收尾处统一走 ParseChapters
                // （排序 / 去重 / 时间格式全在那一处，和编辑器文本框同一个函数）。
                case "章节" or "chapters" or "chapter":
                    chapterLines.Add(value);
                    break;

                // 视频号 @好友（W5，2026-11-14）：昵称，逗号 / 顿号 / 分号分隔，可多行累加。
                // **不按空格切** —— 昵称里可以有空格（同「话题ID / 官方活动」的口径）。
                case "视频号好友" or "视频号@好友" or "wechatmentions" or "wechat_mentions":
                    foreach (var nick in SplitEntries(value))
                        if (!wechatMentions.Contains(nick)) wechatMentions.Add(nick);
                    break;

                // 小红书 @用户（P5，2026-11-15）：每条「昵称=userId」，分号 / 竖线分隔，可多行累加。
                // userId 是平台用户 ID（24 位十六进制，不是小红书号）；昵称只用于
                // 正文「@昵称」与界面展示。用**最后一个**等号切——昵称里可能有「=」，userId 不会有。
                case "小红书@用户" or "小红书@好友" or "xhsmentions" or "xhs_mentions"
                    or "xiaohongshumentions" or "xiaohongshu_mentions":
                    foreach (var entry in value.Split([';', '；', '|'],
                                 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var eq = entry.LastIndexOf('=');
                        var nickname = eq > 0 ? entry[..eq].Trim() : "";
                        var userId = eq >= 0 && eq < entry.Length - 1 ? entry[(eq + 1)..].Trim() : "";
                        if (nickname.Length == 0 || userId.Length == 0)
                        {
                            problems.Add(new(lineNo, key,
                                "认不出（写法：昵称=userId，多条用分号分隔），已忽略这一条"));
                            continue;
                        }
                        if (xhsMentions.All(u => u.UserId != userId))
                            xhsMentions.Add(new XhsMentionUser(userId, nickname));
                    }
                    break;

                // 快手专属领域（P3/P5，2026-11-15）：一级 / 二级领域名，原样下发
                // submit.domain / submit.secondDomain。取值来自发布页领域列表
                // （--probe-kuaishou-domains 可回查）。
                case "快手领域" or "快手一级领域" or "kuaishoudomain" or "kuaishou_domain":
                    kuaishouDomain = value.Length == 0 ? null : value;
                    break;

                case "快手二级领域" or "kuaishouseconddomain" or "kuaishou_second_domain":
                    kuaishouSecondDomain = value.Length == 0 ? null : value;
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
            Tags = tags,
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
            BilibiliSubtitleFile = bilibiliSubtitleFile,
            BilibiliSubtitleLan = bilibiliSubtitleLan,
            BilibiliGuideCard = bilibiliGuideCard,
            BilibiliGuideCardFollowType = bilibiliGuideCardFollowType,
            BilibiliGuideCardStartSeconds = bilibiliGuideCardStartSeconds,
            BilibiliRelatedVideo = bilibiliRelatedVideo,
            BilibiliRelatedVideoMsg = bilibiliRelatedVideoMsg,
            BilibiliRelatedVideoStartSeconds = bilibiliRelatedVideoStartSeconds,
            BilibiliVoteQuestion = bilibiliVoteQuestion,
            BilibiliVoteOptions = bilibiliVoteOptions,
            BilibiliVoteStartSeconds = bilibiliVoteStartSeconds,
            BilibiliVoteDurationSeconds = bilibiliVoteDurationSeconds,
            ToutiaoPraise = toutiaoPraise,
            ToutiaoTemplate = toutiaoTemplate,
            ToutiaoFilter = toutiaoFilter,
            ToutiaoVideoTransArticle = toutiaoVideoTransArticle,
            DouyinLandscapeCover = douyinLandscapeCover,
            KuaishouCoverTitleStyle = kuaishouCoverTitleStyle,
            Chapters = chapterLines.Count == 0
                ? []
                : PublishFieldText.ParseChapters(string.Join("\n", chapterLines)),
            WechatMentionUsers = wechatMentions,
            XhsMentions = xhsMentions,
            KuaishouDomain = kuaishouDomain,
            KuaishouSecondDomain = kuaishouSecondDomain,
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
    /// 头条视频生成图文：0 = 不生成 / 1 = 与视频同时发布 / 2 = 仅保存草稿。
    /// 接受数字与中文语义；认不出返回 false（由调用方记 Problem，不阻断其它字段）。
    /// </summary>
    private static bool TryToutiaoTransArticle(string value, out int flag)
    {
        switch (Norm(value))
        {
            case "0" or "不生成" or "关闭" or "否" or "no" or "n":
                flag = 0; return true;
            case "1" or "与视频同时发布" or "同时发布" or "同时发" or "发布" or "是" or "yes" or "y":
                flag = 1; return true;
            case "2" or "仅保存草稿" or "仅草稿" or "保存草稿" or "草稿":
                flag = 2; return true;
            default:
                flag = 0; return false;
        }
    }

    /// <summary>
    /// 正文类字段（标题/简介/话题）—— 值里的 <c>#</c> 是**内容**，一律不许当注释切掉。
    /// 其余字段（可见性/声明/下载/原创/发布时间/位置）的取值里不可能出现 <c>#</c>，
    /// 切掉尾注释是安全的。
    /// </summary>
    private static bool IsProseKey(string nk) =>
        nk is "标题" or "title" or "简介" or "desc" or "description"
            or "话题" or "topic" or "topics" or "tag" or "tags"
            // 关联视频描述是写给观众看的一句话，`#` 可能真是正文，不能当尾注释切掉
            or "b站关联视频描述" or "bilibili关联视频描述";

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
