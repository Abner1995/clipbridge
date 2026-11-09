using CreatFlow.Platforms.Douyin;
using CreatFlow.Platforms.Kuaishou;

namespace CreatFlow.NewApp.Services;

// ═══════════════════════════════════════════════════════════════════════════
//  语义 → 平台值的映射中枢（设计文档 §4.3 能力矩阵 / §4.2 统一可见性）
//
//  为什么必须有这么一个文件：四平台加起来只有 6 个真实语义，且各有空洞 ——
//  快手没有营销、小红书没有个人观点、抖音没有自行拍摄。**不能写一张固定映射表**，
//  UI 必须按能力矩阵点亮。把矩阵放这里，UI 与提交路径共用同一份事实，
//  不会出现「UI 说不支持、提交却硬塞一个值」这种自相矛盾。
//
//  ⚠️ 铁律：**任何地方都不许把统一枚举裸强转成平台枚举**。
//  老版正是在这里出的事故 —— 小红书可见性用 SelectedIndex 直接强转，
//  UI 顺序(2=仅互关/3=只给/4=不给)与 SDK 枚举(2=不给/3=只给/4=互关)不一致，
//  选「不给谁看」+ 精挑了一批人 → 实际发成「仅互关好友可见」且白名单被丢弃，
//  用户以为屏蔽了那批人，结果他们反而能看到（缺陷 L11，隐私事故）。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>内容声明的能力矩阵与映射。</summary>
public static class DeclarationMapping
{
    /// <summary>
    /// 该平台支不支持这个语义。UI 据此置灰，提交时据此跳过。
    /// </summary>
    public static bool Supports(string platformKey, DeclarationKind kind)
    {
        if (kind == DeclarationKind.None) return true;
        return platformKey switch
        {
            PlatformNames.Douyin => kind is DeclarationKind.AiGenerated or DeclarationKind.Fiction
                or DeclarationKind.PersonalOpinion or DeclarationKind.Marketing or DeclarationKind.Repost,
            PlatformNames.Kuaishou => kind is DeclarationKind.AiGenerated or DeclarationKind.Fiction
                or DeclarationKind.PersonalOpinion or DeclarationKind.Repost,
            PlatformNames.Xiaohongshu => kind is DeclarationKind.AiGenerated or DeclarationKind.Fiction
                or DeclarationKind.Marketing or DeclarationKind.Repost or DeclarationKind.SelfShot,
            PlatformNames.WechatVideo => kind is DeclarationKind.AiGenerated or DeclarationKind.Fiction
                or DeclarationKind.PersonalOpinion or DeclarationKind.Marketing
                or DeclarationKind.Repost or DeclarationKind.SelfShot,
            // B站创作声明下拉只有 0 无需标注 / 1 AI / 2 虚构 / 3 营销 四项；
            // 「转载」不是声明而是版权（copyright=2），走 CopyrightMapping.Bilibili
            PlatformNames.Bilibili => kind is DeclarationKind.AiGenerated or DeclarationKind.Fiction
                or DeclarationKind.Marketing,
            // 头条作品声明提交字段名未抓（1.3 头条表），v1 不下发 —— 能力矩阵标不支持，
            // UI 灰显而不是「选了却没发出去」
            _ => false,
        };
    }

    /// <summary>
    /// 声明在界面上显示的中文标签 —— **唯一来源**。
    /// 编辑器的单选项（<c>BuildDeclarationOptions</c>）与 params.txt 的取值词表都从它取，
    /// 免得出现「编辑器里叫『内容为转载』、params 里必须写别的」这种漂移。
    /// </summary>
    public static string DeclarationLabel(DeclarationKind kind) => kind switch
    {
        DeclarationKind.None => "无",
        DeclarationKind.AiGenerated => "AI 生成",
        DeclarationKind.Fiction => "虚构演绎",
        DeclarationKind.PersonalOpinion => "个人观点",
        DeclarationKind.Marketing => "含营销推广",
        DeclarationKind.Repost => "内容为转载",
        DeclarationKind.SelfShot => "内容为自行拍摄",
        _ => kind.ToString(),
    };

    /// <summary>不支持时的原因文案（**不静默丢弃** —— 用户必须知道那个平台没声明上）</summary>
    public static string? UnsupportedReason(string platformKey, DeclarationKind kind)
    {
        if (Supports(platformKey, kind)) return null;
        return kind switch
        {
            DeclarationKind.SelfShot when platformKey == PlatformNames.Douyin =>
                "抖音没有「自行拍摄」声明",
            DeclarationKind.SelfShot when platformKey == PlatformNames.Kuaishou =>
                "快手没有「自行拍摄」声明",
            DeclarationKind.Marketing when platformKey == PlatformNames.Kuaishou =>
                "快手没有「含营销推广信息」声明",
            DeclarationKind.PersonalOpinion when platformKey == PlatformNames.Xiaohongshu =>
                "小红书没有「内容为个人观点」声明",
            DeclarationKind.Repost when platformKey == PlatformNames.Bilibili =>
                "B站的「转载」是版权选项，去掉原创声明即按转载发布",
            DeclarationKind.SelfShot when platformKey == PlatformNames.Bilibili =>
                "B站没有「自行拍摄」声明",
            DeclarationKind.PersonalOpinion when platformKey == PlatformNames.Bilibili =>
                "B站没有「内容为个人观点」声明",
            _ when platformKey == PlatformNames.Toutiao =>
                "头条作品声明的提交字段尚未接入，本条将不带声明",
            _ => "该平台不支持此声明",
        };
    }

    /// <summary>抖音：choose_value（null = 不声明）</summary>
    public static string? DouyinChooseValue(DeclarationKind kind) => kind switch
    {
        DeclarationKind.AiGenerated => "aigc",
        DeclarationKind.Fiction => "only_fun_new",
        DeclarationKind.PersonalOpinion => "personal_opinion",
        DeclarationKind.Marketing => "marketing",
        DeclarationKind.Repost => "repost",
        _ => null, // None / SelfShot（抖音无自行拍摄）
    };

    /// <summary>快手：declaration（1=AI / 2=虚构 / 3=个人观点 / 4=转载；null = 不声明）</summary>
    public static int? KuaishouDeclaration(DeclarationKind kind) => kind switch
    {
        DeclarationKind.AiGenerated => 1,
        DeclarationKind.Fiction => 2,
        DeclarationKind.PersonalOpinion => 3,
        DeclarationKind.Repost => 4,
        _ => null, // None / Marketing / SelfShot（快手均无）
    };

    /// <summary>小红书：origin（1=虚构 / 2=AI / 3=营销 / 4=自行拍摄 / 5=转载；null = 不声明）</summary>
    public static int? XhsOrigin(DeclarationKind kind) => kind switch
    {
        DeclarationKind.Fiction => 1,
        DeclarationKind.AiGenerated => 2,
        DeclarationKind.Marketing => 3,
        DeclarationKind.SelfShot => 4,
        DeclarationKind.Repost => 5,
        _ => null, // None / PersonalOpinion（小红书无）
    };

    /// <summary>
    /// 视频号：tagType（1=AI / 2=营销 / 3=虚构 / 5=自行拍摄 / 7=转载 / 8=个人观点；null = 不声明）。
    /// 与抓包样例 <c>examples/wechatvideo/tag/finder_get_object_tag_list.json</c> 的全量 7 值对齐 ——
    /// 老版 C# 只认 5 和 7，1/2/3/8 被丢弃（设计文档 §4.3）。
    /// </summary>
    public static int? WechatTagType(DeclarationKind kind) => kind switch
    {
        DeclarationKind.AiGenerated => 1,
        DeclarationKind.Marketing => 2,
        DeclarationKind.Fiction => 3,
        DeclarationKind.SelfShot => 5,
        DeclarationKind.Repost => 7,
        DeclarationKind.PersonalOpinion => 8,
        _ => null,
    };

    /// <summary>
    /// B站：<c>creation_statement.id</c>（0 内容无需标注 / 1 含AI生成内容 / 2 含虚构演绎内容 /
    /// 3 内容含营销信息）。选项清单来自抓包页面下拉（<c>docs/bilibili/20261104/接口说明书.md</c> §2.8），
    /// 接口响应里没有 —— 阶段 4 实账号探针再核对一次。
    /// </summary>
    public static int BilibiliCreationStatement(DeclarationKind kind) => kind switch
    {
        DeclarationKind.AiGenerated => 1,
        DeclarationKind.Fiction => 2,
        DeclarationKind.Marketing => 3,
        _ => 0,
    };

    /// <summary>
    /// 抖音的 user_declare_info JSON。**这是唯一需要二次组装的**（转载要带来源 / 站内作品 ID）。
    /// </summary>
    public static string? DouyinUserDeclareInfo(DeclarationKind kind, ContentDeclaration? detail)
    {
        var choose = DouyinChooseValue(kind);
        if (choose is null) return null;

        if (kind != DeclarationKind.Repost || detail is null)
            return DouyinDeclarationOptions.BuildUserDeclareInfo(choose);

        // 转载：站内作品要带 from_item_id，站外只需 source 文本
        var source = detail.DouyinRepostFromInside ? "抖音作品" : "站外";
        return DouyinDeclarationOptions.BuildUserDeclareInfo(
            choose,
            repostSource: source,
            fromItemId: detail.DouyinRepostFromInside ? detail.DouyinRepostItemId : null);
    }
}

/// <summary>
/// 原创声明能力矩阵 —— 只有投稿体里带原创字段的平台。
/// 抖音/快手/视频号没有投稿字段（视频号只能发布后在平台声明），UI 上不解释成「漏了」。
/// </summary>
public static class OriginalMapping
{
    public static bool Supports(string platformKey) =>
        platformKey is PlatformNames.Xiaohongshu or PlatformNames.Bilibili or PlatformNames.Toutiao;
}

/// <summary>
/// 版权映射（B站 <c>copyright</c>）—— 与「内容声明」是两回事：
/// 声明下拉里没有「转载」，转载语义体现在版权上（2）；勾了原创 → 自制（1）。
/// 抓包里页面默认 3 的含义待实账号探针确认（阶段 4），未勾原创时**沿用 3**，不改页面默认。
/// </summary>
public static class CopyrightMapping
{
    public static int Bilibili(DeclarationKind kind, bool isOriginal)
    {
        if (kind == DeclarationKind.Repost) return 2;
        return isOriginal ? 1 : 3;
    }
}

/// <summary>统一可见性 → 各平台映射。</summary>
public static class VisibilityMapping
{
    /// <summary>
    /// 可见性在界面上显示的中文标签 —— **唯一来源**（编辑器单选项与 params.txt 词表共用）。
    /// </summary>
    public static string VisibilityLabel(PublishVisibility v) => v switch
    {
        PublishVisibility.Public => "公开",
        PublishVisibility.Private => "仅自己",
        PublishVisibility.MutualFriends => "仅互关好友可见",
        PublishVisibility.OnlyUsers => "只给谁看",
        PublishVisibility.ExcludeUsers => "不给谁看",
        PublishVisibility.FollowersOnly => "仅粉丝可见",
        _ => v.ToString(),
    };

    /// <summary>统一六态的支持面（设计文档 §4.2 表 + 头条抓包）</summary>
    public static bool Supports(string platformKey, PublishVisibility v) => v switch
    {
        // 公开：六平台都有（视频号没有可见性设置，恒公开）
        PublishVisibility.Public => true,
        // 仅自己：B站 is_only_self / 头条 HideType=1
        PublishVisibility.Private => platformKey is PlatformNames.Douyin
            or PlatformNames.Kuaishou or PlatformNames.Xiaohongshu
            or PlatformNames.Bilibili or PlatformNames.Toutiao,
        // 仅粉丝：只有头条 HideType=2（抖音的「好友可见」语义不同，不做映射）
        PublishVisibility.FollowersOnly => platformKey == PlatformNames.Toutiao,
        // 后三态是小红书独有
        _ => platformKey == PlatformNames.Xiaohongshu,
    };

    /// <summary>该平台实际会用的取值 —— 不支持的一律回退「公开」</summary>
    public static PublishVisibility Coerce(string platformKey, PublishVisibility v) =>
        Supports(platformKey, v) ? v : PublishVisibility.Public;

    /// <summary>回退说明（UI 用；null = 无需说明）</summary>
    public static string? FallbackNote(string platformKey, PublishVisibility v)
    {
        if (Supports(platformKey, v)) return null;
        return platformKey == PlatformNames.WechatVideo
            ? "视频号不支持可见性设置，将公开"
            : "该平台不支持此可见性，将退回「公开」";
    }

    /// <summary>抖音：visibility_type（0=公开 / 1=仅自己 / 2=好友）</summary>
    public static DouyinVisibility Douyin(PublishVisibility v) => Coerce(PlatformNames.Douyin, v) switch
    {
        PublishVisibility.Private => DouyinVisibility.Private,
        _ => DouyinVisibility.Public,
        // 注意：统一五态里没有「仅好友」这一态，抖音的 Friends(2) 在新版无入口 ——
        // 老版有，但小红书叫它「仅互关好友可见」，两者语义并不相同，不强行合并。
    };

    /// <summary>快手：photoStatus（1=公开 / 2=仅自己 / 4=好友）</summary>
    public static KuaishouVisibility Kuaishou(PublishVisibility v) => Coerce(PlatformNames.Kuaishou, v) switch
    {
        PublishVisibility.Private => KuaishouVisibility.Self,
        _ => KuaishouVisibility.Public,
    };

    /// <summary>B站：is_only_self（0=公开 / 1=仅自己）</summary>
    public static int Bilibili(PublishVisibility v) =>
        Coerce(PlatformNames.Bilibili, v) == PublishVisibility.Private ? 1 : 0;

    /// <summary>头条：HideInfo.HideType（0=公开 / 1=仅我 / 2=粉丝可见）</summary>
    public static int Toutiao(PublishVisibility v) => Coerce(PlatformNames.Toutiao, v) switch
    {
        PublishVisibility.Private => 1,
        PublishVisibility.FollowersOnly => 2,
        _ => 0,
    };

    /// <summary>
    /// 小红书：privacy_info 的 type/op_type。
    ///
    /// **按名字映射，绝不强转 int** —— 老版的隐私事故就出在强转上（缺陷 L11）。
    /// 返回 (type, opType, requiresUserIds)。
    /// </summary>
    public static (int Type, int OpType, bool RequiresUserIds) Xhs(PublishVisibility v) => v switch
    {
        PublishVisibility.Private => (1, 1, false),
        PublishVisibility.ExcludeUsers => (0, 2, true),   // 不给谁看：op_type=2
        PublishVisibility.OnlyUsers => (0, 3, true),      // 只给谁看：op_type=3
        PublishVisibility.MutualFriends => (4, 1, false),
        _ => (0, 1, false),                               // 公开
    };

    /// <summary>小红书白名单用户上限 —— 超了平台侧直接拒</summary>
    public const int XhsMaxPrivacyUsers = 200;
}

/// <summary>各平台的字数与长度规则（预检与提交共用一份事实）</summary>
public static class PlatformLimits
{
    /// <summary>小红书标题上限：20 字位</summary>
    public const int XhsTitleUnits = 20;

    /// <summary>
    /// 小红书正文上限 1000 字（2026-11-05 字段映射 §2.1）。
    /// 话题会被合进正文，所以预检按**最终正文**数（2.6.6），不是只数用户写的简介。
    /// </summary>
    public const int XhsDescChars = 1000;

    /// <summary>
    /// 快手话题标签上限：3 个。
    /// 2026-10-22 实测：4 个话题时 submit 直接拒
    /// <c>result=1000, msg=话题标签数量超过上限：4</c>，把多出来的裁掉才发得出去。
    /// 数的是正文里的 <c>#话题</c>（含用户自己写在简介里的）与 <c>recTagIdList</c>。
    /// </summary>
    public const int KuaishouMaxTopics = 3;

    /// <summary>
    /// 快手正文（<c>caption</c>）上限 500 字 —— 发布页计数器实测 `0/500`
    /// （docs/kuaishou/20261105/字段映射.md）。快手没有独立标题，
    /// 标题 + 简介 + 话题合并后数这一份，超出按 # 话题边界截断（见
    /// <see cref="PublishPayloadBuilder.ComposeKuaishouCaption"/>）。
    /// </summary>
    public const int KuaishouCaptionChars = 500;

    /// <summary>视频号短标题上限</summary>
    public const int WechatShortTitleChars = 20;

    /// <summary>视频号短标题下限 —— 页面实测 5 字报「标题至少6个字」，不足则不下发</summary>
    public const int WechatShortTitleMinChars = 6;

    /// <summary>小红书视频最短时长（秒）—— 老版只有软提示、从不强制（缺陷 L12）</summary>
    public const double XhsMinDurationS = 10.0;

    /// <summary>
    /// 小红书标题字位计法（逆向自 <c>xhs_creator_sign_other.js:138969</c>）：
    /// 非 ASCII 与 <c>^</c> 各计 2 分、ASCII 计 1 分、总分 ÷2 上取整。
    /// 老版只在 UI 显示、不进队列也不进重试路径。
    /// </summary>
    public static int XhsTitleLength(string s)
    {
        var score = 0;
        foreach (var ch in s) score += ch > 127 || ch == '^' ? 2 : 1;
        return (int)Math.Ceiling(score / 2.0);
    }

    /// <summary>视频号短标题取值：标题超长则截断（老版 <c>HomeViewModel.cs:3874</c> 同款）</summary>
    public static string WechatShortTitle(string? title) =>
        string.IsNullOrEmpty(title) ? "" : title[..Math.Min(WechatShortTitleChars, title.Length)];

    // ── B站（页面口径，阶段 4 实账号探针核对）──

    /// <summary>B站标题上限 80 字</summary>
    public const int BilibiliTitleChars = 80;
    /// <summary>B站简介上限 2000 字</summary>
    public const int BilibiliDescChars = 2000;
    /// <summary>B站标签上限 10 个（<c>tag</c> 逗号串）</summary>
    public const int BilibiliMaxTags = 10;

    /// <summary>
    /// B站标签的**完整**去重列表（不截断）—— 预检数「1~10」用（2.6.6）。
    /// 切法见 <see cref="BilibiliTags"/>；两处必须同一套口径，否则预检数出来的数
    /// 和实际下发的数会对不上。
    /// </summary>
    public static List<string> BilibiliAllTags(IEnumerable<string> tags) => tags
        .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Where(t => t.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// B站标签：去空、按逗号拆开（params.txt 的 `标签` 一行可写逗号串）、去重、最多
    /// <see cref="BilibiliMaxTags"/> 个。B站投稿要求至少 1 个标签 —— 不足由预检拦（2.6.6），
    /// 这里不塞默认标签（「不能写死默认值」同样适用于标签）。
    /// </summary>
    public static List<string> BilibiliTags(IEnumerable<string> tags) =>
        [.. BilibiliAllTags(tags).Take(BilibiliMaxTags)];

    // ── 头条（1.3 头条表）──

    /// <summary>头条标题 1~30 字</summary>
    public const int ToutiaoTitleChars = 30;
    /// <summary>头条简介上限 400 字（<c>Abstract</c>，仅横版）</summary>
    public const int ToutiaoAbstractChars = 400;
}

/// <summary>
/// 封面取值规则（阶段 2.5）。目前只有抖音有「帧 / 裁剪 / 自定义图」三者互斥的规则：
///
/// 2026-11-05 抓包（带自定义封面图的那条 create_v2）里 <c>cover</c> 是
/// <c>{poster, poster_delay:0, custom_cover_image_width:1051, custom_cover_image_height:788,
/// cover_tools_info, cover_tools_extend_info}</c> —— 页面上传封面图后
/// <c>poster_delay</c> **恒 0**（帧设置不生效），裁剪尺寸 = 裁剪结果尺寸；
/// 没带自定义图的抓包只有 <c>poster</c>，两个尺寸字段都不出现。
/// </summary>
public static class CoverMapping
{
    /// <summary>
    /// 抖音封面三件套的最终下发值：
    /// 有自定义封面图 → 帧不下发（页面恒 0）、裁剪 = 用户填的否则封面图实际像素；
    /// 没有自定义封面 → 帧照发、裁剪不下发。
    /// </summary>
    public static (long? FrameMs, int? CropWidth, int? CropHeight) Douyin(
        long? frameMs, int? cropWidth, int? cropHeight,
        bool hasUploadedCover, (int Width, int Height)? coverSize)
    {
        if (!hasUploadedCover) return (frameMs, null, null);
        var (w, h) = ImageDimensionProbe.ResolveCrop(cropWidth, cropHeight, coverSize);
        return (null, w, h);
    }
}
