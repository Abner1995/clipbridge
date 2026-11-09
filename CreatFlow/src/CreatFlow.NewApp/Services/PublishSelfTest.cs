using System.Diagnostics;
using System.Text.Json;
using CreatFlow.NewApp.Models;
using CreatFlow.Platforms.Bilibili;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Core.Vod;
using CreatFlow.Platforms.Douyin;
using CreatFlow.Platforms.Kuaishou;
using CreatFlow.Platforms.Topics;
using CreatFlow.Platforms.Toutiao;
using CreatFlow.Platforms.WechatVideo;
using CreatFlow.Platforms.Xiaohongshu;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 发布页底座自检（<c>--selftest</c>）—— 不启动 UI，直接验 P3-1 的三块地基。
///
/// 为什么需要它：UI 要成对启动桌面端 + 服务端才能看，但底座（映射矩阵 / 本地持久化 /
/// 视频探测）是纯本地逻辑，跑一秒钟就能验完。尤其 <see cref="DeclarationMapping"/> 与
/// <see cref="VisibilityMapping"/> 是**表格驱动的**——表填错了 UI 上看不出来，
/// 只会在真发布时把内容发到错误的可见性上（老版就是这么出的隐私事故）。
///
/// 结果写 %TEMP%/newapp_publish_selftest.txt。
/// </summary>
public static class PublishSelfTest
{
    public static async Task<int> RunAsync()
    {
        var sb = new StringBuilder();
        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            sb.AppendLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        sb.AppendLine("═══ 发布页底座自检（P3-1）═══");

        // ── 1. 内容声明能力矩阵 ──
        sb.AppendLine("\n[1] 内容声明能力矩阵（设计文档 §4.3）");
        Check("抖音 支持 AI 生成", DeclarationMapping.Supports(PlatformNames.Douyin, DeclarationKind.AiGenerated));
        Check("抖音 不支持自行拍摄", !DeclarationMapping.Supports(PlatformNames.Douyin, DeclarationKind.SelfShot));
        Check("快手 不支持营销推广", !DeclarationMapping.Supports(PlatformNames.Kuaishou, DeclarationKind.Marketing));
        Check("小红书 不支持个人观点", !DeclarationMapping.Supports(PlatformNames.Xiaohongshu, DeclarationKind.PersonalOpinion));
        Check("视频号 六个语义全支持",
            Enum.GetValues<DeclarationKind>().All(k => DeclarationMapping.Supports(PlatformNames.WechatVideo, k)));

        Check("抖音 转载 → repost", DeclarationMapping.DouyinChooseValue(DeclarationKind.Repost) == "repost");
        Check("快手 AI → 1", DeclarationMapping.KuaishouDeclaration(DeclarationKind.AiGenerated) == 1);
        Check("小红书 自行拍摄 → origin 4", DeclarationMapping.XhsOrigin(DeclarationKind.SelfShot) == 4);
        // 老版 C# 只认 5 和 7，1/2/3/8 全丢 —— 这条守住「全量 7 值」
        Check("视频号 个人观点 → 8（老版丢弃）", DeclarationMapping.WechatTagType(DeclarationKind.PersonalOpinion) == 8);
        Check("视频号 营销 → 2（老版丢弃）", DeclarationMapping.WechatTagType(DeclarationKind.Marketing) == 2);
        Check("视频号 不声明 → null", DeclarationMapping.WechatTagType(DeclarationKind.None) is null);

        // ── 2. 可见性映射（L11 隐私事故的回归防线）──
        sb.AppendLine("\n[2] 可见性映射（缺陷 L11 回归防线）");
        var xhsExclude = VisibilityMapping.Xhs(PublishVisibility.ExcludeUsers);
        var xhsMutual = VisibilityMapping.Xhs(PublishVisibility.MutualFriends);
        var xhsOnly = VisibilityMapping.Xhs(PublishVisibility.OnlyUsers);
        Check("小红书 不给谁看 → op_type=2 且要白名单", xhsExclude == (0, 2, true),
            $"实际 {xhsExclude}");
        Check("小红书 只给谁看 → op_type=3 且要白名单", xhsOnly == (0, 3, true), $"实际 {xhsOnly}");
        Check("小红书 仅互关好友 → type=4 不要白名单", xhsMutual == (4, 1, false), $"实际 {xhsMutual}");
        Check("「不给谁看」与「仅互关好友」**不是**同一个值",
            xhsExclude != xhsMutual && xhsExclude.Type != xhsMutual.Type);

        Check("视频号 不支持任何可见性（恒公开）",
            VisibilityMapping.Coerce(PlatformNames.WechatVideo, PublishVisibility.OnlyUsers) == PublishVisibility.Public);
        Check("抖音 支持仅自己", VisibilityMapping.Supports(PlatformNames.Douyin, PublishVisibility.Private));
        Check("抖音 不支持只给谁看（退回公开）",
            VisibilityMapping.Coerce(PlatformNames.Douyin, PublishVisibility.OnlyUsers) == PublishVisibility.Public);
        Check("抖音 仅自己 → Private(1)", VisibilityMapping.Douyin(PublishVisibility.Private) == DouyinVisibility.Private);

        // 六平台并集：FollowersOnly 只属于头条；抖音「好友可见」不并入
        Check("头条 支持仅粉丝可见",
            VisibilityMapping.Supports(PlatformNames.Toutiao, PublishVisibility.FollowersOnly));
        Check("头条 仅粉丝 → HideType=2", VisibilityMapping.Toutiao(PublishVisibility.FollowersOnly) == 2);
        Check("头条 仅自己 → HideType=1", VisibilityMapping.Toutiao(PublishVisibility.Private) == 1);
        Check("头条 公开 → HideType=0", VisibilityMapping.Toutiao(PublishVisibility.Public) == 0);
        Check("头条 不支持「只给谁看」（退回公开）",
            VisibilityMapping.Toutiao(PublishVisibility.OnlyUsers) == 0);
        Check("B站 支持仅自己、不支持仅粉丝",
            VisibilityMapping.Supports(PlatformNames.Bilibili, PublishVisibility.Private)
            && !VisibilityMapping.Supports(PlatformNames.Bilibili, PublishVisibility.FollowersOnly));
        Check("B站 仅自己 → is_only_self=1", VisibilityMapping.Bilibili(PublishVisibility.Private) == 1);
        Check("B站 公开 → is_only_self=0", VisibilityMapping.Bilibili(PublishVisibility.Public) == 0);
        Check("仅粉丝可见 不被抖音/快手/小红书/B站/视频号支持",
            new[] { PlatformNames.Douyin, PlatformNames.Kuaishou, PlatformNames.Xiaohongshu,
                    PlatformNames.Bilibili, PlatformNames.WechatVideo }
                .All(k => !VisibilityMapping.Supports(k, PublishVisibility.FollowersOnly)));
        Check("仅粉丝可见 中文标签 = 仅粉丝可见",
            VisibilityMapping.VisibilityLabel(PublishVisibility.FollowersOnly) == "仅粉丝可见");

        // ── 3. 小红书标题字位（逆向自 xhs_creator_sign_other.js:138969）──
        sb.AppendLine("\n[3] 小红书标题字位");
        Check("纯 ASCII「abc」= 2", PlatformLimits.XhsTitleLength("abc") == 2,
            $"实际 {PlatformLimits.XhsTitleLength("abc")}");
        Check("纯中文「夏日海边」= 4", PlatformLimits.XhsTitleLength("夏日海边") == 4,
            $"实际 {PlatformLimits.XhsTitleLength("夏日海边")}");
        Check("「^」计 2 分 = 1", PlatformLimits.XhsTitleLength("^") == 1);
        Check("中英混排「夏日vlog」= 4", PlatformLimits.XhsTitleLength("夏日vlog") == 4,
            $"实际 {PlatformLimits.XhsTitleLength("夏日vlog")}");

        // ── 4. 本地持久化往返 ──
        sb.AppendLine("\n[4] 本地持久化（PublishDraftStore）");
        var tempRoot = Path.Combine(Path.GetTempPath(), "creatflow-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new PublishDraftStore(tempRoot);
            var sid = Guid.NewGuid();
            var key = PublishDraftStore.KeyOf(PlatformNames.Douyin, sid);

            await store.SaveDefaultsAsync(new Dictionary<string, PlatformParams>
            {
                [key] = new PlatformParams
                {
                    CollectionId = "7123",
                    CollectionName = "日常vlog",
                    Location = new PoiRef("poi-1", "外滩", "上海市黄浦区"),
                    Visibility = PublishVisibility.OnlyUsers,
                    IsOriginal = true,
                },
            });
            var loaded = await store.LoadDefaultsAsync();
            Check("预设写入后读回", loaded.Count == 1 && loaded.ContainsKey(key));
            Check("预设字段完整（含 PoiRef 嵌套）",
                loaded[key].Location == new PoiRef("poi-1", "外滩", "上海市黄浦区")
                && loaded[key].Visibility == PublishVisibility.OnlyUsers
                && loaded[key].IsOriginal == true);

            // 账号解绑 → 条目应被清掉
            Check("解绑账号后预设被清理", await store.PruneAsync([]) == 1
                && (await store.LoadDefaultsAsync()).Count == 0);

            // ── 1.4 公共字段：一次填满，草稿往返必须一项不丢 ──
            PlatformParams RichParams() => new()
            {
                CollectionOrder = 3,
                AllowComment = false,
                AllowDanmaku = true,
                ExternalLink = "https://example.com/link",
                MusicId = "7123456789",
                Chapters = [new PlatformChapter(0, "开场"), new PlatformChapter(15_000, "正片")],
                Tags = ["家装", "苏州"],
                CoverTitle = "封面大字",
                ProductIds = ["p-1", "p-2"],
                CoPublishUsers = [new CoPublishUser("u-1", 1, "同事")],
                ActivityIds = ["7702034615116174390"],
                // 2.6.2 结构化话题 / 官方活动 —— 一条带 ID、一条纯文本、一条只属于活动
                TopicRefs =
                [
                    new TopicRef { Id = "1658571727441931", Name = "家居", Type = "0",
                        Source = TopicRefSources.Recommend, Metric = "1.7亿浏览" },
                    new TopicRef { Name = "夏日", Source = TopicRefSources.Manual },
                ],
                Activities =
                [
                    new ActivityRef
                    {
                        Id = "7702034615116174390",
                        Name = "全民进入购物听劝时代",
                        Cover = "https://example.com/activity.png",
                        Reward = "有奖活动",
                        EndTime = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.FromHours(8)),
                        Topics =
                        [
                            new TopicRef { Id = "1658571727441931", Name = "家居",
                                Source = TopicRefSources.Activity },
                        ],
                    },
                ],
                Praise = true,
                Summary = "一分钟看完",
                ShortTitle = "苏州买房",
                SyncTargets = [PlatformNames.Toutiao],
            };

            // 草稿只存视频仍存在的条目
            var alive = Path.GetTempFileName();
            try
            {
                var draft = new PublishDraft(alive)
                {
                    Title = "夏日海边",
                    Description = "记录夏天",
                    IsOriginal = true,   // 2026-11-06 全局原创
                    CoverFrameMs = 3200, // 2026-11-06 封面帧（毫秒）
                };
                draft.Topics.Add("夏日");
                draft.Targets.Add(new PublishTarget(PlatformNames.Douyin, sid, "主号") { IsPrimary = true });
                draft.Declaration.Kind = DeclarationKind.Repost;
                draft.Overrides[PublishDraftStore.KeyOf(PlatformNames.Kuaishou, sid)] = RichParams();
                await store.SaveDraftsAsync([draft, new PublishDraft(Path.Combine(tempRoot, "不存在.mp4"))]);

                var drafts = await store.LoadDraftsAsync();
                Check("草稿恢复时丢弃已不存在的视频", drafts.Count == 1, $"实际 {drafts.Count} 条");
                Check("草稿字段往返完整（含全局原创）",
                    drafts[0].Title == "夏日海边"
                    && drafts[0].Topics.Contains("夏日")
                    // 账号预设里的结构化话题也回填全局芯片区（2.6.2；去重后共 2 个）
                    && drafts[0].Topics.Contains("家居") && drafts[0].Topics.Count == 2
                    && drafts[0].Targets.Count == 1 && drafts[0].Targets[0].IsPrimary
                    && drafts[0].Declaration.Kind == DeclarationKind.Repost
                    && drafts[0].IsOriginal);
                Check("封面帧随草稿往返（毫秒）", drafts[0].CoverFrameMs == 3200,
                    $"{drafts[0].CoverFrameMs}");

                var richLoaded = drafts[0].Overrides[PublishDraftStore.KeyOf(PlatformNames.Kuaishou, sid)];
                Check("公共字段随草稿覆盖完整往返（1.4）",
                    richLoaded.CollectionOrder == 3
                    && richLoaded.AllowComment == false
                    && richLoaded.AllowDanmaku == true
                    && richLoaded.ExternalLink == "https://example.com/link"
                    && richLoaded.MusicId == "7123456789"
                    && richLoaded.Chapters.SequenceEqual(
                        [new PlatformChapter(0, "开场"), new PlatformChapter(15_000, "正片")])
                    && richLoaded.Tags.SequenceEqual(["家装", "苏州"])
                    && richLoaded.CoverTitle == "封面大字"
                    && richLoaded.ProductIds.SequenceEqual(["p-1", "p-2"])
                    && richLoaded.CoPublishUsers.SequenceEqual([new CoPublishUser("u-1", 1, "同事")])
                    && richLoaded.ActivityIds.SequenceEqual(["7702034615116174390"])
                    && richLoaded.Praise == true
                    && richLoaded.Summary == "一分钟看完"
                    && richLoaded.ShortTitle == "苏州买房"
                    && richLoaded.SyncTargets.SequenceEqual([PlatformNames.Toutiao]));

                Check("结构化话题随草稿往返（2.6.2：ID/类型/来源 + 2.6.3 讨论量文案）",
                    richLoaded.TopicRefs.Count == 2
                    && richLoaded.TopicRefs[0] is { Id: "1658571727441931", Name: "家居", Type: "0",
                        Source: TopicRefSources.Recommend, Metric: "1.7亿浏览" }
                    && richLoaded.TopicRefs[1] is { Id: null, Name: "夏日" }
                    && !richLoaded.TopicRefs[1].HasPlatformId,
                    $"{richLoaded.TopicRefs.Count} 条");

                Check("官方活动快照随草稿往返（截止时间 + 关联话题）",
                    richLoaded.Activities.Count == 1
                    && richLoaded.Activities[0] is
                    {
                        Id: "7702034615116174390",
                        Name: "全民进入购物听劝时代",
                        Cover: "https://example.com/activity.png",
                        Reward: "有奖活动",
                    } act
                    && act.EndTime == new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.FromHours(8))
                    && act.Topics.Count == 1 && act.Topics[0].Name == "家居",
                    $"{richLoaded.Activities.Count} 个");

                // Clone 必须深拷贝列表：预设播种到目标后改一边不能串另一边
                var richSource = RichParams();
                var richClone = richSource.Clone();
                richClone.Tags.Add("新增");
                richClone.Chapters.Add(new PlatformChapter(30_000, "结尾"));
                richClone.TopicRefs.Add(new TopicRef { Name = "新增话题" });
                richClone.Activities.Clear();
                Check("公共字段 Clone 深拷贝列表（改克隆不串原对象）",
                    richSource.Tags.Count == 2 && richSource.Chapters.Count == 2
                    && richSource.TopicRefs.Count == 2 && richSource.Activities.Count == 1
                    && richClone.Tags.Count == 3 && richClone.Chapters.Count == 3
                    && richClone.TopicRefs.Count == 3 && richClone.Activities.Count == 0,
                    $"源 {richSource.Tags.Count}/{richSource.Chapters.Count}/{richSource.TopicRefs.Count}/{richSource.Activities.Count}"
                    + $" · 克隆 {richClone.Tags.Count}/{richClone.Chapters.Count}/{richClone.TopicRefs.Count}/{richClone.Activities.Count}");
                Check("公共字段 Clone 复制标量",
                    richClone.CollectionOrder == 3 && richClone.ShortTitle == "苏州买房"
                    && richClone.Praise == true && richClone.AllowDanmaku == true);

                // ── 老档一次性迁移：原创原先存在 target 覆盖上（没有全局字段）──
                // 新档明确存 false 的**不许**被迁移（否则「全局关着却发原创」永远修不掉）
                string LegacyJson(bool? topLevelOriginal)
                {
                    var one = new Dictionary<string, object?>
                    {
                        ["videoPath"] = alive,
                        ["title"] = "旧稿",
                        ["overrides"] = new Dictionary<string, object?>
                        {
                            [PublishDraftStore.KeyOf(PlatformNames.Xiaohongshu, sid)] =
                                new Dictionary<string, object?> { ["isOriginal"] = true },
                        },
                    };
                    if (topLevelOriginal is { } o) one["isOriginal"] = o;
                    return JsonSerializer.Serialize(new[] { one });
                }

                await File.WriteAllTextAsync(store.GetDraftsPath(), LegacyJson(null));
                var migrated = await store.LoadDraftsAsync();
                var migratedKey = PublishDraftStore.KeyOf(PlatformNames.Xiaohongshu, sid);
                Check("老档：覆盖里的原创回填到全局、覆盖被清掉",
                    migrated.Count == 1 && migrated[0].IsOriginal
                    && migrated[0].Overrides.TryGetValue(migratedKey, out var mp) && mp.IsOriginal is null,
                    $"draft={migrated.FirstOrDefault()?.IsOriginal}");

                // 旧预设/旧草稿没有 1.4 新字段 → 初始化器兜底为空表/null，绝不能被 JSON 读成 null 后炸
                var legacyParams = migrated.FirstOrDefault()?.Overrides.GetValueOrDefault(migratedKey);
                Check("旧预设缺新公共字段 → 空表 / null",
                    legacyParams is not null && legacyParams.Tags.Count == 0
                    && legacyParams.Chapters.Count == 0 && legacyParams.CollectionOrder is null
                    && legacyParams.ShortTitle is null && legacyParams.SyncTargets.Count == 0
                    && legacyParams.CoPublishUsers.Count == 0
                    && legacyParams.TopicRefs.Count == 0 && legacyParams.Activities.Count == 0);
                Check("旧预设缺六平台 Extras → 整组 null（不炸）",
                    legacyParams is not null && legacyParams.Douyin is null && legacyParams.Kuaishou is null
                    && legacyParams.Xhs is null && legacyParams.WechatVideo is null
                    && legacyParams.Bilibili is null && legacyParams.Toutiao is null);

                // ── 1.5 六平台 Extras 分组：预设往返 + Clone 隔离 ──
                var extrasKey = PublishDraftStore.KeyOf(PlatformNames.Toutiao, sid);
                var extrasParams = new PlatformParams
                {
                    NotifyFollowers = false,   // 顶层快手长尾（与 Extras 一起验）
                    Douyin = new DouyinExtras
                    {
                        CoverCropWidth = 1051,
                        CoverCropHeight = 788,
                        CoverToolsInfo = "{\"cover_type\":1}",
                        CoverToolsExtendInfo = "{\"recommendCoverList\":[]}",
                        Mentions = "[]",
                        InteractionStickers = "[]",
                    },
                    Kuaishou = new KuaishouExtras
                    {
                        CoverTitleStyle = "1",
                        PkCoverKey = "pk-1",
                        PkCoverTimeStamp = -1,
                        PkCoverType = 1,
                        PkCoverSize = "",
                        ProfileCoverKey = "pc-1",
                        RiseQuality = false,
                        UseAiCaption = true,
                        UseAiCaptionCover = false,
                        UseAiCover = true,
                        UseIdealTime = true,
                        DisallowRecreation = true,
                        Domain = "vlog",
                        SecondDomain = "life",
                        MovieId = "m-1",
                        ProjectId = "p-1",
                    },
                    Xhs = new XhsExtras
                    {
                        Ats = "[]",
                        GoodsInfo = "{}",
                        BizRelations = "[]",
                        SketchCollectionId = "sk-1",
                        OrderBind = "{\"order_id\":\"o-1\"}",
                        ChapterSyncText = true,
                        PkCoverBizRelations = "[]",
                    },
                    WechatVideo = new WechatVideoExtras { CardShowStyle = 2 },
                    Bilibili = new BilibiliExtras
                    {
                        Tid = 21,
                        HumanType2 = 1015,
                        NoReprint = true,
                        Dynamic = "今天去看房",
                        Watermark = true,
                        SubtitleOpen = false,
                        SubtitleLan = "zh-CN",
                        UpSelectionReply = true,
                        ActReserve = false,
                        ActReserveTitle = "",
                        AiCover = 0,
                        IsAbCover = 0,
                        AbCoverInfo = "{}",
                        Interactive = false,
                        SpaceHidden = 2,
                        Dolby = false,
                        LosslessMusic = false,
                    },
                    Toutiao = new ToutiaoExtras
                    {
                        TemplateName = "tpl",
                        FilterName = "",
                        Commodity = "[]",
                        CompassVideo = "{}",
                        AttrsValueMap = "{}",
                        EtData = "{}",
                    },
                };
                await store.SaveDefaultsAsync(new Dictionary<string, PlatformParams> { [extrasKey] = extrasParams });
                var extrasBack = (await store.LoadDefaultsAsync())[extrasKey];
                Check("抖音 Extras 往返（裁剪尺寸/封面工具/原始 JSON）",
                    extrasBack.Douyin is { CoverCropWidth: 1051, CoverCropHeight: 788,
                        CoverToolsInfo: "{\"cover_type\":1}", Mentions: "[]", InteractionStickers: "[]" });
                Check("快手 Extras 往返（PK 封面/画质 AI/领域/短剧）",
                    extrasBack.Kuaishou is { PkCoverTimeStamp: -1, PkCoverType: 1, RiseQuality: false,
                        UseIdealTime: true, DisallowRecreation: true, Domain: "vlog", MovieId: "m-1" }
                    && extrasBack.NotifyFollowers == false);
                Check("小红书 Extras 往返（@/带货/速览合集/订单/章节同步）",
                    extrasBack.Xhs is { SketchCollectionId: "sk-1", ChapterSyncText: true,
                        Ats: "[]", OrderBind: "{\"order_id\":\"o-1\"}" });
                Check("视频号 Extras 往返（卡片样式）",
                    extrasBack.WechatVideo is { CardShowStyle: 2 });
                Check("B站 Extras 往返（分区/版权/水印/字幕/互动/预约/AI 封面/空间）",
                    extrasBack.Bilibili is { Tid: 21, HumanType2: 1015, NoReprint: true, Watermark: true,
                        SubtitleOpen: false, SubtitleLan: "zh-CN", UpSelectionReply: true,
                        Interactive: false, SpaceHidden: 2, Dolby: false, LosslessMusic: false });
                Check("头条 Extras 往返（模板/滤镜/商品/懂车帝/长尾属性）",
                    extrasBack.Toutiao is { TemplateName: "tpl", FilterName: "",
                        Commodity: "[]", CompassVideo: "{}", AttrsValueMap: "{}", EtData: "{}" });

                var extrasClone = extrasParams.Clone();
                extrasClone.Douyin!.CoverCropWidth = 999;
                extrasClone.Kuaishou!.PkCoverKey = "pk-2";
                extrasClone.Toutiao!.TemplateName = "tpl-2";
                Check("Extras Clone 隔离（改克隆不串原对象、未设置组仍为 null）",
                    extrasParams.Douyin!.CoverCropWidth == 1051
                    && extrasParams.Kuaishou!.PkCoverKey == "pk-1"
                    && extrasParams.Toutiao!.TemplateName == "tpl"
                    && new PlatformParams().Clone() is { Douyin: null, Kuaishou: null, Xhs: null,
                        WechatVideo: null, Bilibili: null, Toutiao: null });

                await File.WriteAllTextAsync(store.GetDraftsPath(), LegacyJson(false));
                var kept = await store.LoadDraftsAsync();
                Check("新档明确 false 不被旧覆盖回填",
                    kept.Count == 1 && !kept[0].IsOriginal
                    && kept[0].Overrides.TryGetValue(migratedKey, out var kp) && kp.IsOriginal == true);

                // 旧草稿的预设话题只存在 Overrides[].Topics —— 加载时并回全局列表，
                // 否则视频号（只读 draft.Topics）会静默漏发
                await File.WriteAllTextAsync(store.GetDraftsPath(), JsonSerializer.Serialize(new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["videoPath"] = alive,
                        ["title"] = "带预设话题",
                        ["overrides"] = new Dictionary<string, object?>
                        {
                            [PublishDraftStore.KeyOf(PlatformNames.Xiaohongshu, sid)] =
                                new Dictionary<string, object?>
                                {
                                    ["topics"] = new[] { new { name = "苏州房产", platformId = "t-1" } },
                                },
                        },
                    },
                }));
                var withTopics = await store.LoadDraftsAsync();
                Check("旧草稿的账号预设话题并入全局话题列表",
                    withTopics.Count == 1 && withTopics[0].Topics.Contains("苏州房产"),
                    string.Join("|", withTopics.FirstOrDefault()?.Topics ?? []));

                // ── 2.6.2 旧档迁移：结构化话题原先只存在 Topics（ResolvedTopic）里 ──
                // 不迁移的话 2.6.5 builder 读 TopicRefs 会把旧账号预设的话题静默退化成纯文本
                await File.WriteAllTextAsync(store.GetDraftsPath(), JsonSerializer.Serialize(new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["videoPath"] = alive,
                        ["title"] = "旧稿带已解析话题",
                        ["overrides"] = new Dictionary<string, object?>
                        {
                            [PublishDraftStore.KeyOf(PlatformNames.Douyin, sid)] =
                                new Dictionary<string, object?>
                                {
                                    ["topics"] = new[]
                                    {
                                        new { name = "家居", platformId = (string?)"1658571727441931", platformName = (string?)"家居" },
                                        new { name = "未命中", platformId = (string?)null, platformName = (string?)null },
                                    },
                                },
                        },
                    },
                }));
                var migratedTopics = await store.LoadDraftsAsync();
                var mtParams = migratedTopics.FirstOrDefault()?.Overrides
                    .GetValueOrDefault(PublishDraftStore.KeyOf(PlatformNames.Douyin, sid));
                Check("旧档 Topics 迁移出 TopicRefs（Source=legacy，未命中也保留为纯文本）",
                    mtParams is not null && mtParams.TopicRefs.Count == 2
                    && mtParams.TopicRefs[0] is { Name: "家居", Id: "1658571727441931",
                        Source: TopicRefSources.Legacy }
                    && mtParams.TopicRefs[1] is { Name: "未命中", Id: null },
                    $"{mtParams?.TopicRefs.Count ?? -1} 条");
                Check("迁移不删旧 Topics（旧版本软件仍能读）",
                    mtParams is not null && mtParams.Topics.Count == 2);

                // 旧预设走的是另一条加载路径（LoadDefaultsAsync），同样要迁移
                await File.WriteAllTextAsync(store.GetConfigPath(), JsonSerializer.Serialize(
                    new Dictionary<string, object?>
                    {
                        [PublishDraftStore.KeyOf(PlatformNames.Kuaishou, sid)] =
                            new Dictionary<string, object?>
                            {
                                ["topics"] = new[] { new { name = "老预设话题", platformId = "ks-1" } },
                            },
                    }));
                var presetMigrated = await store.LoadDefaultsAsync();
                Check("旧预设 Topics 也迁移出 TopicRefs",
                    presetMigrated.TryGetValue(PublishDraftStore.KeyOf(PlatformNames.Kuaishou, sid),
                        out var pmp)
                    && pmp.TopicRefs.Count == 1 && pmp.TopicRefs[0].Id == "ks-1"
                    && pmp.TopicRefs[0].Source == TopicRefSources.Legacy);
            }
            finally { try { File.Delete(alive); } catch { } }

            // ── 1.2 全局原创 → 小红书载荷（覆盖位优先，其余平台不映射）──
            var origTarget = new PublishTarget(PlatformNames.Xiaohongshu, Guid.NewGuid(), "主号");
            using (var doc = JsonDocument.Parse(
                PublishPayloadBuilder.Build(
                    new PublishDraft(@"D:\demo\original.mp4") { IsOriginal = true },
                    origTarget, new PlatformParams()).ExtraDataJson ?? "{}"))
            {
                Check("全局原创 → 小红书载荷 isOriginal=true",
                    doc.RootElement.GetProperty("isOriginal").GetBoolean());
            }
            using (var doc = JsonDocument.Parse(
                PublishPayloadBuilder.Build(
                    new PublishDraft(@"D:\demo\original.mp4") { IsOriginal = true },
                    origTarget, new PlatformParams { IsOriginal = false }).ExtraDataJson ?? "{}"))
            {
                Check("账号覆盖 false 优先于全局 true（兼容旧预设）",
                    !doc.RootElement.GetProperty("isOriginal").GetBoolean());
            }

            var unitOriginal = new PublishDraft(@"D:\demo\original.mp4");
            PublishDraftFactory.ApplyUnitParams(unitOriginal, PublishParamsFile.Parse("原创: 是"));
            Check("params.txt「原创」写进全局草稿", unitOriginal.IsOriginal);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        // ── 5. 视频元数据探测（ffmpeg/ffprobe 真实跑一遍）──
        sb.AppendLine("\n[5] 视频元数据探测（VideoMetaProbe）");
        Check("绿色版 ffmpeg 已就位", FfmpegCli.IsBundled,
            FfmpegCli.IsBundled ? "" : "!! 退化为 PATH 查找，探测会失败");

        var video = FindSampleVideo();
        if (video is null)
        {
            sb.AppendLine("  [SKIP] 未找到样例视频（仓库 video/ 目录）");
        }
        else
        {
            var probe = new VideoMetaProbe(Path.Combine(tempRoot, "meta"));
            var sw = Stopwatch.StartNew();
            var meta = await probe.ProbeAsync(video);
            sw.Stop();
            Check("探测返回结果", meta is not null, $"耗时 {sw.ElapsedMilliseconds}ms");
            if (meta is not null)
            {
                Check("时长 > 0", meta.DurationS > 0, $"{meta.DurationS:0.##}s");
                Check("宽高 > 0", meta.Width > 0 && meta.Height > 0, $"{meta.Width}x{meta.Height}");
                Check("大小 > 0", meta.SizeBytes > 0, meta.SizeText);
                Check("抽出首帧缩略图", meta.ThumbPath is not null && File.Exists(meta.ThumbPath),
                    meta.ThumbPath ?? "无");
                // 音轨探测（2026-11-09 新增，头条审核要求）：有结果就行（样本有无音轨都可能），
                // null = ffprobe 的 a:0 那一步没跑通
                Check("音轨探测有结果（true/false 均可）", meta.HasAudio is not null,
                    meta.HasAudio?.ToString() ?? "null");
                sb.AppendLine($"         → {Path.GetFileName(video)}: {meta.MetaText} · {meta.Width}x{meta.Height}"
                    + $" · 音轨={meta.HasAudio?.ToString() ?? "未知"}");

                var sw2 = Stopwatch.StartNew();
                var again = await probe.ProbeAsync(video);
                sw2.Stop();
                Check("二次探测命中缓存", again is not null && sw2.ElapsedMilliseconds < sw.ElapsedMilliseconds,
                    $"首次 {sw.ElapsedMilliseconds}ms → 二次 {sw2.ElapsedMilliseconds}ms");
            }
        }

        // ── 6. 账号顺序（默认序 + 用户自定义序）──
        sb.AppendLine("\n[6] 账号顺序（AccountOrder）—— 用户可自定义，见发布页「账号排序」");
        {
            var cfgs = new List<PlatformConfigItem>
            {
                new(PlatformNames.Douyin, "抖音", "#FF0049", "", 3),
                new(PlatformNames.Kuaishou, "快手", "#FF4906", "", 5),
                new(PlatformNames.Xiaohongshu, "小红书", "#FE2C55", "", 3),
                new(PlatformNames.WechatVideo, "视频号", "#07C160", "", 2),
            };

            // 故意造"乱"的原始序：登录时间与平台序完全不一致（服务端返回的就是这个）
            var dyMain = Sess(PlatformNames.Douyin, "抖音主号", primary: true, daysAgo: 9);
            var dySub = Sess(PlatformNames.Douyin, "抖音小号", primary: false, daysAgo: 1);
            var ksMain = Sess(PlatformNames.Kuaishou, "快手主号", primary: true, daysAgo: 8);
            var xhs = Sess(PlatformNames.Xiaohongshu, "小红书", primary: true, daysAgo: 7);
            var wx = Sess(PlatformNames.WechatVideo, "视频号", primary: true, daysAgo: 6);

            List<PlatformSessionItem> raw = [wx, xhs, ksMain, dyMain, dySub];
            var def = AccountOrder.Default(raw, cfgs);
            var defSeq = string.Join(",", def.Select(s => s.PlatformKey));

            Check("默认序不随登录时间变（= 平台配置序，抖音两条相邻）",
                defSeq == "douyin,douyin,kuaishou,xiaohongshu,wechatvideo", defSeq);
            Check("默认序 组内主号优先（主号 9 天前登录也压过 1 天前的非主号）",
                def.FindIndex(s => s.Id == dyMain.Id) < def.FindIndex(s => s.Id == dySub.Id));
            Check("空用户序 = 原样返回默认序",
                AccountOrder.Apply(def, []).Select(s => s.Id).SequenceEqual(def.Select(s => s.Id)));

            // 用户只排了一个「快手提到最前」—— 其余平台应保持默认的相对序
            List<string> userOrder = [AccountOrder.KeyOf(PlatformNames.Kuaishou, ksMain.Id)];
            var applied = AccountOrder.Apply(def, userOrder);
            var appSeq = string.Join(",", applied.Select(s => s.PlatformKey));
            Check("排过的组提到最前，没排过的保持默认相对序",
                appSeq == "kuaishou,douyin,douyin,xiaohongshu,wechatvideo", appSeq);

            // 新登录一个抖音号 —— 实测回归过：简单"追加到最后"会把抖音组劈成两半
            var dyNew = Sess(PlatformNames.Douyin, "新抖音号", primary: false, daysAgo: 0);
            List<PlatformSessionItem> raw2 = [.. raw, dyNew];
            var applied2 = AccountOrder.Apply(AccountOrder.Default(raw2, cfgs), userOrder);
            Check("新账号不打散分组（同平台的行必须连续）", IsGrouped(applied2),
                string.Join(",", applied2.Select(s => s.PlatformKey)));
            var newIdx = applied2.FindIndex(s => s.Id == dyNew.Id);
            Check("新账号落进所属平台组内（不是整表末尾）",
                newIdx > 0 && applied2[newIdx - 1].PlatformKey == PlatformNames.Douyin);
            Check("新账号没丢", applied2.Count == raw2.Count);

            // 顺序表里残留的已解绑 key 不能让人凭空消失
            List<string> stale = [.. userOrder, AccountOrder.KeyOf(PlatformNames.Xiaohongshu, Guid.NewGuid())];
            Check("残留的已解绑 key 被忽略（列表人数不变）",
                AccountOrder.Apply(def, stale).Count == def.Count);
        }

        // ── 8. params.txt 解析（PublishParamsFile）—— 全部喂字符串，不碰磁盘 ──
        sb.AppendLine("\n[8] params.txt 解析（PublishParamsFile）");
        {
            var basic = PublishParamsFile.Parse("标题: 夏日海边\n简介: 记录夏天");
            Check("基本键值对", basic.Title == "夏日海边" && basic.Description == "记录夏天"
                && basic.Problems.Count == 0);

            Check("中文冒号也认（中文输入法打出来就是这个）",
                PublishParamsFile.Parse("标题：夏日海边").Title == "夏日海边");

            var withTime = PublishParamsFile.Parse("发布时间: 2026-09-20 20:00");
            Check("值里的冒号只切第一刀（时间解析正确）",
                withTime.IsScheduled == true && withTime.ScheduledAt == new DateTime(2026, 9, 20, 20, 0, 0),
                $"{withTime.ScheduledAt}");

            var immediate = PublishParamsFile.Parse("发布时间: 立即");
            Check("「立即」→ 不定时且 ScheduledAt 为 null",
                immediate.IsScheduled == false && immediate.ScheduledAt is null);

            var badTime = PublishParamsFile.Parse("发布时间: 随便写");
            Check("时间认不出 → 不动定时 + 记一条警告（不能让预检整批拦下）",
                badTime.IsScheduled is null && badTime.ScheduledAt is null && badTime.Problems.Count == 1);

            Check("未知字段不致命（只记警告，将来模板加键旧版本不炸）",
                PublishParamsFile.Parse("颜色: 蓝").Problems.Count == 1);

            var malformed = PublishParamsFile.Parse("这行没冒号\n标题: x");
            Check("畸形行不致命（跳过它，其余照常）",
                malformed.Title == "x" && malformed.Problems.Count == 1);

            var emptyValue = PublishParamsFile.Parse("标题:\n简介: 有");
            Check("「标题:」空值按没写处理（绝不能落成空串 → 整批空标题）",
                emptyValue.Title is null && emptyValue.Description == "有" && emptyValue.Problems.Count == 1);

            var topics = PublishParamsFile.Parse("话题: #夏日, 海边\n话题: 旅行");
            Check("话题：去 #、多分隔符、多行追加",
                topics.Topics.SequenceEqual(["夏日", "海边", "旅行"]), string.Join("|", topics.Topics));

            // ── 2.6.2 话题ID / 官方活动（平台限定）──
            var structured = PublishParamsFile.Parse(
                "话题: 夏夜\n"
                + "话题ID: 抖音:夏夜=1658571727441931, 快手:夏夜=140808\n"
                + "话题ID: b站:夏夜=1098785\n"
                + "官方活动: 抖音:7702034615116174390\n"
                + "官方活动: B站:我在B站搞装修=4070872\n");
            Check("话题ID：平台前缀 + 名字=ID，多行追加",
                structured.TopicRefs.Count == 3
                && structured.TopicRefs[0] is
                {
                    PlatformKey: PlatformNames.Douyin,
                    Topic: { Name: "夏夜", Id: "1658571727441931", Source: TopicRefSources.Params },
                }
                && structured.TopicRefs[1] is { PlatformKey: PlatformNames.Kuaishou,
                    Topic: { Name: "夏夜", Id: "140808" } }
                && structured.TopicRefs[2] is { PlatformKey: PlatformNames.Bilibili,
                    Topic: { Name: "夏夜", Id: "1098785" } },
                string.Join("|", structured.TopicRefs.Select(t => $"{t.PlatformKey}:{t.Topic.Name}={t.Topic.Id}")));
            Check("官方活动：平台:ID 与 平台:名字=ID",
                structured.Activities.Count == 2
                && structured.Activities[0] is
                {
                    PlatformKey: PlatformNames.Douyin,
                    Activity: { Id: "7702034615116174390", Name: "7702034615116174390" },
                }
                && structured.Activities[1] is
                {
                    PlatformKey: PlatformNames.Bilibili,
                    Activity: { Id: "4070872", Name: "我在B站搞装修" },
                });
            Check("旧「话题」键与「话题ID」共存（名字进统一列表，ID 只落指定平台）",
                structured.Topics.SequenceEqual(["夏夜"]) && structured.Problems.Count == 0,
                string.Join("|", structured.Topics));

            var noPlatform = PublishParamsFile.Parse("话题ID: 家居=123");
            Check("话题ID 缺平台前缀 → 警告且不生效（猜平台会错发）",
                noPlatform.TopicRefs.Count == 0 && noPlatform.Problems.Count == 1);
            var badPlatform = PublishParamsFile.Parse("话题ID: 微博:家居=123");
            Check("话题ID 认不出的平台 → 警告",
                badPlatform.TopicRefs.Count == 0 && badPlatform.Problems.Count == 1
                && badPlatform.Problems[0].Reason.Contains("认不出平台"),
                badPlatform.Problems.FirstOrDefault()?.Reason ?? "(无)");

            // 内容级文件 → 平台级账号参数：按 target 的平台分发，不串号
            var structuredDraft = new PublishDraft(@"D:\demo\topic-refs.mp4");
            var dyTarget = new PublishTarget(PlatformNames.Douyin, Guid.NewGuid(), "抖音号");
            var ksTarget = new PublishTarget(PlatformNames.Kuaishou, Guid.NewGuid(), "快手号");
            structuredDraft.Targets.Add(dyTarget);
            structuredDraft.Targets.Add(ksTarget);
            PublishDraftFactory.ApplyUnitParams(structuredDraft, structured);
            var dyParams = structuredDraft.ParamsFor(dyTarget);
            var ksParams = structuredDraft.ParamsFor(ksTarget);
            Check("话题ID 只发给写出来的平台（抖音/快手各拿自己的 ID）",
                dyParams.TopicRefs.SingleOrDefault(t => t.Name == "夏夜")?.Id == "1658571727441931"
                && ksParams.TopicRefs.SingleOrDefault(t => t.Name == "夏夜")?.Id == "140808",
                $"抖音 {dyParams.TopicRefs.Count} / 快手 {ksParams.TopicRefs.Count}");
            Check("话题ID 的名字进全局话题列表且只一份",
                structuredDraft.Topics.Count(t => t == "夏夜") == 1,
                string.Join("|", structuredDraft.Topics));
            Check("官方活动只发给写出来的平台（B站没勾账号 → 不落）",
                dyParams.Activities.Count == 1 && dyParams.Activities[0].Id == "7702034615116174390"
                && ksParams.Activities.Count == 0);

            // 选活动自动带话题（2.6.4 UI 与 params 导入共用的 UpsertActivity）
            var actParams = new PlatformParams();
            actParams.UpsertActivity(new ActivityRef
            {
                Id = "act-1",
                Name = "活动",
                Topics =
                [
                    new TopicRef { Id = "t-1", Name = "活动话题" },
                    new TopicRef { Id = "t-2", Name = "另一个" },
                ],
            });
            Check("选活动自动把关联话题并入 TopicRefs（来源=activity）",
                actParams.Activities.Count == 1 && actParams.TopicRefs.Count == 2
                && actParams.TopicRefs.All(t => t.Source == TopicRefSources.Activity)
                && actParams.TopicRefs[0].Id == "t-1");
            actParams.UpsertActivity(new ActivityRef
            {
                Id = "act-1",
                Name = "活动改名",
                Topics = [new TopicRef { Id = "t-1", Name = "活动话题" }],
            });
            Check("同 ID 活动重复选中 → 替换快照、话题按名字去重",
                actParams.Activities.Count == 1 && actParams.Activities[0].Name == "活动改名"
                && actParams.TopicRefs.Count == 2);
            Check("活动已结束判定（过期 → 是；未来/无截止 → 否）",
                new ActivityRef { EndTime = DateTimeOffset.Now.AddDays(-1) }.IsEnded
                && !new ActivityRef { EndTime = DateTimeOffset.Now.AddDays(1) }.IsEnded
                && !new ActivityRef().IsEnded);

            var located = PublishParamsFile.Parse("位置: 苏州姑苏");
            Check("位置关键词可解析（导入后走联邦解析）",
                located.Location == "苏州姑苏" && located.Problems.Count == 0, located.Location ?? "(空)");

            var badEnum = PublishParamsFile.Parse("可见性: 随便写");
            Check("非法枚举回退默认值 + 不静默", badEnum.Visibility is null && badEnum.Problems.Count == 1);

            // 照着模板里「# 还可以写（取消注释即可）」把行首的 # 删掉 —— 尾注释就黏在值上了。
            // 这就是 2026-09-19 用户 `video/video/1/params.txt` 的真实形状：四行全废、
            // 界面上只表现为「没生效」，跟没写一模一样。
            var inline = PublishParamsFile.Parse(
                "可见性: 公开                # 公开 / 仅自己 / 仅互关好友可见 / 只给谁看 / 不给谁看\n"
                + "允许下载: 是                # 是 / 否\n"
                + "发布时间: 2026-09-25 20:00  # 不写 = 立即发布\n"
                + "内容声明: 虚构演绎            # 内容由 AI 生成 / 虚构演绎 / 内容为个人观点\n"
                + "原创: 是                    # 仅小红书");
            Check("尾注释不算值的一部分（模板「取消注释即可」必须真的可用）",
                inline.Visibility == PublishVisibility.Public && inline.AllowDownload == true
                && inline.IsScheduled == true && inline.ScheduledAt == new DateTime(2026, 9, 25, 20, 0, 0)
                && inline.Declaration == DeclarationKind.Fiction && inline.IsOriginal == true
                && inline.Problems.Count == 0,
                $"可见性 {inline.Visibility} / 声明 {inline.Declaration} / 定时 {inline.ScheduledAt}"
                + $" / 下载 {inline.AllowDownload} / 原创 {inline.IsOriginal} / 警告 {inline.Problems.Count}");

            Check("标题/简介里的 # 是正文，绝不能被当注释切掉",
                PublishParamsFile.Parse("标题: 夏日 #1\n简介: 记录夏天 #大自然").Description == "记录夏天 #大自然",
                PublishParamsFile.Parse("标题: 夏日 #1").Title ?? "(空)");

            Check("位置也切尾注释（POI 名字里不可能有 #）",
                PublishParamsFile.Parse("位置: 苏州姑苏   # 联邦解析：四个平台各拿一个").Location == "苏州姑苏",
                PublishParamsFile.Parse("位置: 苏州姑苏   # 联邦解析").Location ?? "(空)");

            // 合集写的是**名字**（软件里的逻辑合集），导入时按名字查已同步的映射
            var collected = PublishParamsFile.Parse("合集: 苏州房产   # 在「合集」页建好并同步到账号");
            Check("合集按名字解析（尾注释同样切掉）",
                collected.Collection == "苏州房产" && collected.Problems.Count == 0,
                collected.Collection ?? "(空)");

            var bom = PublishParamsFile.Parse("﻿标题: x\r\n简介: y\r\n");
            Check("BOM 与 CRLF 都认", bom.Title == "x" && bom.Description == "y");

            var blank = PublishParamsFile.Parse("");
            Check("空文件 = 全 null 且零警告（1/params.txt 就是 0 字节）",
                blank.Title is null && blank.Problems.Count == 0 && blank.Topics.Count == 0);

            // 标签唯一来源：编辑器里显示的每个标签，都必须能被 params.txt 原样写回来
            var visRound = Enum.GetValues<PublishVisibility>().All(v =>
                PublishParamsFile.Parse($"可见性: {VisibilityMapping.VisibilityLabel(v)}").Visibility == v);
            var declRound = Enum.GetValues<DeclarationKind>().All(k =>
                PublishParamsFile.Parse($"内容声明: {DeclarationMapping.DeclarationLabel(k)}").Declaration == k);
            Check("编辑器里的每个中文标签都能被 params.txt 解析回来（防标签漂移）",
                visRound && declRound, $"可见性 {(visRound ? "OK" : "!!")} / 声明 {(declRound ? "OK" : "!!")}");
        }

        // ── 9. 导入展开（PublishImportPlanner）—— 在 %TEMP% 造素材，**不碰用户的 publish-drafts.json** ──
        sb.AppendLine("\n[9] 导入展开（PublishImportPlanner）");
        var importRoot = Path.Combine(Path.GetTempPath(), "creatflow-import-" + Guid.NewGuid().ToString("N")[..8]);
        static void Touch(string path) => File.WriteAllText(path, "");
        try
        {
            // 夹具：展开只看扩展名与文件名，所以视频用 0 字节占位就够
            var looseDir = Path.Combine(importRoot, "散装视频");
            var unitsDir = Path.Combine(importRoot, "发布单元");
            var mixedDir = Path.Combine(importRoot, "混合");
            var unitDir = Path.Combine(unitsDir, "夏日海边");
            var legacyDir = Path.Combine(unitsDir, "旧写法");
            var declaredDir = Path.Combine(unitsDir, "声明过的单元");
            Directory.CreateDirectory(looseDir);
            Directory.CreateDirectory(unitDir);
            Directory.CreateDirectory(legacyDir);
            Directory.CreateDirectory(Path.Combine(declaredDir, "素材"));
            Touch(Path.Combine(looseDir, "夏日vlog.mp4"));
            Touch(Path.Combine(looseDir, "秋日.mp4"));
            Touch(Path.Combine(looseDir, "说明.txt"));                 // 非视频：不算数
            Touch(Path.Combine(unitDir, "夏日海边.mp4"));
            File.WriteAllText(Path.Combine(unitDir, PublishParamsFile.FileName), "标题: params 说了算");
            File.WriteAllText(Path.Combine(unitDir, "title.txt"), "title.txt 不该被用");
            Touch(Path.Combine(legacyDir, "旧写法.mp4"));
            File.WriteAllText(Path.Combine(legacyDir, "title.txt"), "旧标题");
            File.WriteAllText(Path.Combine(legacyDir, "desc.txt"), "旧简介");
            // 「声明过的单元」：自己既有 params.txt + 视频，又有个装着视频的子目录 —— 只算一条
            Touch(Path.Combine(declaredDir, "主片.mp4"));
            File.WriteAllText(Path.Combine(declaredDir, PublishParamsFile.FileName), "标题: 主片");
            Touch(Path.Combine(declaredDir, "素材", "花絮.mp4"));

            var loosePlan = PublishImportPlanner.Expand([looseDir]);
            Check("散装目录：视频文件被认出来（原来这里恒为 0 条）",
                loosePlan.Candidates.Count == 2, $"实际 {loosePlan.Candidates.Count} 条");
            Check("散装目录：非视频文件不算数",
                loosePlan.Candidates.All(c => c.VideoPath.EndsWith(".mp4") && c.SourceFolder is null));

            // 「容器 + 散装原件」同时存在 —— video/video 就是这种形状：
            // 只认子文件夹（散装原件是同一批视频的副本，两样都收会让每条进两次），但必须提示
            Touch(Path.Combine(unitsDir, "散装原件.mp4"));
            var unitPlan = PublishImportPlanner.Expand([unitsDir]);
            Check("容器目录：每个含视频的子目录各一条，散装视频不重复计",
                unitPlan.Candidates.Count == 3,
                $"实际 {unitPlan.Candidates.Count} 条（夏日海边 / 旧写法 / 声明过的单元）");
            Check("容器目录里的散装视频被跳过时**有提示**（不能静默丢）",
                unitPlan.Notices.Any(n => n.Contains("散装视频")), string.Join(" / ", unitPlan.Notices));

            var byTitle = unitPlan.Candidates.ToDictionary(c => c.Title);
            Check("params.txt 说了算（同目录的 title.txt 不再参与回退）",
                byTitle.ContainsKey("params 说了算") && !byTitle.ContainsKey("title.txt 不该被用"));
            Check("旧写法已移除：只有 title.txt/desc.txt 的目录回退到文件夹名、简介为空",
                byTitle.TryGetValue("旧写法", out var legacy)
                && legacy.Description is null && !byTitle.ContainsKey("旧标题"));
            Check("有 params.txt 的目录不展开子目录（它自己就是一条）",
                byTitle.ContainsKey("主片") && !byTitle.ContainsKey("花絮"));

            // params.txt 里没看懂的行必须浮到提示条 —— Problems 曾经「收了没人读」，
            // 用户写的行静静地不生效（2026-09-19：发布时间/内容声明就是这么丢的）
            var badDir = Path.Combine(importRoot, "写错的行");
            Directory.CreateDirectory(badDir);
            Touch(Path.Combine(badDir, "视频.mp4"));
            File.WriteAllText(Path.Combine(badDir, PublishParamsFile.FileName),
                "标题: 好的\n发布时间: 随便写\n可见性: 瞎写");
            var badPlan = PublishImportPlanner.Expand([badDir]);
            Check("params.txt 没看懂的行浮到提示条（带行号，不静默）",
                badPlan.Notices.Count(n => n.Contains($"{PublishParamsFile.FileName} 第")) == 2,
                string.Join(" / ", badPlan.Notices));
            Check("行里有问题也不挡导入（其余字段照常生效）",
                badPlan.Candidates.Count == 1 && badPlan.Candidates[0].Title == "好的");

            // 上限与守恒：13 条 → 只取 10，余量进待导入队列
            var many = new List<string>();
            for (var i = 1; i <= 13; i++)
            {
                // 故意不补零：字典序会排出 1,10,11,…,2，自然序才是 1,2,…,13
                var d = Path.Combine(importRoot, $"批量{i}");
                Directory.CreateDirectory(d);
                Touch(Path.Combine(d, $"第{i}条.mp4"));
                many.Add(d);
            }
            var all13 = PublishImportPlanner.Expand(many);
            Check("展开不截断：13 条全返回（截断在队列那一步，不然余量会在进队列前就丢）",
                all13.Candidates.Count == 13, $"实际 {all13.Candidates.Count} 条");

            var queue = new List<ImportCandidate>(all13.Candidates);
            var batch1 = PublishImportPlanner.TakeBatch(queue);
            Check("每次最多导 10 条", batch1.Count == PublishImportPlanner.ImportLimit,
                $"实际 {batch1.Count} / 上限 {PublishImportPlanner.ImportLimit}");
            Check("余量留在队列里（守恒：取走的 + 剩下的 == 总数，否则会出现「还有 3 条」却导不进东西）",
                batch1.Count + queue.Count == all13.Candidates.Count && queue.Count == 3,
                $"取走 {batch1.Count} + 剩下 {queue.Count} = 总数 {all13.Candidates.Count}");
            // 自然序：素材按 1..N 命名时，一批 10 条必须是 1..10，不能是 1,10,11,12,…,2
            Check("一批内保持素材原顺序且按自然序（1,2,…,10 而非字典序 1,10,11,…,2）",
                batch1[0].Title == "第1条" && batch1[9].Title == "第10条",
                $"{batch1[0].Title} … {batch1[9].Title}（共 {batch1.Count} 条）");
            var batch2 = PublishImportPlanner.TakeBatch(queue);
            Check("再取一批：剩下的 3 条取完、队列清空，空队列再取不报错",
                batch2.Count == 3 && queue.Count == 0 && PublishImportPlanner.TakeBatch(queue).Count == 0);

            var dup = PublishImportPlanner.Expand([looseDir, looseDir]);
            Check("去重：同一批里重复给的路径只算一条", dup.Candidates.Count == 2 && dup.Duplicates == 2,
                $"实际 {dup.Candidates.Count} 条 / 重复 {dup.Duplicates}");
            var excluded = PublishImportPlanner.Expand([looseDir],
                [Path.Combine(looseDir, "夏日vlog.mp4")]);
            Check("去重：已在工作台里的视频不再导入",
                excluded.Candidates.Count == 1 && excluded.Duplicates == 1);

            var mixedPlan = PublishImportPlanner.Expand([mixedDir]);
            Check("空目录 = 0 条且计入跳过、不抛", mixedPlan.Candidates.Count == 0 && mixedPlan.SkippedFolders == 1);

            var missing = PublishImportPlanner.Expand([Path.Combine(importRoot, "没这个目录")]);
            Check("不存在的路径：不抛、计入跳过并给出提示",
                missing.Candidates.Count == 0 && missing.SkippedFolders == 1 && missing.Notices.Count == 1);

            // 摘要文案：0 条时必须自己解释清楚（页面上什么都不会变，只说「导入 0 条」等于没说）
            var zero = PublishViewModel.SummarizeImport(new ImportPlan([], 0, 0, 0, []), 0);
            Check("0 条摘要带原因（不是干巴巴一句「导入 0 条」）", zero.Contains("没找到视频"), zero);
            Check("非 0 条摘要不出现「没找到视频」",
                !PublishViewModel.SummarizeImport(new ImportPlan([], 0, 0, 0, []), 3).Contains("没找到视频"));

            var rich = PublishViewModel.SummarizeImport(new ImportPlan([], 2, 1, 3, []), 10, 2);
            Check("剩余 / 跳过 / 警告 / 重复都写进摘要",
                rich.Contains("还有 2 条未导入") && rich.Contains("跳过 2 项")
                && rich.Contains("1 条有警告") && rich.Contains("跳过 3 条重复"), rich);
        }
        finally
        {
            try { Directory.Delete(importRoot, recursive: true); } catch { }
        }

        // ── 10. 模板文件夹（PublishUnitTemplate + ShellLauncher）──
        sb.AppendLine("\n[10] 模板文件夹（PublishUnitTemplate）");
        var tplHome = Path.Combine(Path.GetTempPath(), "creatflow-tpl-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tplHome);
            var first = PublishUnitTemplate.Write(tplHome);
            Check("首次生成：两个文件都写了", first.Error is null && first.Written.Count == 2,
                first.Error ?? string.Join("|", first.Written));
            Check("params.txt 与 说明.txt 都在且非空",
                new FileInfo(Path.Combine(first.Folder, PublishParamsFile.FileName)).Length > 0
                && new FileInfo(Path.Combine(first.Folder, PublishUnitTemplate.ReadmeFileName)).Length > 0);

            // 模板自己不能带警告 —— 带警告的模板等于教用户写错
            var tpl = PublishParamsFile.TryRead(first.Folder);
            Check("模板的 params.txt 解析零警告（自洽）",
                tpl is not null && tpl.Problems.Count == 0,
                tpl is null ? "读不到" : string.Join(" / ", tpl.Problems.Select(p => p.Reason)));
            Check("模板默认值读得回来（公开 / 允许下载 / 立即 / 不声明）",
                tpl!.Visibility == PublishVisibility.Public && tpl.AllowDownload == true
                && tpl.IsScheduled == false && tpl.Declaration == DeclarationKind.None);
            Check("模板列出 话题ID / 官方活动 高级写法（2.6.2）",
                PublishUnitTemplate.ParamsText.Contains("# 话题ID: ", StringComparison.Ordinal)
                && PublishUnitTemplate.ParamsText.Contains("# 官方活动: ", StringComparison.Ordinal)
                && tpl.TopicRefs.Count == 0 && tpl.Activities.Count == 0);

            var second = PublishUnitTemplate.Write(tplHome);
            Check("重复生成不覆盖、也不堆 -2/-3（幂等）",
                second.AllKept && second.Folder == first.Folder && second.Written.Count == 0);

            // ⚠️ 只喂不存在的路径：真实路径会真的弹出资源管理器，自检不能扰民
            Check("打开不存在的目录返回 false 且不抛",
                !ShellLauncher.RevealFolder(Path.Combine(tplHome, "没这个目录")));
        }
        finally
        {
            try { Directory.Delete(tplHome, recursive: true); } catch { }
        }

        // ── 11. 提交前的视频哈希（VideoHasher）—— 服务端对 video_hash 是硬校验 ──
        sb.AppendLine("\n[11] 视频哈希（VideoHasher）");
        {
            var f = Path.Combine(Path.GetTempPath(), "creatflow-hash-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
            File.WriteAllText(f, "abc");
            try
            {
                // 标准向量：SHA-256("abc")。写死期望值是为了挡住「算法被换掉/大小写变了」
                // 这类改动静默漂移 —— 服务端存的是同一份数据契约
                var hash = await VideoHasher.Sha256Async(f);
                Check("SHA-256 小写 hex（对得上标准向量）",
                    hash == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);

                // 2026-09-19：NewApp 一直送空串 → 服务端 PublishTask.Create 的
                // ThrowIfNullOrWhiteSpace(videoHash) 把每一条提交都拒了（页面上只看得到「失败 N 条」）
                Check("哈希非空且 64 字符（服务端拒空串）", hash.Length == 64 && !string.IsNullOrWhiteSpace(hash));
            }
            finally { try { File.Delete(f); } catch { } }
        }

        // ── 12. 上传队列的状态机（QueuePlatformRow / QueueTaskViewModel）──
        // 纯对象断言：不碰网络、不碰用户的发布记录（队列页跑起来会真上传，自检绝不能碰）
        sb.AppendLine("\n[12] 上传队列状态机");
        {
            var taskId = Guid.NewGuid();
            var row = Plat(taskId, Guid.NewGuid(), status: 0);

            // 服务端只知道「待发布」；「暂停」与「正在跑」是本地概念 —— 同步不能把它们盖掉
            // （老版栽过：点了暂停三秒后自己又跑起来；长视频跨过轮询点被重复认领 → 并发重复上传）
            row.ApplyLocal(QueueRowState.Paused, 0, "已暂停");
            row.ApplyServer(0, 0, null);
            Check("本地暂停不被服务端 Pending 打回等待", row.State == QueueRowState.Paused, row.StateText);

            var running = Plat(taskId, Guid.NewGuid(), status: 0);
            running.ApplyLocal(QueueRowState.Uploading, 42, "12.4 / 29.1 MB");
            running.ApplyServer(0, 0, null);
            Check("执行中的行不被服务端同步清零进度",
                running.State == QueueRowState.Uploading && running.ProgressPct == 42);

            var fromServer = Plat(taskId, Guid.NewGuid(), status: 4, pct: 100, publishedAt: DateTime.Now);
            Check("服务端已发布 → 已发布（进度补满）",
                fromServer.State == QueueRowState.Published && fromServer.ProgressPct == 100);

            var failed = Plat(taskId, Guid.NewGuid(), status: 5, error: "标题含平台限制词");
            Check("服务端失败 → 失败 + 原因进详情行",
                failed.IsFailed && failed.DetailText == "标题含平台限制词", failed.DetailText);

            // 聚合（按视频）
            var t2 = new QueueTaskViewModel(taskId, @"D:\不存在\夏日海边vlog.mp4",
                "夏日海边vlog.mp4", "夏日海边vlog", DateTime.Now);
            t2.Rows.Add(Plat(taskId, Guid.NewGuid(), status: 0));
            t2.Rows.Add(Plat(taskId, Guid.NewGuid(), status: 0));
            t2.RaiseDerived();
            Check("全等待 → 「等待中」", t2.StatusText == "等待中", t2.StatusText);

            t2.Rows[0].ApplyServer(4, 100, null);
            t2.Rows[1].ApplyServer(5, 0, "失败了");
            t2.RaiseDerived();
            Check("一成一败 → 「部分失败」+ 计数行",
                t2.StatusText == "部分失败" && t2.CountText == "1 成功 · 1 失败" && t2.HasFailure,
                $"{t2.StatusText} / {t2.CountText}");

            t2.Rows[1].ApplyServer(4, 100, null);
            t2.RaiseDerived();
            Check("全成功 → 「已全部完成」+ 「2 / 2 成功」+ 可隐藏",
                t2.StatusText == "已全部完成" && t2.CountText == "2 / 2 成功" && t2.IsDone
                && !t2.HasFailure, $"{t2.StatusText} / {t2.CountText}");

            Check("视频不在了就不显示大小（不假装知道）",
                !t2.MetaText.Contains("MB") && t2.MetaText.Contains("2 个平台"), t2.MetaText);

            var t3Id = Guid.NewGuid();
            var t3 = new QueueTaskViewModel(t3Id, @"D:\不存在\x.mp4", "x.mp4", null, DateTime.Now);
            t3.Rows.Add(Plat(t3Id, Guid.NewGuid(), status: 1, pct: 30));
            t3.RaiseDerived();
            Check("有行在跑 → 「进行中」", t3.StatusText == "进行中", t3.StatusText);
        }

        // ── 13. 抖音话题 text_extra 的偏移量（2026-09-20 实测踩过的坑）──
        // 手拼的那份把 start/end 写成 0，抖音拿它们去正文里核对 #话题 的位置，对不上就整条拒：
        // 「status_code=35，视频描述参数不合法」。这里把坐标钉死，防止再退回 0。
        sb.AppendLine("\n[13] 抖音话题偏移量（text_extra）");
        {
            var title = "美丽的大自然";
            var desc = "美丽的大自然 #大自然";
            var d = new PublishDraft(@"D:\demo\douyin-topic.mp4")
            {
                Title = title,
                Description = desc,
            };
            d.Topics.Add("大自然");

            var p = new PlatformParams
            {
                Topics = [new ResolvedTopic("大自然", "1568360053346369", "大自然")],
            };
            var dto = PublishPayloadBuilder.Build(d,
                new PublishTarget(PlatformNames.Douyin, Guid.NewGuid(), "主号"), p);

            using var doc = JsonDocument.Parse(dto.ExtraDataJson ?? "{}");
            var textExtraJson = doc.RootElement.GetProperty("textExtra").GetString() ?? "";
            using var extras = JsonDocument.Parse(textExtraJson);
            var first = extras.RootElement[0];

            // item.common.text = 标题 + " " + 简介；#大自然 就在那里
            var text = $"{title} {desc}";
            var expectedStart = text.IndexOf("#大自然", StringComparison.Ordinal);
            var expectedCaptionStart = desc.IndexOf("#大自然", StringComparison.Ordinal);

            Check("start/end 是**正文里的真实偏移**（不是 0）",
                first.GetProperty("start").GetInt32() == expectedStart
                && first.GetProperty("end").GetInt32() == expectedStart + 4,
                $"实际 {first.GetProperty("start").GetInt32()}..{first.GetProperty("end").GetInt32()}"
                + $" / 期望 {expectedStart}..{expectedStart + 4}");

            Check("caption_start/caption_end 用简介的坐标",
                first.GetProperty("caption_start").GetInt32() == expectedCaptionStart,
                $"实际 {first.GetProperty("caption_start").GetInt32()} / 期望 {expectedCaptionStart}");
            Check("hashtag_id 是数字 cid",
                first.GetProperty("hashtag_id").GetInt64() == 1568360053346369);

            var challenges = doc.RootElement.GetProperty("challenges").GetString() ?? "";
            using var chDoc = JsonDocument.Parse(challenges);
            Check("challenges 是**数字数组**（不是对象数组）",
                chDoc.RootElement.ValueKind == JsonValueKind.Array
                && chDoc.RootElement[0].ValueKind == JsonValueKind.Number
                && chDoc.RootElement[0].GetInt64() == 1568360053346369,
                challenges);

            // 不可见字符（NBSP / 零宽）必须清掉：它们占字符位、却看不见，
            // 抖音的 text_extra 是拿偏移量核对位置的 —— 老版对四个平台都清（SanitizeText）
            var dirty = new PublishDraft(@"D:\demo\douyin-dirty.mp4")
            {
                Title = "美丽的\u00A0大自然",            // NBSP：中文输入法常见
                Description = "美丽的大自然\u200B #大自然", // 零宽空格
            };
            dirty.Topics.Add("大自然");
            var dirtyDto = PublishPayloadBuilder.Build(dirty,
                new PublishTarget(PlatformNames.Douyin, Guid.NewGuid(), "主号"), p);

            // NBSP 的规则是**转成普通空格**（不是删掉）—— 它通常就是当空格用的，
            // 删了会把「美丽的 大自然」粘成「美丽的大自然」；零宽字符才是直接删（老版同款）
            Check("标题/简介里的不可见字符被清掉（NBSP → 空格、零宽 → 删除）",
                dirtyDto.Title == "美丽的 大自然" && dirtyDto.Description == "美丽的大自然 #大自然",
                $"标题「{dirtyDto.Title}」简介「{dirtyDto.Description}」");
            using var dirtyDoc = JsonDocument.Parse(dirtyDto.ExtraDataJson ?? "{}");
            using var dirtyExtras = JsonDocument.Parse(
                dirtyDoc.RootElement.GetProperty("textExtra").GetString() ?? "[]");
            var de = dirtyExtras.RootElement[0];
            var dirtyExpected = (dirtyDto.Title + " " + dirtyDto.Description)
                .IndexOf("#大自然", StringComparison.Ordinal);
            Check("清完不可见字符后偏移量仍精确指向 #话题（清与算同一份文本）",
                de.GetProperty("start").GetInt32() == dirtyExpected,
                $"实际 {de.GetProperty("start").GetInt32()} / 期望 {dirtyExpected}");
        }

        // ── 14. 话题进正文（2026-09-20：params.txt 起不再把 # 写在简介里）──
        // 话题在线上**不是一个独立字段**：抖音要正文里真有那个 `#话题`，text_extra 才落得下去。
        // 这里把四条钉死：没写要追加 / 写过不重复 / 抖音删掉没解析出 ID 的 / 其余平台不删。
        sb.AppendLine("\n[14] 话题进正文");
        {
            var dy = new PublishTarget(PlatformNames.Douyin, Guid.NewGuid(), "主号");
            var ks = new PublishTarget(PlatformNames.Kuaishou, Guid.NewGuid(), "主号");
            var xhs = new PublishTarget(PlatformNames.Xiaohongshu, Guid.NewGuid(), "主号");

            var resolved = new PlatformParams
            {
                Topics = [new ResolvedTopic("大自然", "1568360053346369", "大自然")],
            };
            var noId = new PlatformParams
            {
                Topics = [new ResolvedTopic("这个平台没有的话题", null, null)],
            };

            string InlineCaptionOf(PublishDraft d, PublishTarget t, PlatformParams pp)
            {
                using var doc = JsonDocument.Parse(
                    PublishPayloadBuilder.Build(d, t, pp).ExtraDataJson ?? "{}");
                return doc.RootElement.GetProperty("caption").GetString() ?? "";
            }

            var plain = new PublishDraft(@"D:\demo\t14-plain.mp4")
            {
                Title = "美丽的大自然",
                Description = "美丽的大自然",
            };
            Check("抖音：简介里没写 # → 已解析的话题追加到正文末尾",
                InlineCaptionOf(plain, dy, resolved) == "美丽的大自然 #大自然",
                InlineCaptionOf(plain, dy, resolved));

            // 追加完正文里真有那个 # 了 —— text_extra 的坐标必须精确指回它
            // （坐标是以 item.common.text = 标题 + 空格 + 正文 为坐标系的）
            var plainDto = PublishPayloadBuilder.Build(plain, dy, resolved);
            using var plainDoc = JsonDocument.Parse(plainDto.ExtraDataJson ?? "{}");
            using var plainExtras = JsonDocument.Parse(
                plainDoc.RootElement.GetProperty("textExtra").GetString() ?? "[]");
            var pe = plainExtras.RootElement[0];
            var peText = (plainDto.Title ?? "") + " " + (plainDto.Description ?? "");
            var peStart = pe.GetProperty("start").GetInt32();
            var peEnd = pe.GetProperty("end").GetInt32();
            Check("追加进正文的 #话题 与 text_extra 对得上（start/end 就指向它）",
                peStart >= 0 && peEnd <= peText.Length
                && peText[peStart..peEnd] == "#大自然",
                $"正文「{peText}」{peStart}..{peEnd} 取到「{(peEnd <= peText.Length ? peText[peStart..peEnd] : "")}」");

            var inline = new PublishDraft(@"D:\demo\t14-inline.mp4")
            {
                Title = "美丽的大自然",
                Description = "美丽的大自然 #大自然",   // 老写法：话题直接写在简介里
            };
            Check("简介里已经写了这个 #话题 → 不重复追加",
                InlineCaptionOf(inline, dy, resolved) == "美丽的大自然 #大自然",
                InlineCaptionOf(inline, dy, resolved));

            var stale = new PublishDraft(@"D:\demo\t14-stale.mp4")
            {
                Title = "美丽的大自然",
                Description = "美丽的大自然 #大自然",
            };
            Check("抖音：正文里没解析出 ID 的 #xxx 被删掉（留着它=status_code 35 整条拒收）",
                InlineCaptionOf(stale, dy, noId) == "美丽的大自然",
                InlineCaptionOf(stale, dy, noId));
            Check("快手：同一个 #xxx 不删（别的平台不拒，那是用户自己写的字）",
                InlineCaptionOf(stale, ks, noId) == "美丽的大自然 #大自然",
                InlineCaptionOf(stale, ks, noId));
            Check("小红书：同一个 #xxx 不删（保留用户自己写的字）",
                InlineCaptionOf(stale, xhs, noId) == "美丽的大自然 #大自然",
                InlineCaptionOf(stale, xhs, noId));
            Check("小红书：简介里没写 # → 已解析的话题照样追加",
                InlineCaptionOf(plain, xhs, resolved) == "美丽的大自然 #大自然",
                InlineCaptionOf(plain, xhs, resolved));

            var blank = new PublishDraft(@"D:\demo\t14-blank.mp4") { Title = "只有标题", Description = "" };
            Check("简介空着的时候，正文就是话题本身（不留前导空格）",
                InlineCaptionOf(blank, dy, resolved) == "#大自然",
                InlineCaptionOf(blank, dy, resolved));

            // ── 话题单一来源（2026-11-06）：预设话题必须进 draft.Topics ──
            // 视频号载荷只读 draft.Topics —— 以前预设话题留在 PlatformParams.Topics，
            // 抖音/快手/小红书发得出去、视频号静默漏发，编辑器顶部也看不到芯片。
            var wxSid = Guid.NewGuid();
            var wxTarget = new PublishTarget(PlatformNames.WechatVideo, wxSid, "主号");
            var seeded = new PublishDraft(@"D:\demo\t14-seed.mp4") { Title = "预设话题" };
            PublishDraftFactory.SeedTargets(seeded, [wxTarget], new Dictionary<string, PlatformParams>
            {
                [PublishDraftStore.KeyOf(PlatformNames.WechatVideo, wxSid)] = new()
                {
                    Topics = [new ResolvedTopic("苏州房产", null, null)],
                },
            });
            Check("账号预设话题并入草稿统一列表", seeded.Topics.Contains("苏州房产"));

            using (var doc = JsonDocument.Parse(
                PublishPayloadBuilder.Build(seeded, wxTarget, seeded.ParamsFor(wxTarget))
                    .ExtraDataJson ?? "{}"))
            {
                Check("视频号载荷拿到预设话题（以前只进四平台）",
                    doc.RootElement.GetProperty("topics").EnumerateArray()
                        .Any(e => e.GetString() == "苏州房产"));
            }

            // 重复话题不重复加（预设与用户输入同名）
            var dedup = new PublishDraft(@"D:\demo\t14-dedup.mp4") { Title = "去重" };
            dedup.Topics.Add("苏州房产");
            PublishDraftFactory.SeedTargets(dedup, [wxTarget], new Dictionary<string, PlatformParams>
            {
                [PublishDraftStore.KeyOf(PlatformNames.WechatVideo, wxSid)] = new()
                {
                    Topics = [new ResolvedTopic("苏州房产", null, null)],
                },
            });
            Check("预设与手输同名话题只保留一份", dedup.Topics.Count(t => t == "苏州房产") == 1);
        }

        // ── 15. 定时发布窗口（2 小时 ~ 14 天；老版 HomeViewModel.cs:936-966 规则照搬）──
        sb.AppendLine("\n[15] 定时发布窗口（2 小时 ~ 14 天）");
        var schedFile = Path.Combine(Path.GetTempPath(), "newapp-selftest-t15.mp4");
        File.WriteAllText(schedFile, "x");
        try
        {
            PublishDraft Sched(DateTime? at)
            {
                var d = new PublishDraft(schedFile)
                {
                    Title = "定时窗口用例",
                    IsScheduled = at is not null,
                    ScheduledAt = at,
                };
                d.Targets.Add(new PublishTarget(PlatformNames.Douyin, Guid.NewGuid(), "主号"));
                return d;
            }

            var issuesPast = PublishPreflight.Validate(Sched(DateTime.Now.AddMinutes(-5)));
            Check("定时时间已过 → 「已过」Error",
                issuesPast.Any(i => i.Severity == PreflightSeverity.Error && i.Message.Contains("已过")));

            var issuesLow = PublishPreflight.Validate(Sched(DateTime.Now.AddMinutes(119)));
            Check("早于 2 小时 → 「不能早于当前时间 2 小时」Error",
                issuesLow.Any(i => i.Severity == PreflightSeverity.Error
                    && i.Message.Contains("不能早于当前时间 2 小时")),
                string.Join("；", issuesLow.Select(i => i.Message)));

            var issuesEdgeLow = PublishPreflight.Validate(Sched(DateTime.Now.AddHours(2).AddSeconds(3)));
            Check("刚过 2 小时（+3s，避开秒级赛跑）→ 不报该错",
                issuesEdgeLow.All(i => !i.Message.Contains("不能早于")),
                string.Join("；", issuesEdgeLow.Select(i => i.Message)));

            var issuesHigh = PublishPreflight.Validate(Sched(DateTime.Now.AddDays(14).AddMinutes(1)));
            Check("超过 14 天 → 「不能超过 14 天」Error",
                issuesHigh.Any(i => i.Severity == PreflightSeverity.Error
                    && i.Message.Contains("不能超过 14 天")));

            var issuesEdgeHigh = PublishPreflight.Validate(Sched(DateTime.Now.AddDays(14).AddSeconds(-2)));
            Check("略低于 14 天边界（-2s）→ 不报该错",
                issuesEdgeHigh.All(i => !i.Message.Contains("不能超过 14 天")),
                string.Join("；", issuesEdgeHigh.Select(i => i.Message)));

            var issuesImmediate = PublishPreflight.Validate(Sched(null));
            Check("立即发布 → 两条窗口规则都不出",
                issuesImmediate.All(i => !i.Message.Contains("2 小时") && !i.Message.Contains("14 天")));
        }
        finally { File.Delete(schedFile); }

        var slotFixed = PublishPreflight.NextScheduleSlot(new DateTime(2026, 1, 1, 10, 37, 0));
        Check("NextScheduleSlot：10:37 → 13:00（now+2h 后向上取整到整点）",
            slotFixed == new DateTime(2026, 1, 1, 13, 0, 0), slotFixed.ToString("yyyy-MM-dd HH:mm"));
        var slotLate = PublishPreflight.NextScheduleSlot(new DateTime(2026, 1, 1, 23, 30, 0));
        Check("NextScheduleSlot 跨天：23:30 → 次日 02:00",
            slotLate == new DateTime(2026, 1, 2, 2, 0, 0), slotLate.ToString("yyyy-MM-dd HH:mm"));
        var slotBase = DateTime.Now;
        var slotNow = PublishPreflight.NextScheduleSlot(slotBase);
        Check("NextScheduleSlot（真实 now）：≥ now+2h 且分秒为 0",
            slotNow >= slotBase.AddHours(2) && slotNow.Minute == 0 && slotNow.Second == 0);

        // ── 16. 抖音转载预检（选了「抖音作品」却没挑具体作品 = from_item_id 为空，必发不出去）──
        sb.AppendLine("\n[16] 抖音转载预检");
        var repostFile = Path.Combine(Path.GetTempPath(), "newapp-selftest-t16.mp4");
        File.WriteAllText(repostFile, "x");
        try
        {
            PublishDraft Repost(bool fromInside, string? itemId)
            {
                var d = new PublishDraft(repostFile) { Title = "转载预检用例" };
                d.Targets.Add(new PublishTarget(PlatformNames.Douyin, Guid.NewGuid(), "主号"));
                d.Declaration.Kind = DeclarationKind.Repost;
                d.Declaration.DouyinRepostFromInside = fromInside;
                d.Declaration.DouyinRepostItemId = itemId;
                return d;
            }

            var repostMissing = PublishPreflight.Validate(Repost(true, null));
            Check("转载 + 抖音作品 + 空 ID → Error 点名抖音",
                repostMissing.Any(i => i.PlatformKey == PlatformNames.Douyin
                    && i.Severity == PreflightSeverity.Error
                    && i.Message.Contains("还没选具体作品")),
                string.Join("；", repostMissing.Select(i => i.Message)));

            var repostFilled = PublishPreflight.Validate(Repost(true, "7123456789012345678"));
            Check("填了作品 ID → 不再报该错",
                repostFilled.All(i => !i.Message.Contains("还没选具体作品")));

            var repostOutside = PublishPreflight.Validate(Repost(false, null));
            Check("来源选「站外」→ 不报该错",
                repostOutside.All(i => !i.Message.Contains("还没选具体作品")));
        }
        finally { File.Delete(repostFile); }

        // ── 17. 环境快照解析（extra_data 里 env 是字符串编码的 JSON）──
        // 2026-10-16 修的坑：曾把 Extra() 反序列化出的 JsonElement 当 string 判断（`is not string`），
        // 判断永远不成立 → 快照永远读不出 → 全部请求 fallback 硬编码 UA。这组断言钉住解析口径。
        sb.AppendLine("\n[17] 环境快照解析（CredentialFields.EnvironmentSnapshot）");
        var envInner = "{\"userAgent\":\"Mozilla/5.0 TestUA Edg/155\",\"secChUa\":\"\\\"Microsoft Edge\\\";v=\\\"155\\\"\"}";
        var envStringForm = "{\"env\":" + System.Text.Json.JsonSerializer.Serialize(envInner) + ",\"csrfToken\":\"t\"}";
        var snapFromString = CredentialFields.EnvironmentSnapshot(envStringForm);
        Check("env 字符串编码（真实落库形状）能解析出快照",
            snapFromString is { IsValid: true } && snapFromString.UserAgent == "Mozilla/5.0 TestUA Edg/155",
            snapFromString?.UserAgent ?? "(null)");
        Check("env 对象编码也兼容（同一 ToString 口径）",
            CredentialFields.EnvironmentSnapshot("{\"env\":" + envInner + "}") is { IsValid: true });
        Check("缺 env → null（回落硬编码 UA）",
            CredentialFields.EnvironmentSnapshot("{\"csrfToken\":\"t\"}") is null);
        Check("env 空串 → null", CredentialFields.EnvironmentSnapshot("{\"env\":\"\"}") is null);
        Check("env 是 null 值 → null", CredentialFields.EnvironmentSnapshot("{\"env\":null}") is null);
        Check("快照缺 secChUa → null（IsValid 不成立）",
            CredentialFields.EnvironmentSnapshot(
                "{\"env\":" + System.Text.Json.JsonSerializer.Serialize("{\"userAgent\":\"UA\"}") + "}") is null);

        // ── 18. 回填行的「换」（2026-10-22 用户反馈：导入文件夹后小红书话题匹配错，行上没有「换」改不了）──
        sb.AppendLine("\n[18] 话题/位置结果行的「换」（回填行按需现拉候选）");

        TopicResolutionRow TopicRow(
            string platform = PlatformNames.Xiaohongshu, bool needsSearch = true, bool found = true) => new()
        {
            Keyword = "狗狗冷知识",
            PlatformKey = platform,
            SessionId = Guid.NewGuid(),
            DisplayName = PlatformNames.GetDisplayName(platform),
            BrandHex = "#FF0049",
            NeedsSearch = needsSearch,
            Found = found,
        };

        var restored = TopicRow();
        restored.SeedCurrent("xhs-topic-id", "亲近大自然");
        Check("回填行（候选没拉过）也给「换」", restored.ShowSwap);
        Check("回填行带着已采用值（现拉时靠它保住已选项）",
            restored.TopicId == "xhs-topic-id" && restored.TopicName == "亲近大自然");

        // 候选只有一个 = 没什么可换的 → 收起入口（与老口径一致）
        var single = TopicRow();
        single.SetCandidates(
            [new TopicCandidate("only", "#狗狗冷知识", "", "狗狗冷知识")], "only");
        Check("拉过候选且只有一个 → 不给「换」",
            !single.ShowSwap && single.CandidatesLoaded && single.TopicId == "only");

        var multi = TopicRow();
        multi.SetCandidates(
        [
            new TopicCandidate("a", "#狗狗冷知识", "", "狗狗冷知识"),
            new TopicCandidate("b", "#亲近大自然", "", "亲近大自然"),
        ], "b");
        Check("拉回多个候选 → 给「换」", multi.ShowSwap);
        Check("keepId 命中 → 保持已采用的那个（展开候选不会把结果顶掉）",
            multi.Selected?.Id == "b" && multi.TopicId == "b" && multi.TopicName == "亲近大自然");

        var gone = TopicRow();
        gone.SetCandidates([new TopicCandidate("a", "#亲近大自然", "", "亲近大自然")], "gone");
        Check("keepId 不在候选里 → 退回第一个", gone.Selected?.Id == "a");

        Check("视频号（纯文本、不搜）不给「换」",
            !TopicRow(PlatformNames.WechatVideo, needsSearch: false).ShowSwap);
        Check("未命中的行不给「换」（走「重搜」）", !TopicRow(found: false).ShowSwap);

        var poiRestored = new PoiResolutionRow
        {
            Keyword = "外滩",
            PlatformKey = PlatformNames.Douyin,
            SessionId = Guid.NewGuid(),
            DisplayName = PlatformNames.GetDisplayName(PlatformNames.Douyin),
            BrandHex = "#000000",
        };
        poiRestored.SeedCurrent(new PoiRef("poi-1", "外滩", "上海市黄浦区"));
        Check("位置回填行同样给「换」并保住当前值",
            poiRestored.ShowSwap && poiRestored.Found && poiRestored.Value?.PoiId == "poi-1");

        // 空关键词直接返回 —— 不发任何请求（自检零网络）
        var blankRow = new TopicResolutionRow
        {
            Keyword = "",
            PlatformKey = PlatformNames.Xiaohongshu,
            SessionId = Guid.NewGuid(),
            DisplayName = "小红书",
            BrandHex = "#FF0049",
        };
        var blankResult = await new PublishResolver().LoadCandidatesAsync(blankRow, isTopic: true);
        Check("空关键词的「换」直接返回空（不发请求）", blankResult.Count == 0);

        // ── 19. 快手话题标签上限（2026-10-22 实测：4 个话题 submit 直接拒
        //        result=1000「话题标签数量超过上限：4」，上限 = 3）──
        sb.AppendLine("\n[19] 快手话题标签上限（最多 3 个）");
        {
            var ks = new PublishTarget(PlatformNames.Kuaishou, Guid.NewGuid(), "主号");
            var xhs = new PublishTarget(PlatformNames.Xiaohongshu, Guid.NewGuid(), "主号");

            var fiveTopics = new PlatformParams
            {
                Topics =
                [
                    new ResolvedTopic("狗狗冷知识", "140808", "狗狗冷知识"),
                    new ResolvedTopic("狗狗健康", "140809", "狗狗健康"),
                    new ResolvedTopic("养狗经验", "140810", "养狗经验"),
                    new ResolvedTopic("萌宠", "140811", "萌宠"),
                    new ResolvedTopic("宠物知识", "140812", "宠物知识"),
                ],
            };

            var body = new PublishDraft(@"D:\demo\t19-kuaishou.mp4")
            {
                Title = "狗的鼻子为什么总是湿的",
                Description = "狗鼻子湿是正常现象。",
            };

            JsonDocument Payload(PublishDraft d, PublishTarget t, PlatformParams pp) =>
                JsonDocument.Parse(PublishPayloadBuilder.Build(d, t, pp).ExtraDataJson ?? "{}");

            using var ksDoc = Payload(body, ks, fiveTopics);
            var ksCaption = ksDoc.RootElement.GetProperty("caption").GetString() ?? "";
            // 2026-11-09 起标题并入 caption 首行（风险 #30：快手没有独立标题字段）
            Check("快手：5 个话题 → 正文只留前 3 个 #标签",
                ksCaption == "狗的鼻子为什么总是湿的\n狗鼻子湿是正常现象。 #狗狗冷知识 #狗狗健康 #养狗经验",
                ksCaption);

            var recTags = ksDoc.RootElement.GetProperty("recTagIds")
                .EnumerateArray().Select(e => e.GetInt64()).ToList();
            Check("快手：recTagIdList 同步裁到 3 个（平台按 ID 列表再数一遍也会拒）",
                recTags.SequenceEqual([140808L, 140809L, 140810L]),
                string.Join(",", recTags));

            // 用户自己写在简介里的 # 也算平台的话题标签 —— 裁剪在合成之后做
            var inline = new PublishDraft(@"D:\demo\t19-inline.mp4")
            {
                Title = "正文自带话题",
                Description = "正文 #甲 #乙 #丙 #丁",
            };
            using var inlineDoc = Payload(inline, ks, new PlatformParams());
            var inlineCaption = inlineDoc.RootElement.GetProperty("caption").GetString() ?? "";
            Check("快手：简介里自己写的第 4 个 #标签同样被裁掉",
                inlineCaption == "正文自带话题\n正文 #甲 #乙 #丙", inlineCaption);

            // 快手网页版的话题是 `#话题#`（带尾缀）—— 裁第 4 个时不能留下孤零零的 `#`
            var hashStyle = new PublishDraft(@"D:\demo\t19-hashstyle.mp4")
            {
                Title = "尾缀井号",
                Description = "正文 #甲# #乙# #丙# #丁#",
            };
            using var hashDoc = Payload(hashStyle, ks, new PlatformParams());
            var hashCaption = hashDoc.RootElement.GetProperty("caption").GetString() ?? "";
            Check("快手：`#话题#` 尾缀写法裁掉时不留孤立 #",
                hashCaption == "尾缀井号\n正文 #甲# #乙# #丙#", hashCaption);

            var three = new PublishDraft(@"D:\demo\t19-three.mp4")
            {
                Title = "正好三个",
                Description = "正文",
            };
            var threeTopics = new PlatformParams
            {
                Topics =
                [
                    new ResolvedTopic("甲", "1", "甲"),
                    new ResolvedTopic("乙", "2", "乙"),
                    new ResolvedTopic("丙", "3", "丙"),
                ],
            };
            using var threeDoc = Payload(three, ks, threeTopics);
            Check("快手：正好 3 个 → 原样发送（不误裁）",
                (threeDoc.RootElement.GetProperty("caption").GetString() ?? "")
                    == "正好三个\n正文 #甲 #乙 #丙",
                threeDoc.RootElement.GetProperty("caption").GetString() ?? "");

            // 上限只对快手 —— 其它平台照旧全发（小红书话题走 ID 列表，不受正文标签数限制）
            using var xhsDoc = Payload(body, xhs, fiveTopics);
            Check("小红书：同样 5 个话题不被裁",
                (xhsDoc.RootElement.GetProperty("caption").GetString() ?? "")
                    .Split('#').Length - 1 == 5,
                xhsDoc.RootElement.GetProperty("caption").GetString() ?? "");

            // 预检要把「超出部分不会发送」说清楚（Warning，不拦提交）
            var preflightDraft = new PublishDraft(@"D:\demo\t19-preflight.mp4")
            {
                Title = "预检话题上限",
                Description = "狗鼻子湿是正常现象。",
            };
            preflightDraft.Targets.Add(ks);
            var overIssues = PublishPreflight.Validate(preflightDraft, fiveTopics);
            Check("预检：快手话题 >3 → Warning 点名「最多 3 个 / 超出部分不会发送」",
                overIssues.Any(i => i.PlatformKey == PlatformNames.Kuaishou
                    && i.Severity == PreflightSeverity.Warning
                    && i.Message.Contains("话题标签最多 3 个")
                    && i.Message.Contains("超出部分不会发送")),
                string.Join("；", overIssues.Select(i => i.Message)));
            Check("预检：是 Warning 不是 Error（不拦提交，载荷会自动裁）",
                overIssues.All(i => i.Severity != PreflightSeverity.Error
                    || !i.Message.Contains("话题标签最多")));

            // 另起一份草稿：ParamsFor 首次访问会把预设固化进 Overrides，
            // 同一份草稿再传另一份预设是读不到的（自检自身踩过的坑）
            var preflightDraft3 = new PublishDraft(@"D:\demo\t19-preflight3.mp4")
            {
                Title = "预检话题上限",
                Description = "狗鼻子湿是正常现象。",
            };
            preflightDraft3.Targets.Add(ks);
            var okIssues = PublishPreflight.Validate(preflightDraft3, threeTopics);
            Check("预检：快手话题 =3 → 不报该提示",
                okIssues.All(i => !i.Message.Contains("话题标签最多")),
                string.Join("；", okIssues.Select(i => i.Message)));
        }

        // ── 20. 发布工作台「全选」（2026-10-22：待发布 N 条右侧）──
        sb.AppendLine("\n[20] 发布工作台全选（待发布 N 条右侧）");
        {
            var none = Array.Empty<PublishDraft>();
            var drafts3 = new[]
            {
                new PublishDraft(@"D:\demo\select-all-a.mp4"),
                new PublishDraft(@"D:\demo\select-all-b.mp4"),
                new PublishDraft(@"D:\demo\select-all-c.mp4"),
            };

            Check("空列表 → 全选不亮", !PublishSelection.AllSelected(none));
            Check("新导入三条默认都勾 → 全选亮", PublishSelection.AllSelected(drafts3));
            drafts3[1].IsSelected = false;
            Check("逐条取消一条 → 全选熄灭", !PublishSelection.AllSelected(drafts3));
            PublishSelection.SetAll(drafts3, false);
            Check("整批取消 → 三条全不勾且全选熄灭",
                drafts3.All(d => !d.IsSelected) && !PublishSelection.AllSelected(drafts3));
            PublishSelection.SetAll(drafts3, true);
            Check("整批勾选 → 三条全勾且全选亮",
                drafts3.All(d => d.IsSelected) && PublishSelection.AllSelected(drafts3));
            PublishSelection.SetAll(none, true);
            Check("空列表批量设置 no-op 不抛", !PublishSelection.AllSelected(none));
        }

        // ── 21. 2026-10-25 教学视频两连败的回归防线 ──
        //   ① 小红书 461：响应体是假成功壳，提示必须按状态码给；
        //   ② 小红书定时被贴成 UTC → 发布推后 8 小时；
        //   ③ 小红书发布体不带元数据 → 所有视频都声明 10 秒；
        //   ④ 视频号等待转码的窗口按 34 分钟视频放大。
        sb.AppendLine("\n[21] 小红书 461 / 定时 / 元数据 + 视频号转码窗口（2026-10-25 回归）");
        {
            // ① 风控码翻译（461 的 body 是 {"code":0,"success":true}，只能认状态码）
            Check("461 → 提示人机验证", XhsRiskBlock.HintFor(461)?.Contains("人机验证") == true,
                XhsRiskBlock.HintFor(461) ?? "(null)");
            Check("462/465/471 各有专属提示",
                XhsRiskBlock.HintFor(462) is not null && XhsRiskBlock.HintFor(465) is not null
                && XhsRiskBlock.HintFor(471) is not null);
            Check("普通错误码不冒充风控（404 返回 null）", XhsRiskBlock.HintFor(404) is null);

            // ② 定时：本地 20:00(+08) 必须发成 12:00Z，而不是 20:00Z（旧写法多推 8 小时）
            var localAt = new DateTimeOffset(new DateTime(2026, 10, 26, 20, 0, 0), TimeSpan.FromHours(8));
            var scheduled = new XhsPublishRequest
            {
                Title = "夏日海边", FileId = "spectrum/abc", ScheduledAt = localAt,
            };
            var payload = XhsPublishService.BuildNotePayload(scheduled);
            var bindsJson = ((Dictionary<string, object?>)payload["common"]!)["business_binds"] as string;
            var postTime = 0L;
            if (bindsJson is not null)
            {
                using var bindsDoc = JsonDocument.Parse(bindsJson);
                if (bindsDoc.RootElement.GetProperty("notePostTiming")
                        .TryGetProperty("postTime", out var pt))
                    postTime = pt.GetInt64();
            }
            var expectAt = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            Check("定时 postTime = 绝对时刻（本地 20:00 → 12:00Z，不再 +8h）",
                postTime == expectAt, $"实际 {postTime} / 期望 {expectAt}");

            var scheduledBizType = -1;
            if (bindsJson is not null)
            {
                using var bindsDoc2 = JsonDocument.Parse(bindsJson);
                scheduledBizType = bindsDoc2.RootElement.GetProperty("bizType").GetInt32();
            }
            Check("定时 bizType = 13（2026-11-05 实测；旧实现恒 13 对立即发布是错的）",
                scheduledBizType == 13, $"实际 {scheduledBizType}");

            var immediate = XhsPublishService.BuildNotePayload(
                new XhsPublishRequest { Title = "x", FileId = "spectrum/abc" });
            var immediateBinds = ((Dictionary<string, object?>)immediate["common"]!)["business_binds"] as string;
            var immediateOk = false;
            if (immediateBinds is not null)
            {
                using var idoc = JsonDocument.Parse(immediateBinds);
                immediateOk = idoc.RootElement.GetProperty("bizType").GetInt32() == 0
                    && idoc.RootElement.GetProperty("notePostTiming")
                        .GetProperty("postTime").GetInt64() == 0;
            }
            Check("立即发布 = bizType 0 + postTime 0（2026-11-05 抓包）", immediateOk);

            // ③ 元数据：带真值就发真值；没带（探测失败）才回落 1920×1080/10s
            var withMeta = new XhsPublishRequest
            {
                Title = "夏日海边", FileId = "spectrum/abc",
                Width = 1920, Height = 1080, DurationMs = 2_028_667,
            };
            var metaPayload = XhsPublishService.BuildNotePayload(withMeta);
            var videoMeta = ((Dictionary<string, object?>)metaPayload["video_info"]!)["composite_metadata"];
            var videoJson = JsonSerializer.Serialize(videoMeta);
            using (var vdoc = JsonDocument.Parse(videoJson))
            {
                var v = vdoc.RootElement.GetProperty("video");
                Check("发布体带真实时长（2028.667s，不是 10s）", v.GetProperty("duration").GetInt32() == 2_028_667,
                    $"实际 {v.GetProperty("duration").GetInt32()}");
                var segments = ((Dictionary<string, object?>)metaPayload["video_info"]!)["segments"];
                var segJson = JsonSerializer.Serialize(segments);
                using var sdoc = JsonDocument.Parse(segJson);
                var segDur = sdoc.RootElement.GetProperty("items")[0].GetProperty("duration").GetDouble();
                Check("分段时长跟着真实时长走（≈2028.667s）", Math.Abs(segDur - 2028.667) < 0.01,
                    $"实际 {segDur:0.###}");
            }

            // ④ 视频号等待窗口：34 分钟 / 492MB 必须远超旧的 60 次（3 分钟）
            var retries42 = PublishPipeline.WechatClipPollRetries(42, 30_000_000);
            var retries2028 = PublishPipeline.WechatClipPollRetries(2028.67, 492_524_628);
            var retriesUnknown = PublishPipeline.WechatClipPollRetries(0, 492_524_628);
            var retriesTiny = PublishPipeline.WechatClipPollRetries(0, 0);
            Check("42 秒视频 ≈ 旧预算（不因放大窗口而空转太久）", retries42 is >= 60 and <= 90,
                $"{retries42} 次 ≈ {retries42 * 3}s");
            Check("34 分钟视频窗口 > 10 分钟（旧 60 次=3 分钟不够）", retries2028 >= 200,
                $"{retries2028} 次 ≈ {retries2028 * 3 / 60} 分钟");
            Check("时长未知按体积回落（492MB > 3 分钟）", retriesUnknown > 60,
                $"{retriesUnknown} 次 ≈ {retriesUnknown * 3 / 60} 分钟");
            Check("全部夹在 [60, 600] 次（3~30 分钟）",
                retriesTiny == 60 && retries2028 <= 600
                && PublishPipeline.WechatClipPollRetries(86_400, 1L << 40) == 600);
        }

        // ── 22. 载荷基类整理（1.6）：标题上提 / 下载下放 / 旧队列载荷兼容 ──
        //   ① 标题是全局概念 → 上提到 PublishPayloadBase，抖音/小红书不再各存一份；
        //   ② 允许下载只有真正有该开关的平台才持有，视频号载荷上那份是假支持；
        //   ③ 升级前已落盘的队列载荷字段搬家后必须仍能读回（反序列化缺省/多余字段都不炸）。
        sb.AppendLine("\n[22] 载荷基类整理（标题上提 / 下载下放 / 旧载荷兼容）");
        {
            var d = new PublishDraft(@"D:\demo\t22.mp4")
            {
                Title = "苏州园区三居室",
                Description = "南北通透",
                AllowDownload = false,
            };

            string JsonOf(string platformKey)
            {
                var t = new PublishTarget(platformKey, Guid.NewGuid(), "主号");
                return PublishPayloadBuilder.Build(d, t, new PlatformParams()).ExtraDataJson ?? "{}";
            }

            // ① 标题在基类：四平台（B站/头条载荷 1.7 才建）都带 title
            foreach (var key in new[]
            {
                PlatformNames.Douyin, PlatformNames.Kuaishou,
                PlatformNames.Xiaohongshu, PlatformNames.WechatVideo,
            })
            {
                using var doc = JsonDocument.Parse(JsonOf(key));
                var title = doc.RootElement.TryGetProperty("title", out var el)
                    ? el.GetString() : "(缺)";
                Check($"{key} 载荷带 title（基类上提）", title == "苏州园区三居室", title ?? "(null)");
            }

            // ② 允许下载只在真正支持的三家；视频号请求体没有这个字段
            foreach (var key in new[] { PlatformNames.Douyin, PlatformNames.Kuaishou, PlatformNames.Xiaohongshu })
            {
                using var doc = JsonDocument.Parse(JsonOf(key));
                Check($"{key} 载荷带 allowDownload=false",
                    doc.RootElement.TryGetProperty("allowDownload", out var ad)
                    && ad.ValueKind == JsonValueKind.False);
            }
            using (var doc = JsonDocument.Parse(JsonOf(PlatformNames.WechatVideo)))
            {
                Check("视频号载荷不再带 allowDownload（请求体没有该字段）",
                    !doc.RootElement.TryGetProperty("allowDownload", out _));
            }

            // ③ 旧队列载荷：字段搬家后按新位置读回，多余字段忽略不炸
            var oldDy = PublishPayload.Deserialize<DouyinPayload>(
                "{\"caption\":\"正文\",\"title\":\"老标题\",\"allowDownload\":false,"
                + "\"mixId\":\"7123\",\"mixOrder\":3,\"visibility\":\"Private\"}");
            Check("旧抖音载荷：title/allowDownload 按新位置读回，mixOrder 被忽略",
                oldDy is { Title: "老标题", AllowDownload: false, MixId: "7123",
                           Visibility: PublishVisibility.Private });

            var oldWx = PublishPayload.Deserialize<WechatVideoPayload>(
                "{\"caption\":\"正文\",\"shortTitle\":\"老短标题\",\"allowDownload\":false}");
            Check("旧视频号载荷：多出来的 allowDownload 被忽略、其余照读",
                oldWx is { Caption: "正文", ShortTitle: "老短标题" });

            var oldKs = PublishPayload.Deserialize<KuaishouPayload>("{\"caption\":\"正文\"}");
            Check("旧快手载荷缺 allowDownload → 默认 true（老行为）", oldKs?.AllowDownload == true);

            // ④ 合集不再走服务端列 —— 提交 DTO 里没有 collectionId（平台侧 ID 在载荷里）
            var dto = PublishPayloadBuilder.Build(d,
                new PublishTarget(PlatformNames.Douyin, Guid.NewGuid(), "主号"), new PlatformParams());
            var dtoJson = JsonSerializer.Serialize(dto,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Check("提交 DTO 不再带 collectionId",
                !dtoJson.Contains("collectionId", StringComparison.OrdinalIgnoreCase), dtoJson);
        }

        // ── 23. 六平台载荷全量字段（1.7）──
        //   ① 六平台都要产出可反序列化的载荷（B站/头条 1.7 只组装、不执行）；
        //   ② 1.4/1.5 建好的公共字段与 Extras 要真的落到各平台载荷上；
        //   ③ 头条横/竖版分流（VideoType / 无简介 / 无定时）与 B站版权/声明映射。
        sb.AppendLine("\n[23] 六平台载荷全量字段（B站/头条只组装）");
        {
            var d = new PublishDraft(@"D:\demo\t23.mp4")
            {
                Title = "园区三居室",
                Description = "南北通透",
                CoverFrameMs = 1500,
                IsScheduled = true,
                ScheduledAt = DateTime.Today.AddDays(1).AddHours(20),
                Visibility = PublishVisibility.Private,
                IsOriginal = true,
                AllowDownload = false,
                Declaration = new ContentDeclaration { Kind = DeclarationKind.AiGenerated },
            };
            d.Topics.Add("苏州楼市");
            d.Meta = new VideoMeta(d.VideoPath, 1, 12, 1920, 1080);

            var p = new PlatformParams
            {
                CollectionId = "7704513538005402155",
                CollectionOrder = 2,
                Tags = ["家居", "室内设计", "家居"],
                CoverTitle = "封面标题",
                ExternalLink = "https://example.com/a",
                MusicId = "712345",
                Chapters = [new PlatformChapter(0, "开箱"), new PlatformChapter(3000, "实拍")],
                ActivityIds = ["7702034615116174390"],
                SyncTargets = ["toutiao"],
                Praise = true,
                Summary = "摘要一句话",
                AllowComment = false,
                AllowDanmaku = false,
                Douyin = new DouyinExtras { CoverCropWidth = 1080, CoverCropHeight = 1440, Mentions = "[]" },
                Kuaishou = new KuaishouExtras
                {
                    RiseQuality = true,
                    UseAiCover = false,
                    ChaptersJson = "[{\"desc\":\"开场\",\"timestamp\":0}]",
                    CoPublishUsersJson = "[{\"userId\":\"u1\"}]",
                    CoPublishRole = 1,
                },
                Xhs = new XhsExtras { SketchCollectionId = "sk1" },
                WechatVideo = new WechatVideoExtras { CardShowStyle = 2 },
                Bilibili = new BilibiliExtras { Tid = 21, HumanType2 = 1015, Dynamic = "动态", Watermark = false },
                Toutiao = new ToutiaoExtras { TemplateName = "模板A" },
            };

            string JsonOf(PublishDraft draft, string key, PlatformParams pp) =>
                PublishPayloadBuilder.Build(draft,
                    new PublishTarget(key, Guid.NewGuid(), "主号"), pp).ExtraDataJson ?? "{}";

            // ① 六平台都能产出载荷、都带 title（B站/头条此前没有分支）
            foreach (var key in new[]
            {
                PlatformNames.Douyin, PlatformNames.Kuaishou, PlatformNames.Xiaohongshu,
                PlatformNames.WechatVideo, PlatformNames.Bilibili, PlatformNames.Toutiao,
            })
            {
                using var doc = JsonDocument.Parse(JsonOf(d, key, p));
                var hasTitle = doc.RootElement.TryGetProperty("title", out var t)
                    && t.GetString() == "园区三居室";
                Check($"{key} 载荷可解析且带 title", hasTitle);
            }

            // ② 四平台已有的全量字段
            var dy = PublishPayload.Deserialize<DouyinPayload>(JsonOf(d, PlatformNames.Douyin, p));
            Check("抖音载荷：封面帧/裁剪/音乐/章节/活动/同步/合集排序",
                dy is { CoverFrameMs: 1500, CoverCropWidth: 1080, CoverCropHeight: 1440,
                        MusicId: "712345", Mentions: "[]", CollectionOrder: 2 }
                && dy.Chapters?.Count == 2 && dy.ActivityIds?.Count == 1
                && dy.SyncTargets is ["toutiao"]);

            var ks = PublishPayload.Deserialize<KuaishouPayload>(JsonOf(d, PlatformNames.Kuaishou, p));
            Check("快手载荷：封面帧/封面文字/排序/通知/画质/章节与共创 JSON",
                ks is { CoverFrameMs: 1500, CoverTitle: "封面标题", CollectionOrder: 2,
                        NotifyFollowers: true, RiseQuality: true, CoPublishRole: 1 }
                && ks.ChaptersJson == "[{\"desc\":\"开场\",\"timestamp\":0}]"
                && ks.CoPublishUsersJson == "[{\"userId\":\"u1\"}]");

            var xhs = PublishPayload.Deserialize<XhsPayload>(JsonOf(d, PlatformNames.Xiaohongshu, p));
            Check("小红书载荷：封面帧/摘要/评论/速览合集/排序",
                xhs is { CoverFrameMs: 1500, Summary: "摘要一句话", AllowComment: false,
                         SketchCollectionId: "sk1", CollectionOrder: 2, IsOriginal: true,
                         AllowDownload: false });

            var wx = PublishPayload.Deserialize<WechatVideoPayload>(JsonOf(d, PlatformNames.WechatVideo, p));
            Check("视频号载荷：扩展链接/卡片样式/排序",
                wx is { ExternalLink: "https://example.com/a", CardShowStyle: 2, CollectionOrder: 2 });

            // ③ B站：默认分区、标签清洗、版权/声明映射
            var bili = PublishPayload.Deserialize<BilibiliPayload>(JsonOf(d, PlatformNames.Bilibili, p));
            Check("B站载荷：分区默认/水印覆盖/动态/标签去重",
                bili is { Tid: 21, HumanType2: 1015, Watermark: false, Dynamic: "动态" }
                && bili?.Tags is ["家居", "室内设计"]);

            Check("B站：原创 → copyright=1、creation_statement=1",
                bili is { Copyright: 1, CreationStatementId: 1, IsOriginal: true });
            Check("B站：转载 → copyright=2（声明下拉无转载，走版权）",
                CopyrightMapping.Bilibili(DeclarationKind.Repost, isOriginal: false) == 2);
            Check("B站：未声明未原创 → copyright=3（沿用抓包默认）",
                CopyrightMapping.Bilibili(DeclarationKind.None, isOriginal: false) == 3);
            Check("B站声明能力：AI/虚构/营销支持，转载/自行拍摄不支持",
                DeclarationMapping.Supports(PlatformNames.Bilibili, DeclarationKind.AiGenerated)
                && DeclarationMapping.Supports(PlatformNames.Bilibili, DeclarationKind.Marketing)
                && !DeclarationMapping.Supports(PlatformNames.Bilibili, DeclarationKind.Repost)
                && !DeclarationMapping.Supports(PlatformNames.Bilibili, DeclarationKind.SelfShot));
            Check("B站：定时意图带进载荷（执行端阶段 4 必须显式拦截，不得静默改立即）",
                bili?.ScheduledAt == d.ScheduledAt);
            Check("B站：可见性走语义字段（is_only_self 执行时派生）",
                bili?.Visibility == PublishVisibility.Private);

            // ④ 头条：横版 VideoType=3、Abstract 含 # 话题、定时保留
            var tt = PublishPayload.Deserialize<ToutiaoPayload>(JsonOf(d, PlatformNames.Toutiao, p));
            Check("头条横版：VideoType=3 / Abstract 含 # 话题 / 定时保留",
                tt is { VideoType: 3 } && tt.Caption?.Contains("#苏州楼市") == true
                && tt.ScheduledAt == d.ScheduledAt);
            Check("头条：原创/赞赏/合集/模板/同步字段落载荷",
                tt is { IsOriginal: true, Praise: true, TemplateName: "模板A" }
                && tt?.CollectionId == "7704513538005402155"
                && tt?.Topics is ["苏州楼市"]
                && tt?.SyncTargets is ["toutiao"]);
            Check("头条声明暂不支持（字段未抓，v1 不下发）",
                !DeclarationMapping.Supports(PlatformNames.Toutiao, DeclarationKind.AiGenerated));

            // ⑤ 头条竖版：VideoType=6、无简介、无定时
            var vd = new PublishDraft(@"D:\demo\t23v.mp4")
            {
                Title = "竖版视频",
                Description = "竖版简介",
                IsScheduled = true,
                ScheduledAt = DateTime.Today.AddDays(1).AddHours(20),
            };
            vd.Meta = new VideoMeta(vd.VideoPath, 1, 12, 1080, 1920);
            var ttV = PublishPayload.Deserialize<ToutiaoPayload>(
                JsonOf(vd, PlatformNames.Toutiao, new PlatformParams()));
            Check("头条竖版：VideoType=6 / 无 Abstract / 无定时",
                ttV is { VideoType: 6, Caption: null, ScheduledAt: null });

            // ⑥ 空列表不进载荷（WhenWritingNull），旧队列记录保持干净
            var lean = JsonOf(d, PlatformNames.Douyin, new PlatformParams());
            Check("空列表不写进载荷（chapters/activityIds 不出现）",
                !lean.Contains("chapters", StringComparison.Ordinal)
                && !lean.Contains("activityIds", StringComparison.Ordinal));

            // ⑦ 往返：序列化 → 反序列化，B站/头条关键字段不丢
            var biliBack = PublishPayload.Deserialize<BilibiliPayload>(
                PublishPayload.Serialize(bili!));
            var ttBack = PublishPayload.Deserialize<ToutiaoPayload>(PublishPayload.Serialize(tt!));
            Check("B站载荷往返：分区/版权/声明/标签不丢",
                biliBack is { Tid: 21, HumanType2: 1015, Copyright: 1, CreationStatementId: 1 }
                && biliBack?.Tags is ["家居", "室内设计"]);
            Check("头条载荷往返：VideoType/原创/合集/话题不丢",
                ttBack is { VideoType: 3, IsOriginal: true, Praise: true }
                && ttBack?.CollectionId == "7704513538005402155"
                && ttBack?.Topics is ["苏州楼市"]);
        }

        // ── 24. 抖音发布字段接线（阶段 2.1）──
        //   ① 抓包默认值：hashtag_source=recommend / media_type=4 / timing 是 Unix 秒；
        //   ② 1.3 抖音表的长尾真的落到 create_v2 item 上（音乐/章节/活动/@/贴纸/同步/封面帧/裁剪/排序）；
        //   ③ 编辑器文本代理（PublishFieldText）解析规则。
        sb.AppendLine("\n[24] 抖音发布字段接线（2.1）");
        {
            var scheduled = new DateTime(2026, 11, 7, 20, 0, 0, DateTimeKind.Local);
            var req = new CreateDouyinWorkRequest(
                VideoId: "v0300test",
                Title: "标题",
                Description: "简介",
                ScheduledAt: scheduled);

            var item = DouyinVideoService.BuildCreateItem(req);
            Check("抖音：默认 hashtag_source=recommend（三段式已过时）",
                item.Common.HashtagSource == "recommend", item.Common.HashtagSource ?? "(null)");
            Check("抖音：media_type=4", item.Common.MediaType == 4);
            Check("抖音：timing 下发 Unix 秒",
                item.Common.Timing == (int)new DateTimeOffset(scheduled).ToUnixTimeSeconds(),
                item.Common.Timing.ToString());

            var itemJson = JsonSerializer.Serialize(item);
            Check("抖音：请求体不再有 timing_time", !itemJson.Contains("timing_time", StringComparison.Ordinal));
            Check("抖音：请求体带 media_type=4 / hashtag_source=recommend",
                itemJson.Contains("\"media_type\":4") && itemJson.Contains("\"hashtag_source\":\"recommend\""));
            Check("抖音：无章节时仍是默认空章节（chapter_type=0）",
                item.Chapter.Chapter.Contains("\"chapter_type\":0", StringComparison.Ordinal));
            Check("抖音：无活动时 activity=[] / 同步关闭 / 无音乐",
                item.Common.Activity == "[]" && item.Common.MusicId is null
                && !item.Sync.ShouldSync && item.Sync.SyncToToutiao == 0);

            var hot = DouyinVideoService.BuildCreateItem(req with { HotSentence = "苏州楼市" });
            Check("抖音：选热点 → hashtag_source=search/search/search",
                hot.Common.HashtagSource == "search/search/search", hot.Common.HashtagSource ?? "(null)");

            // 全量长尾
            var full = new CreateDouyinWorkRequest(
                VideoId: "v0300full",
                Title: "标题",
                Description: "简介",
                CollectionMixId: "7704506497719355407",
                MixOrder: 3,
                MusicId: "712345",
                Chapters:
                [
                    new DouyinChapter(1500, "开场"),
                    new DouyinChapter(0, "片头"),
                    new DouyinChapter(0, "同一时间点重复"),
                ],
                ActivityIds: ["7702034615116174390", "abc", "7702034615116174390"],
                Mentions: "[{\"uid\":\"1\"}]",
                InteractionStickers: "[]",
                SyncTargets: ["toutiao"],
                CoverFrameMs: 4500,
                CoverCropWidth: 1051,
                CoverCropHeight: 788,
                CoverToolsInfo: "{\"cover_type\":1}",
                CoverToolsExtendInfo: "{\"recommendCoverList\":[]}",
                CoverUri: "tos-cn-i-test/cover");

            var fi = DouyinVideoService.BuildCreateItem(full);
            Check("抖音：音乐 / @ / 贴纸进请求",
                fi.Common.MusicId == "712345" && fi.Common.Mentions == "[{\"uid\":\"1\"}]"
                && fi.Common.InteractionStickers == "[]");
            Check("抖音：同步头条 → should_sync=true / sync_to_toutiao=1",
                fi.Sync.ShouldSync && fi.Sync.SyncToToutiao == 1);
            Check("抖音：封面帧 4500ms → poster_delay=4 秒", fi.Cover.PosterDelay == 4,
                fi.Cover.PosterDelay.ToString());
            Check("抖音：封面裁剪尺寸 / 工具信息 / 封面 poster（2.5.1 起不再发 uri）",
                fi.Cover.CustomCoverImageWidth == 1051 && fi.Cover.CustomCoverImageHeight == 788
                && fi.Cover.CoverToolsInfo == "{\"cover_type\":1}"
                && fi.Cover.CoverToolsExtendInfo == "{\"recommendCoverList\":[]}"
                && fi.Cover.Poster == "tos-cn-i-test/cover");
            Check("抖音：合集排序进 mix", fi.Mix.MixId == "7704506497719355407" && fi.Mix.MixOrder == 3);
            Check("抖音：活动 JSON 字符串（数字 ID / 去重 / 非数字保留字符串）",
                fi.Common.Activity == "[7702034615116174390,\"abc\"]", fi.Common.Activity);

            using (var chapterDoc = JsonDocument.Parse(fi.Chapter.Chapter))
            {
                var root = chapterDoc.RootElement;
                var details = root.GetProperty("chapter_details");
                Check("抖音：章节 JSON（desc + timestamp 毫秒 / type=1 / 按时间升序 / 去重）",
                    root.GetProperty("chapter_type").GetInt32() == 1
                    && details.GetArrayLength() == 2
                    && details[0].GetProperty("desc").GetString() == "片头"
                    && details[0].GetProperty("timestamp").GetInt64() == 0
                    && details[1].GetProperty("desc").GetString() == "开场"
                    && details[1].GetProperty("timestamp").GetInt64() == 1500);
            }

            // 编辑器文本代理（纯函数）
            var chapters = PublishFieldText.ParseChapters(
                "0:00 开场\n1:30 实拍\n92 纯秒\n坏行没有时间\n1:30 重复");
            Check("文本代理：章节解析（mm:ss / 纯秒 / 坏行跳过 / 同一时间点去重）",
                chapters.Count == 3
                && chapters[0] == new PlatformChapter(0, "开场")
                && chapters[1] == new PlatformChapter(90_000, "实拍")
                && chapters[2] == new PlatformChapter(92_000, "纯秒"));
            Check("文本代理：章节格式化往返",
                PublishFieldText.FormatChapters([new PlatformChapter(90_000, "实拍")]) == "1:30 实拍");
            Check("文本代理：封面帧秒 ↔ 毫秒",
                PublishFieldText.ParseSecondsToMs("4.5") == 4500
                && PublishFieldText.FormatMsAsSeconds(4500) == "4.5"
                && PublishFieldText.ParseSecondsToMs("") is null
                && PublishFieldText.ParseSecondsToMs("-1") is null);
            Check("文本代理：ID 列表（中英文逗号/顿号/空白 + 去重）",
                PublishFieldText.ParseIds(" a, b，a、c ").SequenceEqual(["a", "b", "c"]));
            Check("文本代理：整数（0 合法 / 负数与垃圾为空）",
                PublishFieldText.ParseInt("0") == 0
                && PublishFieldText.ParseInt("-1") is null
                && PublishFieldText.ParseInt("abc") is null);
        }

        // ── 25. 快手发布字段接线（阶段 2.2）──
        //   ① 抓包口径：downloadType 1/2（0 不是「禁止」）、notifyResult 1/0、poiId 数字；
        //   ② 1.3 快手表长尾真的落到 submit 请求体（封面帧/封面文字/PK 封面/画质 AI/禁止二创/领域/短剧/活动）；
        //   ③ 章节 / 共创元素结构未抓到 —— 原始 JSON 透传，坏格式明确报错。
        sb.AppendLine("\n[25] 快手发布字段接线（2.2）");
        {
            KuaishouPublishRequest Req(bool allowDownload = true) => new()
            {
                FileId = 4082915297,
                CoverKey = "cp_video_upload_cover_test.jpg",
                MediaId = "12587728518441832067123780",
                PhotoIdStr = "214622404377",
                VideoDuration = 6208,
                Caption = "文案",
                AllowDownload = allowDownload,
            };

            var def = KuaishouPublishService.BuildSubmitRequest(Req());
            Check("快手：允许下载 → downloadType=1", def.DownloadType == 1);
            Check("快手：禁止下载 → downloadType=2（旧实现 0 是错的）",
                KuaishouPublishService.BuildSubmitRequest(Req(false)).DownloadType == 2);
            Check("快手：默认通知粉丝 notifyResult=1 / 同城与同框开",
                def.NotifyResult == 1 && !def.DisableNearbyShow && def.AllowSameFrame);
            Check("快手：无位置 → poiId=0（数字，不是空串）",
                Convert.ToInt64(def.PoiId) == 0);

            var full = KuaishouPublishService.BuildSubmitRequest(new KuaishouPublishRequest
            {
                FileId = 1,
                CoverKey = "cover.jpg",
                MediaId = "media",
                PhotoIdStr = "photo",
                VideoDuration = 1000,
                Caption = "文案",
                AllowDownload = false,
                NotifyFollowers = false,
                ShowInNearby = false,
                AllowSameFrame = false,
                PoiId = "120597863",
                CoverFrameMs = 4500,
                CoverTitle = "封面字",
                PkCoverKey = "pk.jpg",
                PkCoverTimeStamp = 2000,
                PkCoverType = 2,
                PkCoverSize = "720x1280",
                ProfileCoverKey = "profile.jpg",
                RiseQuality = true,
                UseAiCaption = true,
                UseAiCaptionCover = true,
                UseAiCover = true,
                UseIdealTime = true,
                DisallowRecreation = true,
                Domain = "短剧",
                SecondDomain = "都市",
                MovieId = "m1",
                ProjectId = "p1",
                ChaptersJson = "[{\"desc\":\"开场\",\"timestamp\":0}]",
                CoPublishUsersJson = "[{\"userId\":\"u1\"}]",
                CoPublishRole = 1,
                ActivityIds = ["318524", "abc", "318524"],
            });

            Check("快手：封面帧 4500ms → coverTimeStamp=4500 / 封面文字",
                full.CoverTimeStamp == 4500 && full.CoverTitle == "封面字");
            Check("快手：PK 封面 / 主页封面字段落请求体",
                full.PkCoverKey == "pk.jpg" && full.PkCoverTimeStamp == 2000
                && full.PkCoverType == 2 && full.PkCoverSize == "720x1280"
                && full.ProfileCoverKey == "profile.jpg");
            Check("快手：画质增强 / AI 字幕 / AI 封面 / 智能时间 / 禁止二创",
                full.RiseQuality && full.UseAiCaption && full.UseAiCaptionCover
                && full.UseAiCover && full.IsUseIdealTime && full.DisallowRecreation);
            Check("快手：专属领域 / 短剧 / 项目",
                full.Domain == "短剧" && full.SecondDomain == "都市"
                && full.MovieId == "m1" && full.ProjectId == "p1");
            Check("快手：通知/同城/同框反向映射 + 禁止下载",
                full.NotifyResult == 0 && full.DisableNearbyShow && !full.AllowSameFrame
                && full.DownloadType == 2);
            Check("快手：poiId 数字（不是字符串）", Convert.ToInt64(full.PoiId) == 120597863);

            var act = KuaishouPublishService.BuildActivityIds(["318524", "abc", "318524"]);
            Check("快手：活动 ID（数字转数字 / 字符串保留 / 去重）",
                act.Count == 2 && act[0] is long n && n == 318524 && (string)act[1] == "abc");

            // upload/finish 对中文文件名回 result=500002（2026-11-09 实测：同一视频同一账号，
            // 中文名 4×2 次全挂、cat_video.mp4 第一次通过）—— wire 名必须 ASCII。
            Check("快手：中文文件名 → video.mp4（upload/finish 500002 实测）",
                KuaishouUploadService.WireFileName("橘猫为什么大多是公的.mp4") == "video.mp4",
                KuaishouUploadService.WireFileName("橘猫为什么大多是公的.mp4"));
            Check("快手：ASCII 文件名原样保留",
                KuaishouUploadService.WireFileName("cat_video.mp4") == "cat_video.mp4");
            Check("快手：非法字符替换 / 无扩展名回落 video.mp4",
                KuaishouUploadService.WireFileName("a b:c.mp4") == "a_b_c.mp4"
                && KuaishouUploadService.WireFileName("视频") == "video.mp4");

            var fullJson = JsonSerializer.Serialize(full);
            using (var body = JsonDocument.Parse(fullJson))
            {
                var root = body.RootElement;
                var chapters = root.GetProperty("chapters");
                Check("快手：章节原始 JSON 透传（元素原样）",
                    chapters.GetArrayLength() == 1
                    && chapters[0].GetProperty("desc").GetString() == "开场");
                Check("快手：共创 JSON 透传 + coPublishRole",
                    root.GetProperty("coPublishUser").GetArrayLength() == 1
                    && root.GetProperty("coPublishRole").GetInt32() == 1);
                Check("快手：请求体不再有重复的 activity 字段",
                    !root.TryGetProperty("activity", out _));
            }

            var leanJson = JsonSerializer.Serialize(KuaishouPublishService.BuildSubmitRequest(Req()));
            Check("快手：未填章节/共创 → 空数组（不是 null）",
                leanJson.Contains("\"chapters\":[]", StringComparison.Ordinal)
                && leanJson.Contains("\"coPublishUser\":[]", StringComparison.Ordinal));

            var bad = "";
            try { KuaishouPublishService.ParseJsonArray("不是 JSON", "章节"); }
            catch (Exception ex) { bad = ex.Message; }
            Check("快手：坏章节 JSON 明确报错（不静默丢）",
                bad.Contains("章节", StringComparison.Ordinal), bad);
        }

        // ── 26. 小红书发布字段接线（2.3）──
        //   抓包依据：docs/xiaohongshu/20261105/requests/note_create_video_public.json、
        //   note_create_scheduled_private.json、note_update_video.json + 页面 Vue bundle 的章节构造函数。
        //   要点：bizType 0/13 随定时；coProduceBind 不再下发；copyable/commentPermission 映射；
        //         chapters 是 [{text, time:"<毫秒字符串>"}]。
        sb.AppendLine("\n[26] 小红书发布字段接线（2.3）");
        {
            static string Binds(Dictionary<string, object?> payload)
            {
                var common = (Dictionary<string, object?>)payload["common"]!;
                return (string)common["business_binds"]!;
            }

            var def = XhsPublishService.BuildNotePayload(
                new XhsPublishRequest { Title = "夏日海边", FileId = "spectrum/abc" });
            using (var doc = JsonDocument.Parse(Binds(def)))
            {
                var b = doc.RootElement;
                Check("小红书：默认 copyable=true / commentPermission=0",
                    b.GetProperty("noteCopyBind").GetProperty("copyable").GetBoolean()
                    && b.GetProperty("interactionPermissionBind")
                        .GetProperty("commentPermission").GetInt32() == 0);
                Check("小红书：发布体不再带 coProduceBind（2026-11-05 抓包无该字段）",
                    !b.TryGetProperty("coProduceBind", out _));
                Check("小红书：立即发布 bizType=0 / postTime=0",
                    b.GetProperty("bizType").GetInt32() == 0
                    && b.GetProperty("notePostTiming").GetProperty("postTime").GetInt64() == 0);
            }

            var defJson = JsonSerializer.Serialize(def);
            using (var doc = JsonDocument.Parse(defJson))
            {
                var root = doc.RootElement;
                Check("小红书：未填 @/带货/业务关系 → 空数组与空对象（不是 null）",
                    root.GetProperty("common").GetProperty("ats").GetArrayLength() == 0
                    && root.GetProperty("common").GetProperty("goods_info").ValueKind == JsonValueKind.Object
                    && root.GetProperty("common").GetProperty("biz_relations").GetArrayLength() == 0
                    && root.GetProperty("video_info").GetProperty("chapters").GetArrayLength() == 0
                    && root.GetProperty("video_info").GetProperty("summary").GetString() == ""
                    && !root.GetProperty("video_info").GetProperty("chapter_sync_text").GetBoolean());
            }

            var scheduledAt = new DateTimeOffset(2026, 11, 7, 20, 0, 0, TimeSpan.FromHours(8));
            var full = XhsPublishService.BuildNotePayload(new XhsPublishRequest
            {
                Title = "夏日海边",
                FileId = "spectrum/abc",
                ScheduledAt = scheduledAt,
                AllowDownload = false,
                AllowComment = false,
                Summary = "一句话摘要",
                Chapters =
                [
                    new XhsChapter("实拍", 90_000),
                    new XhsChapter("开场", 0),
                ],
                SketchCollectionId = "sk-1",
                OrderBindJson = "{\"orderId\":\"o1\"}",
                ChapterSyncText = true,
                AtsJson = "[{\"userId\":\"u1\"}]",
                GoodsInfoJson = "{\"goodsId\":\"g1\"}",
                BizRelationsJson = "[{\"bizId\":\"b1\"}]",
                PkCoverBizRelationsJson = "[{\"bizId\":\"c1\"}]",
            });

            using (var doc = JsonDocument.Parse(Binds(full)))
            {
                var b = doc.RootElement;
                Check("小红书：定时 bizType=13 / postTime=绝对毫秒",
                    b.GetProperty("bizType").GetInt32() == 13
                    && b.GetProperty("notePostTiming").GetProperty("postTime").GetInt64()
                        == scheduledAt.ToUnixTimeMilliseconds());
                Check("小红书：禁止下载 → copyable=false",
                    !b.GetProperty("noteCopyBind").GetProperty("copyable").GetBoolean());
                Check("小红书：禁止评论 → commentPermission=1",
                    b.GetProperty("interactionPermissionBind")
                        .GetProperty("commentPermission").GetInt32() == 1);
                Check("小红书：速览合集 / 订单绑定",
                    b.GetProperty("noteSketchCollectionBind").GetProperty("id").GetString() == "sk-1"
                    && b.GetProperty("noteOrderBind").GetProperty("orderId").GetString() == "o1");
            }

            using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(full)))
            {
                var root = doc.RootElement;
                var vi = root.GetProperty("video_info");
                var chapters = vi.GetProperty("chapters");
                Check("小红书：章节 [{text, time:\"<毫秒>\"}]（按时间升序）",
                    chapters.GetArrayLength() == 2
                    && chapters[0].GetProperty("text").GetString() == "开场"
                    && chapters[0].GetProperty("time").GetString() == "0"
                    && chapters[1].GetProperty("text").GetString() == "实拍"
                    && chapters[1].GetProperty("time").GetString() == "90000");
                Check("小红书：摘要 / 章节文字同步",
                    vi.GetProperty("summary").GetString() == "一句话摘要"
                    && vi.GetProperty("chapter_sync_text").GetBoolean());
                Check("小红书：PK 封面业务关联透传",
                    vi.GetProperty("pk_cover_biz_relations").GetArrayLength() == 1);
                var common = root.GetProperty("common");
                Check("小红书：@ 用户 / 带货 / 业务关系透传",
                    common.GetProperty("ats")[0].GetProperty("userId").GetString() == "u1"
                    && common.GetProperty("goods_info").GetProperty("goodsId").GetString() == "g1"
                    && common.GetProperty("biz_relations")[0].GetProperty("bizId").GetString() == "b1");
            }

            var bad = "";
            try { XhsPublishService.ParseJsonValue("不是 JSON", "@ 用户", JsonValueKind.Array); }
            catch (XhsPublishException ex) { bad = ex.Message; }
            Check("小红书：坏 JSON 明确报错（不静默丢）",
                bad.Contains("小红书@ 用户", StringComparison.Ordinal), bad);

            var wrongKind = "";
            try { XhsPublishService.ParseJsonValue("{}", "业务关系", JsonValueKind.Array); }
            catch (XhsPublishException ex) { wrongKind = ex.Message; }
            Check("小红书：类型不对明确报错（数组字段传对象）",
                wrongKind.Contains("必须是 JSON 数组", StringComparison.Ordinal), wrongKind);
        }

        // ── 27. 视频号发布字段接线（2.4）──
        //   抓包依据：docs/wechatvideo/20261105/requests/post_create_video.json
        //   （立即 postFlag=1 / 无 effectiveTime / 有 tagInfo 与 position）、
        //   定时抓包（postFlag=0 + effectiveTime 秒 + **无 tagInfo**）、图文抓包（无 shortTitle）；
        //   短标题 ≥6 字与 extReading {link,title,type} 取自页面 bundle。
        sb.AppendLine("\n[27] 视频号发布字段接线（2.4）");
        {
            var fixedNow = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
            static JsonElement Body(WechatVideoPublishRequest req, DateTimeOffset now) =>
                JsonSerializer.SerializeToElement(
                    WechatVideoContentService.BuildPostBody(req, "clip-1", "v2_xxx@finder", now));
            JsonElement Make(WechatVideoPublishRequest req) => Body(req, fixedNow);

            var immediate = Make(new WechatVideoPublishRequest(
                VideoUrl: "https://example.com/v.mp4", FileSize: 1024));
            Check("视频号：立即发布 postFlag=1 且不下发 effectiveTime",
                immediate.GetProperty("postFlag").GetInt32() == 1
                && !immediate.TryGetProperty("effectiveTime", out _));

            var scheduledAt = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
            var scheduled = Make(new WechatVideoPublishRequest(
                VideoUrl: "https://example.com/v.mp4", FileSize: 1024,
                ScheduledAt: scheduledAt));
            Check("视频号：定时 postFlag=0 / effectiveTime=Unix 秒",
                scheduled.GetProperty("postFlag").GetInt32() == 0
                && scheduled.GetProperty("effectiveTime").GetInt64()
                    == new DateTimeOffset(scheduledAt).ToUnixTimeSeconds());

            var desc = immediate.GetProperty("objectDesc");
            Check("视频号：未选标注 → 不下发 tagInfo（定时抓包无该键）",
                !immediate.TryGetProperty("tagInfo", out _));
            var withTag = Make(new WechatVideoPublishRequest(
                VideoUrl: "https://example.com/v.mp4", FileSize: 1024,
                TagInfo: new { tagType = 8, tagKey = "k1" }));
            Check("视频号：标注透传 tagInfo",
                withTag.GetProperty("tagInfo").GetProperty("tagType").GetInt32() == 8);

            Check("视频号：无位置 → 整个 location 不下发",
                !desc.TryGetProperty("location", out _));
            var withLoc = Make(new WechatVideoPublishRequest(
                VideoUrl: "https://example.com/v.mp4", FileSize: 1024,
                Location: new { poiName = "东方胖菜店", latitude = 31.2, longitude = 120.5 }));
            Check("视频号：有位置 → location 透传",
                withLoc.GetProperty("objectDesc").GetProperty("location")
                    .GetProperty("poiName").GetString() == "东方胖菜店");

            Check("视频号：无链接 → extReading = {}",
                desc.GetProperty("extReading").ValueKind == JsonValueKind.Object
                && !desc.GetProperty("extReading").EnumerateObject().Any());
            var withLink = Make(new WechatVideoPublishRequest(
                VideoUrl: "https://example.com/v.mp4", FileSize: 1024,
                ExternalLink: "https://example.com/a", ExternalLinkTitle: "示例文章"));
            var ext = withLink.GetProperty("objectDesc").GetProperty("extReading");
            Check("视频号：扩展链接 {link,title,type}",
                ext.GetProperty("link").GetString() == "https://example.com/a"
                && ext.GetProperty("title").GetString() == "示例文章"
                && ext.GetProperty("type").GetInt32() == 1);

            Check("视频号：未填短标题 → 不下发 shortTitle",
                !desc.TryGetProperty("shortTitle", out _));
            var shortOk = Make(new WechatVideoPublishRequest(
                VideoUrl: "https://example.com/v.mp4", FileSize: 1024,
                ShortTitle: "视频号测试短标题"));
            Check("视频号：短标题 [{shortTitle}]（≥6 字）",
                shortOk.GetProperty("objectDesc").GetProperty("shortTitle")[0]
                    .GetProperty("shortTitle").GetString() == "视频号测试短标题");
            var shortBad = Make(new WechatVideoPublishRequest(
                VideoUrl: "https://example.com/v.mp4", FileSize: 1024,
                ShortTitle: "五字短标"));
            Check("视频号：短标题 5 字 → 不下发（页面会报「至少6个字」）",
                !shortBad.GetProperty("objectDesc").TryGetProperty("shortTitle", out _));

            var card = Make(new WechatVideoPublishRequest(
                VideoUrl: "https://example.com/v.mp4", FileSize: 1024,
                CardShowStyle: 1));
            Check("视频号：卡片样式透传 media[].cardShowStyle",
                card.GetProperty("objectDesc").GetProperty("media")[0]
                    .GetProperty("cardShowStyle").GetInt32() == 1);

            // builder：标题 ≥6 → 短标题；<6 → 不下发 + 标题并入正文首行
            WechatVideoPayload? Built(string title, PlatformParams pp)
            {
                var d = new PublishDraft(@"D:\demo\t24.mp4")
                {
                    Title = title,
                    Description = "南北通透",
                };
                return PublishPayload.Deserialize<WechatVideoPayload>(
                    PublishPayloadBuilder.Build(d,
                        new PublishTarget(PlatformNames.WechatVideo, Guid.NewGuid(), "主号"), pp)
                        .ExtraDataJson);
            }

            var wxLong = Built("苏州园区三居室", new PlatformParams());
            Check("视频号 builder：标题 ≥6 字 → ShortTitle=标题、正文不动",
                wxLong is { ShortTitle: "苏州园区三居室", Caption: "南北通透" });

            var wxShort = Built("买房", new PlatformParams());
            Check("视频号 builder：标题 <6 字 → 不下发短标题、标题并入正文首行",
                wxShort is { ShortTitle: null, Caption: "买房\n南北通透" });

            var wxOverride = Built("苏州园区三居室", new PlatformParams { ShortTitle = "四字的短标" });
            Check("视频号 builder：短标题预设覆盖标题（<6 字同样下沉正文）",
                wxOverride is { ShortTitle: null }
                && wxOverride.Caption?.StartsWith("四字的短标\n", StringComparison.Ordinal) == true);

            // 预检：标题 <6 字不再 Error（发得出去，只是短标题缺省）
            var wxFile = Path.Combine(Path.GetTempPath(), "newapp-selftest-t27.mp4");
            File.WriteAllText(wxFile, "x");
            try
            {
                var pd = new PublishDraft(wxFile)
                {
                    Title = "买房",
                    Description = "南北通透",
                };
                pd.Targets.Add(new PublishTarget(PlatformNames.WechatVideo, Guid.NewGuid(), "主号"));
                var pre = PublishPreflight.Validate(pd);
                Check("视频号预检：标题 <6 字只是 Warning（不拦发布）",
                    pre.Any(i => i.PlatformKey == PlatformNames.WechatVideo
                        && i.Severity == PreflightSeverity.Warning
                        && i.Message.Contains("不会作为短标题下发"))
                    && pre.All(i => !(i.PlatformKey == PlatformNames.WechatVideo
                        && i.Severity == PreflightSeverity.Error)));
            }
            finally { File.Delete(wxFile); }
        }

        // ── 28. 封面补齐：抖音封面帧/裁剪（2.5.1）──
        //   抓包依据：docs/douyin/20261105/requests/video_publish.json（自定义封面图那条：
        //   poster_delay=0、custom_cover_image_width=1051 / height=788 —— 页面状态里的
        //   裁剪结果尺寸）、collection_publish_video.json（无自定义图时两个尺寸字段都不出现）；
        //   docs/douyin/create.js（cover_timestamp = posterDelay * 1e3 ⇒ poster_delay 单位是秒）。
        sb.AppendLine("\n[28] 封面补齐：抖音封面帧/裁剪（2.5.1）");
        {
            static byte[] Png(int w, int h)
            {
                var b = new byte[24];
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
                b[11] = 0x0D;                                            // IHDR 长度 13（大端）
                b[12] = (byte)'I'; b[13] = (byte)'H'; b[14] = (byte)'D'; b[15] = (byte)'R';
                b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
                b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
                return b;
            }

            static byte[] Jpeg(int w, int h)
            {
                var b = new byte[17];
                b[0] = 0xFF; b[1] = 0xD8;                                  // SOI
                b[2] = 0xFF; b[3] = 0xE0; b[4] = 0; b[5] = 4; b[6] = 0; b[7] = 0;   // APP0（空段）
                b[8] = 0xFF; b[9] = 0xC0; b[10] = 0; b[11] = 17;           // SOF0
                b[12] = 8;                                                  // 精度
                b[13] = (byte)(h >> 8); b[14] = (byte)h;
                b[15] = (byte)(w >> 8); b[16] = (byte)w;
                return b;
            }

            static byte[] Gif(int w, int h)
            {
                var b = new byte[10];
                b[0] = (byte)'G'; b[1] = (byte)'I'; b[2] = (byte)'F';
                b[3] = (byte)'8'; b[4] = (byte)'9'; b[5] = (byte)'a';
                b[6] = (byte)w; b[7] = (byte)(w >> 8);
                b[8] = (byte)h; b[9] = (byte)(h >> 8);
                return b;
            }

            static byte[] Bmp(int w, int h)
            {
                var b = new byte[26];
                b[0] = (byte)'B'; b[1] = (byte)'M';
                b[14] = 40;   // DIB 头大小（BITMAPINFOHEADER）
                b[18] = (byte)w; b[19] = (byte)(w >> 8); b[20] = (byte)(w >> 16); b[21] = (byte)(w >> 24);
                b[22] = (byte)h; b[23] = (byte)(h >> 8); b[24] = (byte)(h >> 16); b[25] = (byte)(h >> 24);
                return b;
            }

            static byte[] WebpVp8X(int w, int h)
            {
                var b = new byte[30];
                b[0] = (byte)'R'; b[1] = (byte)'I'; b[2] = (byte)'F'; b[3] = (byte)'F';
                b[8] = (byte)'W'; b[9] = (byte)'E'; b[10] = (byte)'B'; b[11] = (byte)'P';
                b[12] = (byte)'V'; b[13] = (byte)'P'; b[14] = (byte)'8'; b[15] = (byte)'X';
                b[16] = 10;   // chunk 大小
                var wm = w - 1; var hm = h - 1;
                b[24] = (byte)wm; b[25] = (byte)(wm >> 8); b[26] = (byte)(wm >> 16);
                b[27] = (byte)hm; b[28] = (byte)(hm >> 8); b[29] = (byte)(hm >> 16);
                return b;
            }

            static byte[] WebpVp8L(int w, int h)
            {
                var b = new byte[25];
                b[0] = (byte)'R'; b[1] = (byte)'I'; b[2] = (byte)'F'; b[3] = (byte)'F';
                b[8] = (byte)'W'; b[9] = (byte)'E'; b[10] = (byte)'B'; b[11] = (byte)'P';
                b[12] = (byte)'V'; b[13] = (byte)'P'; b[14] = (byte)'8'; b[15] = (byte)'L';
                b[20] = 0x2F;   // 无损签名
                var bits = (uint)((w - 1) | ((h - 1) << 14));
                b[21] = (byte)bits; b[22] = (byte)(bits >> 8);
                b[23] = (byte)(bits >> 16); b[24] = (byte)(bits >> 24);
                return b;
            }

            var png = ImageDimensionProbe.TryRead(Png(1051, 788));
            Check("图片尺寸：PNG 1051×788", png?.Width == 1051 && png?.Height == 788, $"{png}");
            var jpg = ImageDimensionProbe.TryRead(Jpeg(1920, 1080));
            Check("图片尺寸：JPEG 1920×1080", jpg?.Width == 1920 && jpg?.Height == 1080, $"{jpg}");
            var gif = ImageDimensionProbe.TryRead(Gif(320, 240));
            Check("图片尺寸：GIF 320×240", gif?.Width == 320 && gif?.Height == 240, $"{gif}");
            var bmp = ImageDimensionProbe.TryRead(Bmp(640, 480));
            Check("图片尺寸：BMP 640×480", bmp?.Width == 640 && bmp?.Height == 480, $"{bmp}");
            var vp8x = ImageDimensionProbe.TryRead(WebpVp8X(800, 600));
            Check("图片尺寸：WebP(VP8X) 800×600", vp8x?.Width == 800 && vp8x?.Height == 600, $"{vp8x}");
            var vp8l = ImageDimensionProbe.TryRead(WebpVp8L(1051, 788));
            Check("图片尺寸：WebP(VP8L) 1051×788", vp8l?.Width == 1051 && vp8l?.Height == 788, $"{vp8l}");
            Check("图片尺寸：乱码 → null（不抛）",
                ImageDimensionProbe.TryRead("not an image"u8) is null);
            Check("图片尺寸：截断的 JPEG → null（不抛）",
                ImageDimensionProbe.TryRead(Jpeg(800, 600).AsSpan(0, 6)) is null);
            Check("图片尺寸：空数据 → null",
                ImageDimensionProbe.TryRead(ReadOnlySpan<byte>.Empty) is null);

            var coverFile = Path.Combine(Path.GetTempPath(), "newapp-selftest-t28.png");
            File.WriteAllBytes(coverFile, Png(1051, 788));
            try
            {
                var fromFile = ImageDimensionProbe.TryReadFile(coverFile);
                Check("图片尺寸：读文件（封面选择路径）",
                    fromFile?.Width == 1051 && fromFile?.Height == 788, $"{fromFile}");
                Check("图片尺寸：文件不存在 → null",
                    ImageDimensionProbe.TryReadFile(
                        Path.Combine(Path.GetTempPath(), "newapp-no-such-t28.png")) is null);
            }
            finally { File.Delete(coverFile); }

            var noCover = CoverMapping.Douyin(4500, 1080, 1440, hasUploadedCover: false, (1051, 788));
            Check("抖音封面：没有自定义封面 → 帧照发、裁剪不下发",
                noCover.FrameMs == 4500 && noCover.CropWidth is null && noCover.CropHeight is null,
                $"{noCover}");

            var withCover = CoverMapping.Douyin(4500, null, null, hasUploadedCover: true, (1051, 788));
            Check("抖音封面：有封面图 → poster_delay 不发、裁剪按图片实际像素",
                withCover.FrameMs is null && withCover.CropWidth == 1051 && withCover.CropHeight == 788,
                $"{withCover}");

            var manualCrop = CoverMapping.Douyin(4500, 1080, 1440, hasUploadedCover: true, (1051, 788));
            Check("抖音封面：有封面图 + 用户填裁剪 → 用用户的",
                manualCrop.FrameMs is null && manualCrop.CropWidth == 1080 && manualCrop.CropHeight == 1440,
                $"{manualCrop}");

            var unknownSize = CoverMapping.Douyin(null, null, null, hasUploadedCover: true, null);
            Check("抖音封面：有封面图但读不出尺寸 → 裁剪不下发",
                unknownSize.FrameMs is null && unknownSize.CropWidth is null
                && unknownSize.CropHeight is null, $"{unknownSize}");

            // 组装层：毫秒 → poster_delay 秒；poster + 裁剪尺寸落位（cover 不再发 uri）
            var builtFrame = DouyinVideoService.BuildCreateItem(new CreateDouyinWorkRequest(
                VideoId: "v-t28", CoverFrameMs: 4500));
            Check("抖音组装：封面帧 4500ms → poster_delay=4（秒）",
                builtFrame.Cover.PosterDelay == 4 && builtFrame.Cover.Poster is null,
                $"{builtFrame.Cover.PosterDelay}");

            var builtCover = DouyinVideoService.BuildCreateItem(new CreateDouyinWorkRequest(
                VideoId: "v-t28", CoverUri: "tos-cn-i-jm8ajry58r/t28",
                CoverCropWidth: 1051, CoverCropHeight: 788));
            Check("抖音组装：自定义封面 → poster + 裁剪尺寸",
                builtCover.Cover.Poster == "tos-cn-i-jm8ajry58r/t28"
                && builtCover.Cover.CustomCoverImageWidth == 1051
                && builtCover.Cover.CustomCoverImageHeight == 788);
        }

        sb.AppendLine("\n[29] 封面补齐：快手自定义封面（2.5.2）");
        {
            // 响应解析 —— 端点抓包未覆盖，钉住两种兼容形状与「认不出就报错」的口径
            var key = KuaishouCoverService.ParseCoverKey(
                "{\"result\":1,\"data\":{\"coverKey\":\"cp_video_upload_cover_t29.jpg\"}}");
            Check("快手封面：解析 data.coverKey", key == "cp_video_upload_cover_t29.jpg", key);

            var bare = KuaishouCoverService.ParseCoverKey(
                "{\"result\":1,\"data\":\"cp_video_upload_cover_t29b.jpg\"}");
            Check("快手封面：data 直接是字符串也认", bare == "cp_video_upload_cover_t29b.jpg", bare);

            var resultMsg = "";
            try { KuaishouCoverService.ParseCoverKey("{\"result\":2,\"message\":\"图片格式不支持\"}"); }
            catch (KuaishouApiException ex) { resultMsg = ex.Message; }
            Check("快手封面：result≠1 → 报出平台 message",
                resultMsg.Contains("图片格式不支持") && resultMsg.Contains("result=2"), resultMsg);

            var shapeMsg = "";
            try { KuaishouCoverService.ParseCoverKey("{\"result\":1,\"data\":{}}"); }
            catch (KuaishouApiException ex) { shapeMsg = ex.Message; }
            Check("快手封面：没有 coverKey → 报错并带原始响应",
                shapeMsg.Contains("coverKey") && shapeMsg.Contains("原文"), shapeMsg);

            var badJsonMsg = "";
            try { KuaishouCoverService.ParseCoverKey("<html>not json"); }
            catch (KuaishouApiException ex) { badJsonMsg = ex.Message; }
            Check("快手封面：非 JSON → 报错（不静默）", badJsonMsg.Contains("不是 JSON"), badJsonMsg);

            // 取值规则：有自定义封面 → 新 Key + 帧归 0；没有 → 默认 Key + 帧照发
            var noCover = KuaishouCoverService.ResolveCover("default-key", null, 4500);
            Check("快手封面：没有自定义封面 → 默认 Key + 帧照发",
                noCover.CoverKey == "default-key" && noCover.CoverFrameMs == 4500, $"{noCover}");

            var withCover = KuaishouCoverService.ResolveCover(
                "default-key", "cp_video_upload_cover_t29.jpg", 4500);
            Check("快手封面：上传成功 → 新 Key + coverTimeStamp 归 0",
                withCover.CoverKey == "cp_video_upload_cover_t29.jpg" && withCover.CoverFrameMs == 0,
                $"{withCover}");

            var blankCover = KuaishouCoverService.ResolveCover("default-key", "  ", 3000);
            Check("快手封面：空上传结果按未上传处理",
                blankCover.CoverKey == "default-key" && blankCover.CoverFrameMs == 3000, $"{blankCover}");

            // 组装层：coverKey / coverTimeStamp 落位
            var uploadReq = KuaishouPublishService.BuildSubmitRequest(new KuaishouPublishRequest
            {
                FileId = 1, CoverKey = withCover.CoverKey, CoverFrameMs = withCover.CoverFrameMs,
                MediaId = "media-t29", PhotoIdStr = "photo-t29", VideoDuration = 6208,
            });
            Check("快手组装：自定义封面 → coverKey 用新值 + coverTimeStamp=0",
                uploadReq.CoverKey == "cp_video_upload_cover_t29.jpg"
                && uploadReq.CoverTimeStamp == 0, $"{uploadReq.CoverKey}/{uploadReq.CoverTimeStamp}");

            var frameReq = KuaishouPublishService.BuildSubmitRequest(new KuaishouPublishRequest
            {
                FileId = 1, CoverKey = noCover.CoverKey, CoverFrameMs = noCover.CoverFrameMs,
                MediaId = "media-t29", PhotoIdStr = "photo-t29", VideoDuration = 6208,
            });
            Check("快手组装：默认封面 → coverTimeStamp=4500（毫秒原样）",
                frameReq.CoverKey == "default-key" && frameReq.CoverTimeStamp == 4500,
                $"{frameReq.CoverKey}/{frameReq.CoverTimeStamp}");

            // multipart wire 形体：字段名 file（页面 postCoverUploadV4({ file: File })）
            using var form = KuaishouCoverService.BuildMultipart("cover.jpg", "image/jpeg", [1, 2, 3]);
            var wire = await form.ReadAsStringAsync();
            Check("快手封面：multipart 字段名 file + 文件名",
                wire.Contains("name=file") && wire.Contains("filename=cover.jpg"), wire);
            Check("快手封面：Content-Type 是 multipart/form-data",
                form.Headers.ContentType?.MediaType == "multipart/form-data",
                form.Headers.ContentType?.MediaType ?? "null");

            Check("快手封面：扩展名 → MIME",
                KuaishouCoverService.GuessMimeType("a.png") == "image/png"
                && KuaishouCoverService.GuessMimeType("a.webp") == "image/webp"
                && KuaishouCoverService.GuessMimeType("a.heic") == "image/jpeg",
                KuaishouCoverService.GuessMimeType("a.heic"));
        }

        sb.AppendLine("\n[30] 封面补齐：小红书自定义封面（2.5.3）");
        {
            static Dictionary<string, object?> CoverOf(Dictionary<string, object?> note)
                => (Dictionary<string, object?>)((Dictionary<string, object?>)note["video_info"]!)["cover"]!;
            static Dictionary<string, object?> FrameOf(Dictionary<string, object?> note)
                => (Dictionary<string, object?>)CoverOf(note)["frame"]!;

            // permit 抓包形状：data.uploadTempPermits[0].fileIds[0]（2026-11-05 image_permit）
            const string permitJson = """
                {"code":0,"success":true,"data":{"uploadTempPermits":[
                  {"uploadAddr":"ros-upload.xiaohongshu.com","token":"tok:t30",
                   "bucket":"unknown","region":"unknown",
                   "fileIds":["spectrum/cover-t30"]}]}}
                """;
            using var permitDoc = JsonDocument.Parse(permitJson);
            var permit = XhsUploadService.ParseFirstPermit(permitDoc.RootElement);
            Check("小红书封面：解析 permit → fileId / uploadAddr / fileKey",
                permit.FileId == "spectrum/cover-t30"
                && permit.UploadAddr == "ros-upload.xiaohongshu.com"
                && permit.FileKey == "cover-t30" && permit.Token == "tok:t30", $"{permit}");

            using var permitBareDoc = JsonDocument.Parse(
                """{"uploadTempPermits":[{"uploadAddr":"ros-upload-d4.xhscdn.com","token":"t","fileIds":["raw-id"]}]}""");
            var barePermit = XhsUploadService.ParseFirstPermit(permitBareDoc.RootElement);
            Check("小红书封面：permit 无 data 外壳也认 + 无斜杠 fileId",
                barePermit.FileId == "raw-id" && barePermit.FileKey == "raw-id", $"{barePermit}");

            var noPermitMsg = "";
            using (var emptyDoc = JsonDocument.Parse("""{"data":{}}"""))
            {
                try { XhsUploadService.ParseFirstPermit(emptyDoc.RootElement); }
                catch (XhsPublishException ex) { noPermitMsg = ex.Message; }
            }
            Check("小红书封面：permit 没有 uploadTempPermits → 报错",
                noPermitMsg.Contains("uploadTempPermits"), noPermitMsg);

            // 图片 permit query：scene=image / file_count=1 / **不带 file_format**（与视频不同）
            var imageQuery = XhsUploadService.BuildImagePermitQuery();
            Check("小红书封面：图片 permit scene=image / file_count=1 / 无 file_format",
                imageQuery["biz_name"] == "spectrum" && imageQuery["scene"] == "image"
                && imageQuery["file_count"] == "1" && !imageQuery.ContainsKey("file_format"),
                string.Join(",", imageQuery.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));

            // 取值规则：上传成功 → 新 fileId + 帧归 0；没有 → 不指定封面 + 帧照发
            var noCover = XhsPublishService.ResolveCover(null, 4500);
            Check("小红书封面：没有自定义封面 → 平台默认 + 帧照发",
                noCover.CoverFileId is null && noCover.CoverFrameMs == 4500, $"{noCover}");

            var withCover = XhsPublishService.ResolveCover("spectrum/cover-t30", 4500);
            Check("小红书封面：上传成功 → 新 fileId + 帧归 0",
                withCover.CoverFileId == "spectrum/cover-t30" && withCover.CoverFrameMs == 0,
                $"{withCover}");

            var blankCover = XhsPublishService.ResolveCover("  ", 3000);
            Check("小红书封面：空上传结果按未上传处理",
                blankCover.CoverFileId is null && blankCover.CoverFrameMs == 3000, $"{blankCover}");

            // 组装层：cover.file_id / cover.frame.ts（秒）/ is_upload / is_user_select
            var defaultNote = XhsPublishService.BuildNotePayload(new XhsPublishRequest
            {
                Title = "t30", FileId = "spectrum/video-t30",
            });
            var defaultCover = CoverOf(defaultNote);
            var defaultFrame = FrameOf(defaultNote);
            Check("小红书组装：默认封面 → 视频 fileId + frame 全 false",
                defaultCover["file_id"] as string == "spectrum/video-t30"
                && Convert.ToDouble(defaultFrame["ts"]) == 0d
                && defaultFrame["is_upload"] is false
                && defaultFrame["is_user_select"] is false,
                $"{defaultCover["file_id"]}/{defaultFrame["ts"]}");

            var frameNote = XhsPublishService.BuildNotePayload(new XhsPublishRequest
            {
                Title = "t30", FileId = "spectrum/video-t30", CoverFrameMs = 4500,
            });
            var frame = FrameOf(frameNote);
            Check("小红书组装：封面帧 4500ms → ts=4.5 秒 + is_user_select",
                Convert.ToDouble(frame["ts"]) == 4.5
                && frame["is_user_select"] is true && frame["is_upload"] is false,
                $"{frame["ts"]}/{frame["is_user_select"]}");

            var uploadedNote = XhsPublishService.BuildNotePayload(new XhsPublishRequest
            {
                Title = "t30", FileId = "spectrum/video-t30",
                CoverFileId = "spectrum/cover-t30", CoverFrameMs = 4500,
            });
            var uploadedCover = CoverOf(uploadedNote);
            var uploadedFrame = FrameOf(uploadedNote);
            Check("小红书组装：自定义封面 → file_id 用新值 + ts=0/is_upload",
                uploadedCover["file_id"] as string == "spectrum/cover-t30"
                && Convert.ToDouble(uploadedFrame["ts"]) == 0d
                && uploadedFrame["is_upload"] is true
                && uploadedFrame["is_user_select"] is false,
                $"{uploadedCover["file_id"]}/{uploadedFrame["ts"]}");

            var zeroNote = XhsPublishService.BuildNotePayload(new XhsPublishRequest
            {
                Title = "t30", FileId = "spectrum/video-t30", CoverFrameMs = 0,
            });
            var zeroFrame = FrameOf(zeroNote);
            Check("小红书组装：帧 0 → is_user_select=false（默认同款）",
                Convert.ToDouble(zeroFrame["ts"]) == 0d
                && zeroFrame["is_user_select"] is false, $"{zeroFrame["ts"]}");
        }

        sb.AppendLine("\n[31] 封面补齐：视频号封面直传 / 首帧兜底 / coverEdit 入口（2.5.4）");
        {
            // 封面来源优先级：用户封面 → 首帧 → 占位图（2026-11-05 抓包：页面发布前
            // 会上传一张 20304 图片，post_create 的五个封面子字段都用它的 URL）
            var coverTmp = Path.Combine(Path.GetTempPath(), $"wechat_cover_t31_{Guid.NewGuid():N}");
            Directory.CreateDirectory(coverTmp);
            try
            {
                var custom = Path.Combine(coverTmp, "custom.jpg");
                var frame = Path.Combine(coverTmp, "frame.jpg");
                File.WriteAllBytes(custom, [0xFF, 0xD8, 0xFF, 0xD9]);
                File.WriteAllBytes(frame, [0xFF, 0xD8, 0xFF, 0xD9]);
                var missing = Path.Combine(coverTmp, "missing.jpg");

                Check("视频号封面：用户封面优先",
                    WechatVideoContentService.ChooseCoverSource(custom, frame)
                        == WechatVideoCoverSource.CustomCover);

                Check("视频号封面：没选封面 → 首帧兜底",
                    WechatVideoContentService.ChooseCoverSource(null, frame)
                        == WechatVideoCoverSource.FirstFrame);

                Check("视频号封面：封面文件被删 → 退回首帧（不报错）",
                    WechatVideoContentService.ChooseCoverSource(missing, frame)
                        == WechatVideoCoverSource.FirstFrame);

                Check("视频号封面：首帧也抽不出来 → 占位图兜底",
                    WechatVideoContentService.ChooseCoverSource(null, null)
                        == WechatVideoCoverSource.Placeholder
                    && WechatVideoContentService.ChooseCoverSource("  ", missing)
                        == WechatVideoCoverSource.Placeholder);
            }
            finally
            {
                try { Directory.Delete(coverTmp, true); } catch { /* best effort */ }
            }

            // post_create：五个封面子字段同源（2026-11-05 抓包 media 里 5 个 URL 完全相同）
            const string coverUrl31 =
                "https://finder.video.qq.com/251/20304/stodownload?encfilekey=t31";
            var pubBody31 = WechatVideoContentService.BuildPostBody(
                new WechatVideoPublishRequest(
                    VideoUrl: "https://finder.video.qq.com/251/20302/stodownload?encfilekey=v31",
                    FileSize: 1024, ThumbUrl: coverUrl31, CoverUrl: coverUrl31,
                    Description: "t31", Duration: 3, Width: 1080, Height: 1920,
                    VideoClipTaskId: "clip31"),
                "clip31", "finder31", DateTimeOffset.UnixEpoch);
            var media31 = ((Dictionary<string, object?>[])
                ((Dictionary<string, object?>)pubBody31["objectDesc"]!)["media"]!)[0];
            var coverFields = new[]
            {
                "thumbUrl", "fullThumbUrl", "coverUrl", "fullCoverUrl", "shareCoverUrl",
            };
            Check("视频号封面：post_create 五个封面字段 = 上传后的图片 URL",
                coverFields.All(f => media31[f] as string == coverUrl31),
                string.Join(",", coverFields.Select(f => media31[f] as string ?? "null")));

            // CoverUrl 空时组装层回落 ThumbUrl（老载荷兼容；管线本身保证两者都填）
            var thumbOnlyBody = WechatVideoContentService.BuildPostBody(
                new WechatVideoPublishRequest(
                    VideoUrl: "v", FileSize: 1, ThumbUrl: "thumb-only", Description: "t31"),
                "clip31", null, DateTimeOffset.UnixEpoch);
            var thumbOnlyMedia = ((Dictionary<string, object?>[])
                ((Dictionary<string, object?>)thumbOnlyBody["objectDesc"]!)["media"]!)[0];
            Check("视频号封面：CoverUrl 空时回落 ThumbUrl（组装层兼容）",
                thumbOnlyMedia["coverUrl"] as string == "thumb-only",
                thumbOnlyMedia["coverUrl"] as string ?? "null");

            // coverEdit：页面地址（接口说明书 §9）与三个封面位
            Check("视频号 coverEdit：页面地址带转义后的 objectId",
                WechatVideoContentService.BuildCoverEditPageUrl("export/UzFfBgAAx+2/3")
                    == "https://channels.weixin.qq.com/platform/post/coverEdit"
                       + "?objectId=export%2FUzFfBgAAx%2B2%2F3",
                WechatVideoContentService.BuildCoverEditPageUrl("export/UzFfBgAAx+2/3") ?? "null");

            Check("视频号 coverEdit：没有 objectId → 不给页面地址",
                WechatVideoContentService.BuildCoverEditPageUrl(null) is null
                && WechatVideoContentService.BuildCoverEditPageUrl("  ") is null);

            var coverSlots = WechatVideoContentService.CoverEditSlots;
            Check("视频号 coverEdit：三个封面位（视频封面 / 个人主页 3:4 / 分享 4:3）",
                coverSlots.Count == 3
                && coverSlots[0].Label == "视频封面" && coverSlots[0].AspectRatio is null
                && coverSlots[1].AspectRatio is { } p34 && Math.Abs(p34 - 0.75) < 1e-9
                && coverSlots[2].AspectRatio is { } s43 && Math.Abs(s43 - 4.0 / 3.0) < 1e-9,
                string.Join(" / ", coverSlots.Select(s => $"{s.Label}:{s.AspectRatio}")));

            Check("视频号 coverEdit：保存接口未抓包 → 明确标记不支持（不猜 wire）",
                !WechatVideoContentService.PublishedCoverEditSupported
                && WechatVideoContentService.PublishedCoverEditUnavailableReason.Contains("抓包")
                && WechatVideoContentService.PublishedCoverEditUnavailableReason.Contains("手动"),
                WechatVideoContentService.PublishedCoverEditUnavailableReason);
        }

        sb.AppendLine("\n[32] 封面补齐：B站 16:9 + 4:3 封面上传（2.5.5）");
        {
            // Cookie → csrf（bili_jct）；缺了要引导重新登录
            const string cookie32 =
                "SESSDATA=abc123; bili_jct=0123456789abcdef0123456789abcdef; DedeUserID=42";
            Check("B站封面：从 Cookie 取 bili_jct",
                BilibiliUploadService.RequireCsrf(cookie32)
                    == "0123456789abcdef0123456789abcdef",
                BilibiliUploadService.RequireCsrf(cookie32));

            var noCsrf32 = false;
            try
            {
                BilibiliUploadService.RequireCsrf("SESSDATA=abc123");
            }
            catch (BilibiliApiException ex)
                when (ex.Message.Contains("bili_jct") && ex.Message.Contains("重新登录"))
            {
                noCsrf32 = true;
            }
            Check("B站封面：Cookie 缺 bili_jct → 明确引导重新登录", noCsrf32);

            // 封面字节 → data URL（抓包原文：data:image/jpeg;base64,…）
            byte[] jpeg32 = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
            var dataUrl32 = BilibiliUploadService.BuildCoverDataUrl(jpeg32);
            Check("B站封面：JPEG → data:image/jpeg;base64（可还原字节）",
                dataUrl32.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)
                && Convert.FromBase64String(dataUrl32["data:image/jpeg;base64,".Length..])
                    .SequenceEqual(jpeg32),
                dataUrl32);

            Check("B站封面：PNG / GIF / BMP / WebP 文件头都能识别",
                BilibiliUploadService.DetectImageMime([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
                    == "image/png"
                && BilibiliUploadService.DetectImageMime("GIF89a"u8) == "image/gif"
                && BilibiliUploadService.DetectImageMime([0x42, 0x4D, 0x00, 0x00]) == "image/bmp"
                && BilibiliUploadService.DetectImageMime("RIFF1234WEBPVP8 "u8) == "image/webp");

            var badImage32 = false;
            try
            {
                BilibiliUploadService.BuildCoverDataUrl("not an image"u8);
            }
            catch (BilibiliApiException ex) when (ex.Message.Contains("不是可识别的图片"))
            {
                badImage32 = true;
            }
            Check("B站封面：非图片字节 → 拒绝上传（不猜）", badImage32);

            // URL：参数顺序 + axios 编码（冒号/逗号不转义）与抓包同形
            var expectedUrl32 =
                "https://member.bilibili.com/x/vu/web/cover/up?t=1793786067437"
                + "&x-bili-locale-json=%7B%22c_locale%22:%7B%22language%22:%22zh%22,"
                + "%22script%22:%22Hans%22%7D,%22always_translate%22:false%7D"
                + "&x-bili-device-req-json=%7B%22platform%22:%22web%22,%22device%22:%22pc%22,"
                + "%22mobi_app%22:%22web_cn%22,%22spmid%22:%220.0%22%7D"
                + "&csrf=0123456789abcdef0123456789abcdef";
            Check("B站封面：请求 URL 与抓包同形（t / locale / device / csrf）",
                BilibiliUploadService.BuildCoverUploadUrl(
                    "0123456789abcdef0123456789abcdef", 1793786067437) == expectedUrl32);

            // multipart：两个纯文本字段 cover / csrf，无 filename、无 Content-Type（抓包同形）
            using (var form32 = BilibiliUploadService.BuildCoverForm(
                       dataUrl32, "0123456789abcdef0123456789abcdef"))
            {
                var wire32 = form32.ReadAsStringAsync().GetAwaiter().GetResult();
                Check("B站封面：multipart 两个文本字段 cover / csrf（无 filename）",
                    wire32.Contains("name=cover") && wire32.Contains("name=csrf")
                    && !wire32.Contains("filename=")
                    && !wire32.Contains("Content-Type: text/plain"),
                    wire32.Length > 160 ? wire32[..160] : wire32);
                Check("B站封面：cover 字段 = base64 data URL、csrf 字段 = bili_jct",
                    wire32.Contains(dataUrl32)
                    && wire32.Contains("0123456789abcdef0123456789abcdef"));
            }

            // 响应解析：成功 / code≠0 / 缺 data.url / 非 JSON
            Check("B站封面：响应 data.url 解析",
                BilibiliUploadService.ParseCoverUrl(
                    """{"code":0,"message":"OK","ttl":1,"data":{"url":"https://archive.biliimg.com/bfs/archive/9d1a.jpg"}}""")
                == "https://archive.biliimg.com/bfs/archive/9d1a.jpg");

            var codeError32 = false;
            try
            {
                BilibiliUploadService.ParseCoverUrl(
                    """{"code":-101,"message":"账号未登录"}""");
            }
            catch (BilibiliApiException ex)
                when (ex.Code == -101 && ex.Message.Contains("账号未登录")
                      && ex.Message.Contains("重新登录"))
            {
                codeError32 = true;
            }
            Check("B站封面：code≠0 → 报平台 message（-101 追加登录引导）", codeError32);

            var noUrl32 = false;
            try
            {
                BilibiliUploadService.ParseCoverUrl("""{"code":0,"data":{}}""");
            }
            catch (BilibiliApiException ex) when (ex.Message.Contains("data.url"))
            {
                noUrl32 = true;
            }
            Check("B站封面：没有 data.url → 报错并附原文", noUrl32);

            var nonJson32 = false;
            try
            {
                BilibiliUploadService.ParseCoverUrl("<html>bad</html>");
            }
            catch (BilibiliApiException ex) when (ex.Message.Contains("不是 JSON"))
            {
                nonJson32 = true;
            }
            Check("B站封面：非 JSON 响应 → 报错并附原文", nonJson32);

            // add/v3 落点：cover = 16:9，cover43 = 4:3（不能对调）
            var (coverField32, cover43Field32) = new BilibiliCover(
                "https://archive.biliimg.com/bfs/archive/9d1a.jpg",
                "https://archive.biliimg.com/bfs/archive/4ead.jpg").ToAddV3CoverFields();
            Check("B站封面：add/v3 落点 cover=16:9 / cover43=4:3",
                coverField32.EndsWith("9d1a.jpg", StringComparison.Ordinal)
                && cover43Field32.EndsWith("4ead.jpg", StringComparison.Ordinal),
                coverField32 + " / " + cover43Field32);

            // 双比例准备：滤镜（居中裁切 + 缩到 1080）
            Check("B站封面：16:9 滤镜（居中裁切 + 缩到 1080）",
                BilibiliCoverPreparer.Aspect16x9Filter.Contains("ih*16/9")
                && BilibiliCoverPreparer.Aspect16x9Filter.Contains("iw*9/16")
                && BilibiliCoverPreparer.Aspect16x9Filter.Contains("scale=1080:-2"),
                BilibiliCoverPreparer.Aspect16x9Filter);
            Check("B站封面：4:3 滤镜（居中裁切 + 缩到 1080）",
                BilibiliCoverPreparer.Aspect43Filter.Contains("ih*4/3")
                && BilibiliCoverPreparer.Aspect43Filter.Contains("iw*3/4")
                && BilibiliCoverPreparer.Aspect43Filter.Contains("scale=1080:-2"),
                BilibiliCoverPreparer.Aspect43Filter);

            var missingSource32 = false;
            try
            {
                BilibiliCoverPreparer.PrepareAsync(
                    Path.Combine(Path.GetTempPath(), $"no_such_cover_{Guid.NewGuid():N}.jpg"),
                    Path.GetTempPath()).GetAwaiter().GetResult();
            }
            catch (IOException ex) when (ex.Message.Contains("封面图不存在"))
            {
                missingSource32 = true;
            }
            Check("B站封面：源图不存在 → 明确报错（不起 ffmpeg）", missingSource32);

            // 实跑：1280×800 测试图 → 两张 1080 宽封面（16:9 / 4:3）
            var prepDir32 = Path.Combine(Path.GetTempPath(), $"bili_cover_t32_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(prepDir32);
                var source32 = Path.Combine(prepDir32, "source.png");
                var (_, genErr32, genCode32) = FfmpegCli.RunAsync(new[]
                {
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc=size=1280x800",
                    "-frames:v", "1", source32,
                }).GetAwaiter().GetResult();
                Check("B站封面：测试源图生成（ffmpeg）",
                    genCode32 == 0 && File.Exists(source32), genErr32);

                if (File.Exists(source32))
                {
                    var pair32 = BilibiliCoverPreparer
                        .PrepareAsync(source32, prepDir32).GetAwaiter().GetResult();
                    var dim16932 = ImageDimensionProbe.TryReadFile(pair32.Path16x9);
                    var dim4332 = ImageDimensionProbe.TryReadFile(pair32.Path43);
                    Check("B站封面：实跑裁出 16:9 封面（1080 宽，比例≈16:9）",
                        dim16932 is { } d16932 && d16932.Width == 1080
                        && Math.Abs(d16932.Width / (double)d16932.Height - 16.0 / 9.0) < 0.01,
                        dim16932?.ToString() ?? "null");
                    Check("B站封面：实跑裁出 4:3 封面（1080×810，比例≈4:3）",
                        dim4332 is { } d4332 && d4332.Width == 1080
                        && Math.Abs(d4332.Width / (double)d4332.Height - 4.0 / 3.0) < 0.01,
                        dim4332?.ToString() ?? "null");
                }
            }
            finally
            {
                try { Directory.Delete(prepDir32, true); } catch { /* best effort */ }
            }
        }

        sb.AppendLine("\n[33] 封面补齐：头条视频封面必填（TOS → VideoInfo.ThumbUri）（2.5.6）");
        {
            var dir33 = Path.Combine(Path.GetTempPath(), $"tt_cover_t33_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir33);
            try
            {
                var svc33 = new ToutiaoVideoCoverService(new PlatformOptions());

                string? CoverError33(Func<Task> action)
                {
                    try
                    {
                        action().GetAwaiter().GetResult();
                        return null;
                    }
                    catch (ToutiaoVideoCoverException ex)
                    {
                        return ex.Message;
                    }
                }

                Check("头条封面：封面链路的 Referer = 视频上传页（抓包同值）",
                    ToutiaoVideoCoverService.VideoCoverReferer
                        == "https://mp.toutiao.com/profile_v4/xigua/upload-video",
                    ToutiaoVideoCoverService.VideoCoverReferer);

                // ── 必填：空路径 / 空白 —— 在读文件与发请求之前就拦 ──
                var blank33 = CoverError33(() => svc33.UploadAsync("cookie=1", null, "42", "csrf"));
                Check("头条封面：空路径 → 必填文案（平台硬要求）",
                    blank33 is not null && blank33.Contains("必须上传封面"),
                    blank33 ?? "未抛异常");

                var ws33 = CoverError33(() => svc33.UploadAsync("cookie=1", "   ", "42", "csrf"));
                Check("头条封面：空白路径同样拦（不只判 null）",
                    ws33 is not null && ws33.Contains("必须上传封面"),
                    ws33 ?? "未抛异常");

                Check("头条封面：必填文案常量与异常同源",
                    ToutiaoVideoCoverService.CoverRequiredMessage.Contains("封面"));

                // ── 文件级校验（全部在发请求之前） ──
                var missing33 = Path.Combine(dir33, $"no_such_{Guid.NewGuid():N}.jpg");
                var missingErr33 = CoverError33(
                    () => svc33.UploadAsync("cookie=1", missing33, "42", "csrf"));
                Check("头条封面：文件不存在 → 明确报错（不发请求）",
                    missingErr33 is not null && missingErr33.Contains("不存在"),
                    missingErr33 ?? "未抛异常");

                var empty33 = Path.Combine(dir33, "empty.jpg");
                File.WriteAllBytes(empty33, []);
                var emptyErr33 = CoverError33(
                    () => svc33.UploadAsync("cookie=1", empty33, "42", "csrf"));
                Check("头条封面：空文件 → 明确报错",
                    emptyErr33 is not null && emptyErr33.Contains("空文件"),
                    emptyErr33 ?? "未抛异常");

                // SetLength 造稀疏文件，不真的写 10 MB
                var huge33 = Path.Combine(dir33, "huge.jpg");
                using (var fs33 = new FileStream(huge33, FileMode.Create, FileAccess.Write))
                    fs33.SetLength(11L * 1024 * 1024);
                var hugeErr33 = CoverError33(
                    () => svc33.UploadAsync("cookie=1", huge33, "42", "csrf"));
                Check("头条封面：超过本地保护上限 → 明确报错",
                    hugeErr33 is not null && hugeErr33.Contains("保护上限"),
                    hugeErr33 ?? "未抛异常");

                var text33 = Path.Combine(dir33, "not-image.jpg");
                File.WriteAllText(text33, "这不是图片");
                var textErr33 = CoverError33(
                    () => svc33.UploadAsync("cookie=1", text33, "42", "csrf"));
                Check("头条封面：非图片字节 → 拒绝上传（不猜格式）",
                    textErr33 is not null && textErr33.Contains("不是可识别的图片"),
                    textErr33 ?? "未抛异常");

                // ── 图片识别 + 尺寸（PNG / GIF / 垃圾字节） ──
                byte[] png33 =
                [
                    0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                    0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
                    0x00, 0x00, 0x03, 0x20,   // width  = 800
                    0x00, 0x00, 0x02, 0x58,   // height = 600
                ];
                var pngOk33 = ToutiaoVideoCoverService.TryReadCoverSize(png33, out var pw33, out var ph33);
                Check("头条封面：PNG 头读出 800×600",
                    pngOk33 && pw33 == 800 && ph33 == 600, $"{pw33}x{ph33}");

                byte[] gif33 = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
                    0x40, 0x01, 0xF0, 0x00];
                var gifOk33 = ToutiaoVideoCoverService.TryReadCoverSize(gif33, out var gw33, out var gh33);
                Check("头条封面：GIF 头读出 320×240",
                    gifOk33 && gw33 == 320 && gh33 == 240, $"{gw33}x{gh33}");

                Check("头条封面：垃圾字节 → 认不出（不猜默认 1280×720）",
                    !ToutiaoVideoCoverService.TryReadCoverSize(
                        "not an image"u8.ToArray(), out _, out _));

                // ── 发布字段映射（抓包行 4776：ThumbUri = TOS 对象名，ThumbUrl = blob） ──
                const string guid33 = "9388b39f-760a-485b-89be-ea9fa6a2c3f4";
                Check("头条封面：ThumbUrl 与页面同形（blob:https://mp.toutiao.com/{guid}）",
                    ToutiaoVideoCoverService.BuildThumbUrl(guid33)
                        == "blob:https://mp.toutiao.com/" + guid33);

                var auto33 = ToutiaoVideoCoverService.BuildThumbUrl();
                Check("头条封面：默认 ThumbUrl 每次生成新的 blob 地址",
                    auto33.StartsWith(ToutiaoVideoCoverService.BlobUrlPrefix, StringComparison.Ordinal)
                    && auto33.Length == ToutiaoVideoCoverService.BlobUrlPrefix.Length + 36
                    && auto33 != ToutiaoVideoCoverService.BuildThumbUrl(),
                    auto33);

                var mapped33 = ToutiaoVideoCoverService.FromUpload(
                    new ToutiaoImageUploadResult(
                        "tos-cn-i-e874c6/oQNC6FLTD9AD4beoAAAZoGfqR3AFr2EqgEsIGY", 1920, 1080),
                    sizeBytes: 44223, guid: guid33);
                var (thumbUri33, thumbUrl33) = mapped33.ToVideoInfoFields();
                Check("头条封面：ThumbUri 就是 TOS 对象名（抓包行 4776 同值）",
                    thumbUri33 == "tos-cn-i-e874c6/oQNC6FLTD9AD4beoAAAZoGfqR3AFr2EqgEsIGY",
                    thumbUri33);
                Check("头条封面：VideoInfo 两字段映射 + 尺寸/大小原样带出",
                    thumbUrl33 == "blob:https://mp.toutiao.com/" + guid33
                    && mapped33.Width == 1920 && mapped33.Height == 1080
                    && mapped33.SizeBytes == 44223);

                // ── 凭证缺失 → 同一条「打开账号窗口重新登录」引导（仍不发请求） ──
                var tinyPng33 = Path.Combine(dir33, "tiny.png");
                File.WriteAllBytes(tinyPng33, png33);
                var noCsrf33 = CoverError33(
                    () => svc33.UploadAsync("cookie=1", tinyPng33, "42", null));
                Check("头条封面：缺 secsdk 凭证 → 引导打开账号窗口重新登录（不发请求）",
                    noCsrf33 is not null && noCsrf33.Contains("secsdk")
                    && noCsrf33.Contains("重新登录"),
                    noCsrf33 ?? "未抛异常");

                var noCookie33 = CoverError33(
                    () => svc33.UploadAsync(null, tinyPng33, "42", "csrf"));
                Check("头条封面：缺 Cookie → 同一条登录引导",
                    noCookie33 is not null && noCookie33.Contains("重新登录"),
                    noCookie33 ?? "未抛异常");

                // ── 真实 JPEG（ffmpeg 生成）能读出尺寸 ──
                var real33 = Path.Combine(dir33, "real.jpg");
                var (_, genErr33, genCode33) = FfmpegCli.RunAsync(new[]
                {
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc=size=1280x720",
                    "-frames:v", "1", "-q:v", "3", real33,
                }).GetAwaiter().GetResult();
                Check("头条封面：ffmpeg 生成测试 JPEG", genCode33 == 0 && File.Exists(real33), genErr33);

                if (File.Exists(real33))
                {
                    var realBytes33 = File.ReadAllBytes(real33);
                    var realOk33 = ToutiaoVideoCoverService.TryReadCoverSize(
                        realBytes33, out var rw33, out var rh33);
                    Check("头条封面：真实 JPEG 读出 1280×720",
                        realOk33 && rw33 == 1280 && rh33 == 720, $"{rw33}x{rh33}");
                }
            }
            finally
            {
                try { Directory.Delete(dir33, true); } catch { /* best effort */ }
            }
        }

        sb.AppendLine("\n[34] 封面统一：CoverPath 缺失 → 自动抽首帧（2.5.7）");
        {
            var dir34 = Path.Combine(Path.GetTempPath(), $"cover_unify_t34_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir34);
            try
            {
                // ── 纯函数：用户封面 > 首帧 > 无（存在性由调用方传入，不碰磁盘） ──
                const string custom34 = @"D:\封面\用户选的.jpg";
                const string frame34 = @"C:\缓存\首帧.jpg";
                Check("封面统一：用户封面优先（选了图就不抽帧）",
                    PublishCoverResolver.Choose(custom34, true, frame34, true)
                        is { Source: PublishCoverSource.Custom, Path: custom34 });

                Check("封面统一：用户封面文件被删 → 退回首帧",
                    PublishCoverResolver.Choose(custom34, false, frame34, true)
                        is { Source: PublishCoverSource.FirstFrame, Path: frame34 });

                Check("封面统一：两者都没有 → None（平台默认封面继续）",
                    PublishCoverResolver.Choose(null, false, null, false)
                        is { Source: PublishCoverSource.None, Path: null });

                Check("封面统一：空白路径不算封面（不误判为已选）",
                    PublishCoverResolver.Choose("   ", true, null, false)
                        .Source == PublishCoverSource.None);

                var customFile34 = Path.Combine(dir34, "custom.jpg");
                File.WriteAllBytes(customFile34, [0xFF, 0xD8, 0xFF, 0xD9]);
                Check("封面统一：HasFile 核真实文件（路径在但文件被删 → false）",
                    new PublishCover(customFile34, PublishCoverSource.Custom).HasFile
                    && !new PublishCover(Path.Combine(dir34, "gone.jpg"),
                        PublishCoverSource.Custom).HasFile
                    && !new PublishCover(null, PublishCoverSource.None).HasFile);

                // ── ResolveAsync：不需要探测器的两条短路 ──
                var noProbe34 = await PublishCoverResolver.ResolveAsync(
                    Path.Combine(dir34, "no.mp4"), null, null, probe: null);
                Check("封面统一：没选图 + 没有探测器 → None（不阻断发布）",
                    noProbe34.Source == PublishCoverSource.None);

                var customResolved34 = await PublishCoverResolver.ResolveAsync(
                    Path.Combine(dir34, "no.mp4"), customFile34, null, probe: null);
                Check("封面统一：选了图且文件在 → Custom（不需要探测器）",
                    customResolved34 is { Source: PublishCoverSource.Custom }
                    && customResolved34.Path == customFile34);

                // 缺省抽帧点 = 0.5 秒（0 秒黑场的常见问题）
                Check("封面统一：默认抽帧点 = 0.5 秒（避开黑场）",
                    VideoMetaProbe.DefaultCoverFrameMs == 500,
                    VideoMetaProbe.DefaultCoverFrameMs.ToString());

                // ── 真实抽帧（ffmpeg 真跑；LGPL 构建无 libx264 → 用内置 mpeg4） ──
                var probe34 = new VideoMetaProbe(Path.Combine(dir34, "cache"));
                var v72034 = Path.Combine(dir34, "v720.mp4");
                var (_, genErr34, genCode34) = await FfmpegCli.RunAsync(new[]
                {
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc=size=1280x720:rate=10", "-t", "3",
                    "-pix_fmt", "yuv420p", "-c:v", "mpeg4", "-q:v", "5", v72034,
                });
                Check("封面统一：ffmpeg 生成测试视频（1280×720 / 3 秒）",
                    genCode34 == 0 && File.Exists(v72034), genErr34);

                if (File.Exists(v72034))
                {
                    var framePath34 = await probe34.ExtractCoverFrameAsync(v72034);
                    Check("封面统一：没选图 → 自动抽帧（默认 0.5 秒）",
                        framePath34 is not null && File.Exists(framePath34),
                        framePath34 ?? "null");

                    var dim34 = ImageDimensionProbe.TryReadFile(framePath34);
                    Check("封面统一：抽帧是发布质量（1280×720 原样，不是 320px 卡片缩略图）",
                        dim34 is { Width: 1280, Height: 720 },
                        dim34 is { } d34 ? $"{d34.Width}x{d34.Height}" : "读不出");

                    Check("封面统一：封面帧缓存在 thumbs/cover_*.jpg（与缩略图分前缀）",
                        framePath34 is not null
                        && Path.GetFileName(framePath34).StartsWith("cover_", StringComparison.Ordinal)
                        && Path.GetFileName(framePath34).EndsWith(".jpg", StringComparison.OrdinalIgnoreCase),
                        framePath34 is null ? "null" : Path.GetFileName(framePath34));

                    // 同一视频 + 同一时间点 → 缓存命中（同一条视频多平台发布只抽一次）
                    var again34 = await probe34.ExtractCoverFrameAsync(v72034);
                    Check("封面统一：同视频同时间点命中缓存（同一文件）",
                        again34 == framePath34, again34 ?? "null");

                    // 指定封面帧 → 另一个缓存文件
                    var at2s34 = await probe34.ExtractCoverFrameAsync(v72034, 2000);
                    Check("封面统一：指定封面帧（2 秒）→ 单独缓存文件",
                        at2s34 is not null && File.Exists(at2s34) && at2s34 != framePath34,
                        at2s34 ?? "null");

                    // 指定时间点超出视频时长 → 退回默认 0.5 秒（宁可差一点，不要「必填封面」发不出去）
                    var beyond34 = await probe34.ExtractCoverFrameAsync(v72034, 60_000);
                    Check("封面统一：封面帧超出时长 → 退回 0.5 秒兜底",
                        beyond34 is not null && File.Exists(beyond34) && beyond34 == framePath34,
                        beyond34 ?? "null");

                    // 卡片缩略图仍是 320px（两份资产互不顶替）
                    var meta34 = await probe34.ProbeAsync(v72034);
                    Check("封面统一：卡片缩略图仍是 320px（封面帧单独出图）",
                        meta34?.ThumbPath is { } thumb34 && thumb34 != framePath34
                        && ImageDimensionProbe.TryReadFile(thumb34) is { Width: 320 },
                        meta34?.ThumbPath ?? "无缩略图");

                    // ResolveAsync 全链路：没选图 → FirstFrame（就是刚抽的那张）
                    var autoCover34 = await PublishCoverResolver.ResolveAsync(
                        v72034, null, null, probe34);
                    Check("封面统一：没选图 + 有探测器 → FirstFrame（路径真实存在）",
                        autoCover34 is { Source: PublishCoverSource.FirstFrame }
                        && autoCover34.HasFile && autoCover34.Path == framePath34,
                        autoCover34.Path ?? "null");

                    // 选了图 → Custom，优先于抽帧
                    var customCover34 = await PublishCoverResolver.ResolveAsync(
                        v72034, customFile34, null, probe34);
                    Check("封面统一：选了图 → Custom（优先于抽帧）",
                        customCover34 is { Source: PublishCoverSource.Custom }
                        && customCover34.Path == customFile34);
                }

                // 小片源不放大（640×360 保持原样，不拉成 1920）
                var v36034 = Path.Combine(dir34, "v360.mp4");
                await FfmpegCli.RunAsync(new[]
                {
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc=size=640x360:rate=10", "-t", "1",
                    "-pix_fmt", "yuv420p", "-c:v", "mpeg4", "-q:v", "5", v36034,
                });
                if (File.Exists(v36034))
                {
                    var smallFrame34 = await probe34.ExtractCoverFrameAsync(v36034);
                    var smallDim34 = ImageDimensionProbe.TryReadFile(smallFrame34);
                    Check("封面统一：小片源不放大（640×360 原样）",
                        smallDim34 is { Width: 640, Height: 360 },
                        smallDim34 is { } sd34 ? $"{sd34.Width}x{sd34.Height}" : "读不出");
                }

                // 超大帧缩到宽 1920（4K 片源不整张上传）
                var v1440p34 = Path.Combine(dir34, "v1440p.mp4");
                await FfmpegCli.RunAsync(new[]
                {
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc=size=2560x1440:rate=5", "-t", "1",
                    "-pix_fmt", "yuv420p", "-c:v", "mpeg4", "-q:v", "5", v1440p34,
                });
                if (File.Exists(v1440p34))
                {
                    var bigFrame34 = await probe34.ExtractCoverFrameAsync(v1440p34);
                    var bigDim34 = ImageDimensionProbe.TryReadFile(bigFrame34);
                    Check("封面统一：超大帧缩到宽 1920 上限（2560×1440 → 1920×1080）",
                        bigDim34 is { Width: 1920, Height: 1080 },
                        bigDim34 is { } bd34 ? $"{bd34.Width}x{bd34.Height}" : "读不出");
                }

                // 视频不存在 → null（不抛，不阻断）
                var missingVideo34 = await probe34.ExtractCoverFrameAsync(
                    Path.Combine(dir34, $"no_such_{Guid.NewGuid():N}.mp4"));
                Check("封面统一：视频不存在 → null（不抛）", missingVideo34 is null);
            }
            finally
            {
                try { Directory.Delete(dir34, true); } catch { /* best effort */ }
            }
        }

        // ── 35. 发布页话题块：平台候选 → TopicRef 的映射（2.6.3，纯函数，零网络）──
        sb.AppendLine("\n[35] 话题块：候选映射 / 讨论量 / 去重（2.6.3）");
        Check("话题名规范化：# 前缀与多余空白都处理",
            TopicSelection.NormalizeName("  #家居 ") == "家居"
            && TopicSelection.NormalizeName("##夏日  海边") == "夏日 海边"
            && TopicSelection.NormalizeName("   ") == "");

        var sug35 = new TopicSuggestion
        {
            Name = "家居",
            PlatformId = "1658571727441931",
            TopicType = "0",
            MissionId = 4070872,
            ViewCount = 126_000_000,
            ExtraJson = "{\"x\":1}",
        };
        var ref35 = TopicSelection.ToRef(sug35, TopicRefSources.Search);
        Check("候选 → TopicRef：ID / 类型 / 任务号 / 来源 / 讨论量全带上",
            ref35 is
            {
                Id: "1658571727441931", Name: "家居", Type: "0", MissionId: 4070872,
                Source: TopicRefSources.Search, Metric: "1.3 亿",
            }
            && ref35.HasPlatformId && ref35.IsActivityTopic,
            $"metric={ref35.Metric}");

        Check("讨论量优先用平台原样文案（小红书「浏览」口径）",
            TopicSelection.ToRef(sug35 with { DiscussText = "1.7亿浏览" },
                TopicRefSources.Recommend).Metric == "1.7亿浏览");

        Check("讨论量兜底：万 / 亿 / 原值 / 0 = null",
            TopicSelection.FormatCount(4_800_000) == "480 万"
            && TopicSelection.FormatCount(126_000_000) == "1.3 亿"
            && TopicSelection.FormatCount(3200) == "3200"
            && TopicSelection.FormatCount(0) is null);

        var noId35 = TopicSelection.ToRef(sug35 with { PlatformId = "  " }, TopicRefSources.Recommend);
        Check("候选没有平台 ID → 只存名字（发布时降级纯文本）",
            noId35.Id is null && !noId35.HasPlatformId
            && noId35.Source == TopicRefSources.Recommend);

        var manual35 = TopicSelection.Manual("夏日");
        Check("手动话题：无 ID、来源 = manual",
            manual35 is { Id: null, Name: "夏日", Source: TopicRefSources.Manual }
            && manual35.Metric is null);

        var dedupe35 = TopicSelection.Dedupe(
        [
            new TopicSuggestion { Name = "#家居" },
            new TopicSuggestion { Name = "家居", PlatformId = "1" },
            new TopicSuggestion { Name = "夏日" },
            new TopicSuggestion { Name = "  " },
        ]);
        Check("候选去重：同名保留带 ID 的那条、空名丢弃",
            dedupe35.Count == 2
            && dedupe35[0] is { Name: "家居", PlatformId: "1" }
            && dedupe35[1].Name == "夏日",
            $"{dedupe35.Count} 条");

        // ── 36. 发布页官方活动块：平台活动 → ActivityRef 的映射（2.6.4，纯函数，零网络）──
        sb.AppendLine("\n[36] 官方活动块：活动快照映射 / 截止与热度文案 / 旧 ID 迁移（2.6.4）");
        var now36 = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.FromHours(8));
        var act36 = new PlatformActivity
        {
            Id = "7702034615116174390",
            Name = "  夏日创作大赛  ",
            CoverUrl = "https://example.com/act.png",
            Reward = " 有奖活动 ",
            EndTime = now36.AddDays(7),
            HotScore = 126_000_000,
            Topics =
            [
                new TopicSuggestion { Name = "夏日", PlatformId = "t-1", ViewCount = 4_800_000 },
                new TopicSuggestion { Name = "#家装", PlatformId = "t-2", DiscussText = "1.7亿浏览" },
                new TopicSuggestion { Name = "  " },
            ],
        };
        var actRef36 = TopicSelection.ToActivityRef(act36);
        Check("活动 → ActivityRef：名称/奖励去空白、封面/截止保留",
            actRef36 is
            {
                Id: "7702034615116174390",
                Name: "夏日创作大赛",
                Cover: "https://example.com/act.png",
                Reward: "有奖活动",
            }
            && actRef36.EndTime == act36.EndTime,
            $"{actRef36.Name}/{actRef36.Reward}");

        Check("活动关联话题：# 前缀规范化 + 来源 = activity + 讨论量保留",
            actRef36.Topics.Count == 2
            && actRef36.Topics[0] is
            {
                Name: "夏日", Id: "t-1", Source: TopicRefSources.Activity, Metric: "480 万",
            }
            && actRef36.Topics[1] is
            {
                Name: "家装", Id: "t-2", Source: TopicRefSources.Activity, Metric: "1.7亿浏览",
            },
            $"{actRef36.Topics.Count} 条："
            + string.Join("|", actRef36.Topics.Select(t => $"{t.Name}:{t.Id}:{t.Metric}")));

        var noName36 = TopicSelection.ToActivityRef(new PlatformActivity { Id = "act-9" });
        Check("活动名缺失 → 芯片退回显示 ID（不留空白）",
            noName36.Name == "act-9" && noName36.Cover is null && noName36.Reward is null);

        var future36 = TopicSelection.ActivityDeadlineText(now36.AddDays(2), now36);
        Check("活动截止文案：未给 = null / 未过期写截止 / 已过期写已结束",
            TopicSelection.ActivityDeadlineText(null, now36) is null
            && future36 is not null && future36.EndsWith("截止", StringComparison.Ordinal)
            && TopicSelection.ActivityDeadlineText(now36.AddSeconds(-1), now36) == "已结束"
            && TopicSelection.ActivityDeadlineText(now36, now36) == "已结束",
            future36 ?? "null");

        Check("活动热度文案：人数优先 / 热度兜底 / 都没有 = null",
            TopicSelection.ActivityMetricText(new PlatformActivity { ParticipantCount = 12_000 })
                == "1.2 万人参与"
            && TopicSelection.ActivityMetricText(new PlatformActivity { HotScore = 4_800_000 })
                == "热度 480 万"
            && TopicSelection.ActivityMetricText(new PlatformActivity()) is null);

        // 旧档「官方活动 ID」一次性迁移
        var legacyAct36 = new PlatformParams { ActivityIds = [" 7702034615116174390 ", "act-2"] };
        legacyAct36.MigrateLegacyActivities();
        Check("旧活动 ID 迁移成活动快照（名称先按 ID 显示、幂等）",
            legacyAct36.Activities.Count == 2
            && legacyAct36.Activities[0] is { Id: "7702034615116174390", Name: "7702034615116174390" }
            && legacyAct36.Activities[1].Id == "act-2"
            && legacyAct36.ActivityIds.Count == 2,
            $"{legacyAct36.Activities.Count} 个");

        var migratedAgain36 = legacyAct36.Activities.Count;
        legacyAct36.MigrateLegacyActivities();
        Check("已有活动快照时迁移不再执行（不产生重复）",
            legacyAct36.Activities.Count == migratedAgain36);

        var already36 = new PlatformParams
        {
            ActivityIds = ["old-id"],
            Activities =
            [
                new ActivityRef { Id = "new-id", Name = "新活动" },
            ],
        };
        already36.MigrateLegacyActivities();
        Check("新档已有 ActivityRef → 旧 ID 不迁移（以结构化活动为准）",
            already36.Activities.Count == 1 && already36.Activities[0].Id == "new-id");

        // 「选活动自动带话题」与 2.6.4 映射联用：UpsertActivity 后 TopicRefs 是 activity 来源
        var upsert36 = new PlatformParams();
        upsert36.UpsertActivity(actRef36);
        Check("选中活动 → 关联话题并进 TopicRefs（Source=activity）",
            upsert36.Activities.Count == 1
            && upsert36.TopicRefs.Count == 2
            && upsert36.TopicRefs.All(t => t.Source == TopicRefSources.Activity)
            && upsert36.TopicRefs.Any(t => t.Name == "家装" && t.Id == "t-2"));
        upsert36.UpsertActivity(actRef36 with { Name = "改过名" });
        Check("同 ID 活动重复选中 = 替换快照不重复",
            upsert36.Activities.Count == 1 && upsert36.Activities[0].Name == "改过名");

        // ── 37. builder 六平台话题/活动落点（2.6.5，纯函数，零网络）──
        //   ① 结构化 TopicRefs（params.txt / 平台卡 / 活动自动带入）与旧 ResolvedTopic
        //      （联邦解析 / 旧档）合并成一份来源，同名只发一次、优先带 ID 的那份；
        //   ② 抖音 text_extra+challenges / 快手 recTagIdList / 小红书 hash_tag /
        //      视频号 topics / B站 topic_id+mission_id / 头条 HashtagList；
        //   ③ 活动走 Activities（旧 ActivityIds 只做空表回落）；
        //   ④ 空值降级：没有话题/活动时字段为 null、不写进载荷。
        sb.AppendLine("\n[37] builder 六平台话题/活动落点（2.6.5）");
        {
            var d37 = new PublishDraft(@"D:\demo\t37.mp4")
            {
                Title = "园区三居室",
                Description = "南北通透",
            };
            d37.Topics.Add("苏州楼市"); // 全局纯文本（视频号/头条的名单）
            d37.Meta = new VideoMeta(d37.VideoPath, 1, 12, 1920, 1080);

            string Json37(string platformKey, PlatformParams pp) =>
                PublishPayloadBuilder.Build(d37,
                    new PublishTarget(platformKey, Guid.NewGuid(), "主号"), pp).ExtraDataJson ?? "{}";

            // ── 抖音：text_extra + challenges（cid）──
            var dy37p = new PlatformParams
            {
                TopicRefs =
                [
                    new TopicRef { Id = "1568360053346369", Name = "家居", Source = TopicRefSources.Params },
                    new TopicRef { Name = "手动话题", Source = TopicRefSources.Manual },
                ],
                // 旧解析与结构化同名（去重）+ 只有旧解析的话题（旧档兼容）
                Topics =
                [
                    new ResolvedTopic("家居", "1568360053346369", "家居"),
                    new ResolvedTopic("旧话题", "999", "旧话题"),
                ],
                Activities = [new ActivityRef { Id = "7702034615116174390", Name = "夏日创作大赛" }],
                ActivityIds = ["legacy-act"],
            };
            var dy37 = PublishPayload.Deserialize<DouyinPayload>(Json37(PlatformNames.Douyin, dy37p));
            var challenges37 = string.IsNullOrWhiteSpace(dy37?.Challenges)
                ? [] : JsonSerializer.Deserialize<List<long>>(dy37!.Challenges!) ?? [];
            var extras37 = string.IsNullOrWhiteSpace(dy37?.TextExtra)
                ? [] : JsonSerializer.Deserialize<List<JsonElement>>(dy37!.TextExtra!) ?? [];
            // text_extra 的元素是 snake_case（HashtagExtraEntry 显式 JsonPropertyName）
            var extraNames37 = extras37
                .Select(e => e.GetProperty("hashtag_name").GetString() ?? "").ToList();
            Check("抖音：text_extra/challenges 收结构化 + 旧解析，同名只发一次",
                challenges37.Count == 2
                && challenges37.Contains(1568360053346369) && challenges37.Contains(999)
                && extraNames37.Count(n => n == "家居") == 1 && extraNames37.Contains("旧话题"),
                $"challenges={string.Join(",", challenges37)} extra={string.Join("|", extraNames37)}");

            Check("抖音：正文追加带 ID 的 #话题；无 ID 的手动话题不进正文（平台会拒收对不上的 #）",
                dy37?.Caption is { } dyCap37
                && dyCap37.Split("#家居").Length == 2
                && dyCap37.Contains("#旧话题")
                && !dyCap37.Contains("#手动话题"),
                dy37?.Caption ?? "null");

            Check("抖音：活动落点用 Activities（旧 ActivityIds 不混入）",
                dy37?.ActivityIds is ["7702034615116174390"],
                string.Join(",", dy37?.ActivityIds ?? []));

            // 旧档兼容：只有 Topics（没有 TopicRefs）也必须照发
            var dyLegacy37 = PublishPayload.Deserialize<DouyinPayload>(Json37(
                PlatformNames.Douyin,
                new PlatformParams
                {
                    Topics = [new ResolvedTopic("大自然", "1568360053346369", "大自然")],
                }));
            Check("抖音：旧档只有 Topics → 挑战照发（不迁移也不丢）",
                dyLegacy37?.Challenges?.Contains("1568360053346369") == true
                && dyLegacy37.Caption?.Contains("#大自然") == true);

            // 结构化只有名字、旧解析命中 ID → 合并时补 ID（params.txt + 联邦解析混用）
            var dyMix37 = PublishPayload.Deserialize<DouyinPayload>(Json37(
                PlatformNames.Douyin,
                new PlatformParams
                {
                    TopicRefs = [new TopicRef { Name = "家居", Source = TopicRefSources.Manual }],
                    Topics = [new ResolvedTopic("家居", "1568360053346369", "家居")],
                }));
            Check("抖音：结构化无 ID + 旧解析有 ID → 用旧 ID，正文 # 只出现一次",
                dyMix37?.Challenges?.Contains("1568360053346369") == true
                && dyMix37.Caption?.Split("#家居").Length == 2,
                dyMix37?.Caption ?? "null");

            // ── 快手：recTagIdList（≤3）+ activityIds ──
            var ks37 = PublishPayload.Deserialize<KuaishouPayload>(Json37(
                PlatformNames.Kuaishou,
                new PlatformParams
                {
                    TopicRefs =
                    [
                        new TopicRef { Id = "111", Name = "装修" },
                        new TopicRef { Id = "222", Name = "家居" },
                        new TopicRef { Id = "333", Name = "好物" },
                        new TopicRef { Id = "444", Name = "第四个" },
                    ],
                    Activities = [new ActivityRef { Id = "ks-act", Name = "快手活动" }],
                }));
            Check("快手：recTagIdList 用结构化 ID、裁到 3 个；活动走 activityIds",
                ks37?.RecTagIds is [111, 222, 333] && ks37.ActivityIds is ["ks-act"],
                $"tags={string.Join(",", ks37?.RecTagIds ?? [])} acts={string.Join(",", ks37?.ActivityIds ?? [])}");
            Check("快手：正文话题同样裁到 3 个（含用户写进简介的 #）",
                (ks37?.Caption?.Split('#').Length ?? 0) - 1 == 3,
                ks37?.Caption ?? "null");

            // ── 小红书：hash_tag（有 ID 才发）+ 活动只靠关联话题 ──
            var xhs37 = PublishPayload.Deserialize<XhsPayload>(Json37(
                PlatformNames.Xiaohongshu,
                new PlatformParams
                {
                    TopicRefs =
                    [
                        new TopicRef { Id = "xhs-1", Name = "装修日记", Type = "0" },
                        new TopicRef { Name = "纯文本话题", Source = TopicRefSources.Manual },
                    ],
                    Topics = [new ResolvedTopic("旧解析", "xhs-old", "旧解析")],
                    Activities = [new ActivityRef { Id = "xhs-act" }],
                }));
            Check("小红书：hash_tag 收结构化 + 旧解析（无 ID 的话题不进接口）",
                xhs37?.Topics?.Count == 2
                && xhs37?.Topics?.Any(t => t is { Id: "xhs-1", Name: "装修日记" }) == true
                && xhs37?.Topics?.Any(t => t is { Id: "xhs-old", Name: "旧解析" }) == true,
                string.Join("|", xhs37?.Topics?.Select(t => $"{t.Id}:{t.Name}") ?? []));

            // ── 视频号：topics（纯文本，草稿统一列表 ∪ 平台卡话题）──
            var wx37 = PublishPayload.Deserialize<WechatVideoPayload>(Json37(
                PlatformNames.WechatVideo,
                new PlatformParams
                {
                    TopicRefs =
                    [
                        new TopicRef { Id = "wx-1", Name = "卡片话题" },
                        new TopicRef { Name = "苏州楼市" },   // 与全局话题同名 → 去重
                    ],
                }));
            Check("视频号：topics = 草稿统一列表 ∪ 平台卡话题（去重、只取名字）",
                wx37?.Topics is ["苏州楼市", "卡片话题"],
                string.Join("|", wx37?.Topics ?? []));

            // ── B站：topic_id + mission_id ──
            var bili37 = PublishPayload.Deserialize<BilibiliPayload>(Json37(
                PlatformNames.Bilibili,
                new PlatformParams
                {
                    Tags = ["家居"],
                    TopicRefs =
                    [
                        new TopicRef { Id = "1098785", Name = "我在B站搞装修", MissionId = 4070872 },
                        new TopicRef { Name = "无ID话题", Source = TopicRefSources.Manual },
                    ],
                    Activities = [new ActivityRef { Id = "4070872", Name = "我在B站搞装修" }],
                }));
            Check("B站：topic_id 只发有 ID 的话题，mission_id 去重带上",
                bili37?.Topics is [{ Id: "1098785", Name: "我在B站搞装修", MissionId: 4070872 }]
                && bili37.MissionIds is [4070872]
                && bili37.Tags is ["家居"],
                $"topics={bili37?.Topics?.Count} missions={string.Join(",", bili37?.MissionIds ?? [])}");

            // ── 头条：HashtagList + 纯文本名单 ──
            var tt37 = PublishPayload.Deserialize<ToutiaoPayload>(Json37(
                PlatformNames.Toutiao,
                new PlatformParams
                {
                    TopicRefs =
                    [
                        new TopicRef { Id = "7425665423405751331", Name = "作品同步头条赢激励" },
                    ],
                }));
            Check("头条：HashtagList = HashtagId（字符串）+ HashtagName；纯文本名单并集",
                tt37?.Hashtags is [{ Id: "7425665423405751331", Name: "作品同步头条赢激励" }]
                && tt37.Topics is ["苏州楼市", "作品同步头条赢激励"]
                && tt37.Caption?.Contains("#苏州楼市") == true
                && tt37.Caption?.Contains("#作品同步头条赢激励") == true,
                $"hashtags={tt37?.Hashtags?.Count} topics={string.Join("|", tt37?.Topics ?? [])}");

            // ── 空值降级：没有话题/活动 → 六平台相关字段全是 null ──
            var d37Empty = new PublishDraft(@"D:\demo\t37e.mp4")
            {
                Title = "空参数",
                Description = "无话题",
            };
            d37Empty.Meta = new VideoMeta(d37Empty.VideoPath, 1, 12, 1920, 1080);
            string JsonEmpty37(string key) =>
                PublishPayloadBuilder.Build(d37Empty,
                    new PublishTarget(key, Guid.NewGuid(), "主号"), new PlatformParams())
                .ExtraDataJson ?? "{}";
            var dyE37 = PublishPayload.Deserialize<DouyinPayload>(JsonEmpty37(PlatformNames.Douyin));
            var ksE37 = PublishPayload.Deserialize<KuaishouPayload>(JsonEmpty37(PlatformNames.Kuaishou));
            var xhsE37 = PublishPayload.Deserialize<XhsPayload>(JsonEmpty37(PlatformNames.Xiaohongshu));
            var wxE37 = PublishPayload.Deserialize<WechatVideoPayload>(JsonEmpty37(PlatformNames.WechatVideo));
            var biliE37 = PublishPayload.Deserialize<BilibiliPayload>(JsonEmpty37(PlatformNames.Bilibili));
            var ttE37 = PublishPayload.Deserialize<ToutiaoPayload>(JsonEmpty37(PlatformNames.Toutiao));
            Check("空参数：六平台话题/活动字段全部为 null（不写进载荷）",
                dyE37 is { TextExtra: null, Challenges: null, ActivityIds: null }
                && ksE37 is { RecTagIds: null, ActivityIds: null }
                && xhsE37?.Topics is null
                && wxE37?.Topics is null
                && biliE37 is { Topics: null, MissionIds: null }
                && ttE37 is { Hashtags: null, Topics: null });
            Check("空参数：抖音载荷 JSON 里连键都不出现（WhenWritingNull）",
                !JsonEmpty37(PlatformNames.Douyin).Contains("textExtra", StringComparison.Ordinal)
                && !JsonEmpty37(PlatformNames.Douyin).Contains("challenges", StringComparison.Ordinal)
                && !JsonEmpty37(PlatformNames.Bilibili).Contains("missionIds", StringComparison.Ordinal));
        }

        // ── 38. 发布预检：话题/活动/长度（2.6.6，纯函数，零网络）──
        //   ① B站标签 1~10（0 = Error 拦提交，>10 = Warning 只发前 10）；
        //   ② 小红书正文（含话题）≤1000 字 —— 按最终正文数，不是只数简介；
        //   ③ 活动已结束 / 平台没有活动能力 / 小红书活动没带话题；
        //   ④ 结构化话题缺 ID 按平台点名「会怎么降级」；视频号无接口给 Info。
        sb.AppendLine("\n[38] 发布预检：话题/活动/长度（2.6.6）");
        {
            // 每条用例独占一份草稿 —— ParamsFor 首次访问即把预设固化进 Overrides（[19] 踩过）
            PublishDraft Draft38(string platformKey, string description = "正文", string title = "预检")
            {
                var d = new PublishDraft($@"D:\demo\t38-{Guid.NewGuid():N}.mp4")
                {
                    Title = title,
                    Description = description,
                };
                d.Meta = new VideoMeta(d.VideoPath, 1, 12, 1920, 1080);
                d.Targets.Add(new PublishTarget(platformKey, Guid.NewGuid(), "主号"));
                return d;
            }

            bool Has38(List<PreflightIssue> issues, string platformKey,
                PreflightSeverity severity, params string[] parts) =>
                issues.Any(i => i.PlatformKey == platformKey && i.Severity == severity
                    && parts.All(part => i.Message.Contains(part, StringComparison.Ordinal)));

            string Msg38(List<PreflightIssue> issues) =>
                string.Join("；", issues.Select(i => i.Message));

            // ── B站标签 1~10 ──
            var biliNoTags38 = Draft38(PlatformNames.Bilibili);
            var biliNoTagsIssues38 = PublishPreflight.Validate(biliNoTags38, new PlatformParams());
            Check("预检：B站没有标签 → Error（投稿至少 1 个标签）",
                Has38(biliNoTagsIssues38, PlatformNames.Bilibili, PreflightSeverity.Error,
                    "B站投稿至少需要 1 个标签"),
                Msg38(biliNoTagsIssues38));

            var biliTwelve38 = Draft38(PlatformNames.Bilibili);
            var biliTwelveIssues38 = PublishPreflight.Validate(biliTwelve38, new PlatformParams
            {
                Tags = [.. Enumerable.Range(1, 12).Select(i => $"标签{i}")],
            });
            Check("预检：B站标签 >10 → Warning（只发送前 10 个）",
                Has38(biliTwelveIssues38, PlatformNames.Bilibili, PreflightSeverity.Warning,
                    "B站标签最多 10 个", "超出部分不会发送"),
                Msg38(biliTwelveIssues38));

            var biliTen38 = Draft38(PlatformNames.Bilibili);
            var biliTenIssues38 = PublishPreflight.Validate(biliTen38, new PlatformParams
            {
                Tags = [.. Enumerable.Range(1, 10).Select(i => $"标签{i}")],
            });
            Check("预检：B站标签 =10 → 不报超限（边界合法）",
                !biliTenIssues38.Any(i => i.Message.Contains("B站标签最多")),
                Msg38(biliTenIssues38));

            // ── 小红书标题 ≤20 与正文（含话题）总长 ──
            var xhsEdge38 = Draft38(PlatformNames.Xiaohongshu,
                new string('房', PlatformLimits.XhsDescChars));
            var xhsEdgeIssues38 = PublishPreflight.Validate(xhsEdge38, new PlatformParams());
            Check("预检：小红书正文正好 1000 字 → 不报长度错误（边界合法）",
                !xhsEdgeIssues38.Any(i => i.Message.Contains("正文（含话题）")),
                Msg38(xhsEdgeIssues38));

            var xhsLong38 = Draft38(PlatformNames.Xiaohongshu,
                new string('房', PlatformLimits.XhsDescChars + 1));
            var xhsLongIssues38 = PublishPreflight.Validate(xhsLong38, new PlatformParams());
            Check("预检：小红书正文 >1000 字 → Error 拦提交",
                Has38(xhsLongIssues38, PlatformNames.Xiaohongshu, PreflightSeverity.Error,
                    "正文（含话题）", "超出上限 1000 字"),
                Msg38(xhsLongIssues38));

            // 「话题总长」：简介本身没超，但带 ID 话题追加进正文后超了 —— 必须按最终正文算
            var xhsTopicLong38 = Draft38(PlatformNames.Xiaohongshu,
                new string('房', PlatformLimits.XhsDescChars - 10));
            var xhsTopicLongIssues38 = PublishPreflight.Validate(xhsTopicLong38, new PlatformParams
            {
                TopicRefs = [new TopicRef { Id = "t38", Name = new string('装', 20) }],
            });
            Check("预检：小红书话题追加进正文后按总长判（990 + #话题 > 1000）",
                Has38(xhsTopicLongIssues38, PlatformNames.Xiaohongshu, PreflightSeverity.Error,
                    "正文（含话题）", "超出上限 1000 字"),
                Msg38(xhsTopicLongIssues38));

            // ── 活动：已结束 / 平台不支持 / 小红书没带话题 ──
            var dyEnded38 = Draft38(PlatformNames.Douyin);
            var dyEndedIssues38 = PublishPreflight.Validate(dyEnded38, new PlatformParams
            {
                Activities =
                [
                    new ActivityRef
                    {
                        Id = "1", Name = "夏日创作大赛",
                        EndTime = DateTimeOffset.Now.AddDays(-1),
                    },
                ],
            });
            Check("预检：活动已结束 → Warning 点名活动与「已结束」",
                Has38(dyEndedIssues38, PlatformNames.Douyin, PreflightSeverity.Warning,
                    "夏日创作大赛", "已结束"),
                Msg38(dyEndedIssues38));

            var dyLive38 = Draft38(PlatformNames.Douyin);
            var dyLiveIssues38 = PublishPreflight.Validate(dyLive38, new PlatformParams
            {
                Activities =
                [
                    new ActivityRef
                    {
                        Id = "2", Name = "进行中的活动",
                        EndTime = DateTimeOffset.Now.AddDays(1),
                    },
                ],
            });
            Check("预检：进行中的活动 → 不报「已结束」",
                !dyLiveIssues38.Any(i => i.Message.Contains("已结束")),
                Msg38(dyLiveIssues38));

            var ttActivity38 = Draft38(PlatformNames.Toutiao);
            var ttActivityIssues38 = PublishPreflight.Validate(ttActivity38, new PlatformParams
            {
                Activities = [new ActivityRef { Id = "9", Name = "头条活动" }],
            });
            Check("预检：头条没有官方活动 → Warning「不会生效」",
                Has38(ttActivityIssues38, PlatformNames.Toutiao, PreflightSeverity.Warning,
                    "没有官方活动", "不会生效"),
                Msg38(ttActivityIssues38));

            var xhsActivity38 = Draft38(PlatformNames.Xiaohongshu);
            var xhsActivityIssues38 = PublishPreflight.Validate(xhsActivity38, new PlatformParams
            {
                Activities = [new ActivityRef { Id = "a38", Name = "家居活动" }],
            });
            Check("预检：小红书只选活动、没有带 ID 话题 → Warning（活动不会生效）",
                Has38(xhsActivityIssues38, PlatformNames.Xiaohongshu, PreflightSeverity.Warning,
                    "活动必须关联话题"),
                Msg38(xhsActivityIssues38));

            var xhsActivityOk38 = Draft38(PlatformNames.Xiaohongshu);
            var xhsActivityOkIssues38 = PublishPreflight.Validate(xhsActivityOk38, new PlatformParams
            {
                Activities = [new ActivityRef { Id = "a38", Name = "家居活动" }],
                TopicRefs =
                [
                    new TopicRef { Id = "t38", Name = "家居", Source = TopicRefSources.Activity },
                ],
            });
            Check("预检：小红书活动带关联话题 → 不报「必须关联话题」",
                !xhsActivityOkIssues38.Any(i => i.Message.Contains("活动必须关联话题")),
                Msg38(xhsActivityOkIssues38));

            // ── 结构化话题缺 ID：按平台点名「会怎么降级」──
            var dyTopic38 = Draft38(PlatformNames.Douyin);
            var dyTopicIssues38 = PublishPreflight.Validate(dyTopic38, new PlatformParams
            {
                TopicRefs = [new TopicRef { Name = "只写名字", Source = TopicRefSources.Manual }],
            });
            Check("预检：抖音话题缺平台 ID → Warning 点名话题（正文 #标签 会被清掉）",
                Has38(dyTopicIssues38, PlatformNames.Douyin, PreflightSeverity.Warning,
                    "只写名字", "缺少平台 ID"),
                Msg38(dyTopicIssues38));

            var biliTopic38 = Draft38(PlatformNames.Bilibili);
            var biliTopicIssues38 = PublishPreflight.Validate(biliTopic38, new PlatformParams
            {
                Tags = ["家居"],
                TopicRefs = [new TopicRef { Name = "无ID话题", Source = TopicRefSources.Manual }],
            });
            Check("预检：B站话题缺 ID → Warning（B站话题必须来自接口/params）",
                Has38(biliTopicIssues38, PlatformNames.Bilibili, PreflightSeverity.Warning,
                    "无ID话题", "B站不会带上这条话题"),
                Msg38(biliTopicIssues38));

            // 旧解析（Topics）里没 ID 的话题同样要点名 —— MergeTopics 是唯一来源
            var ksLegacy38 = Draft38(PlatformNames.Kuaishou);
            var ksLegacyIssues38 = PublishPreflight.Validate(ksLegacy38, new PlatformParams
            {
                Topics = [new ResolvedTopic("旧解析无ID", null, "旧解析无ID")],
            });
            Check("预检：旧解析 Topics 里的无 ID 话题同样点名（不只查 TopicRefs）",
                Has38(ksLegacyIssues38, PlatformNames.Kuaishou, PreflightSeverity.Warning,
                    "旧解析无ID", "缺少平台 ID"),
                Msg38(ksLegacyIssues38));

            // ── 视频号：无话题接口说明（有话题才提示，不刷屏）──
            var wxTopics38 = Draft38(PlatformNames.WechatVideo);
            wxTopics38.Topics.Add("苏州楼市");
            var wxTopicsIssues38 = PublishPreflight.Validate(wxTopics38, new PlatformParams());
            Check("预检：视频号有话题但无接口 → Info 说明只按正文纯文本发送",
                Has38(wxTopicsIssues38, PlatformNames.WechatVideo, PreflightSeverity.Info,
                    "没有话题列表接口"),
                Msg38(wxTopicsIssues38));

            var wxNoTopics38 = Draft38(PlatformNames.WechatVideo);
            var wxNoTopicsIssues38 = PublishPreflight.Validate(wxNoTopics38, new PlatformParams());
            Check("预检：视频号没话题 → 不出现无接口提示（不刷屏）",
                !wxNoTopicsIssues38.Any(i => i.Message.Contains("没有话题列表接口")),
                Msg38(wxNoTopicsIssues38));
        }

        // ── 39. 阶段 3：头条合集视频/文章类型（3.1 ~ 3.4）──
        sb.AppendLine("\n[39] 阶段 3：头条合集视频/文章类型（3.1 ~ 3.4）");
        var ttFile39 = Path.Combine(Path.GetTempPath(), "newapp-selftest-t39.mp4");
        File.WriteAllText(ttFile39, "x");
        try
        {
            PublishDraft ToutiaoDraft39(int? width, int? height)
            {
                var d = new PublishDraft(ttFile39) { Title = "头条合集类型用例" };
                d.Targets.Add(new PublishTarget(PlatformNames.Toutiao, Guid.NewGuid(), "头条号"));
                if (width is { } w && height is { } h)
                    d.Meta = new VideoMeta(ttFile39, 1, 10, w, h);
                return d;
            }

            var collection39 = new PlatformParams
            {
                CollectionId = "album-1",
                CollectionName = "视频合集",
            };

            var portrait39 = PublishPreflight.Validate(ToutiaoDraft39(1080, 1920), collection39);
            Check("预检：头条 + 视频合集 + 竖版 → Warning「只支持横版」",
                portrait39.Any(i => i.Severity == PreflightSeverity.Warning
                    && i.Message.Contains("只支持横版")),
                string.Join("；", portrait39.Select(i => i.Message)));

            var landscape39 = PublishPreflight.Validate(ToutiaoDraft39(1920, 1080), collection39);
            Check("预检：头条 + 视频合集 + 横版 → 不报竖版提示",
                landscape39.All(i => !i.Message.Contains("只支持横版")),
                string.Join("；", landscape39.Select(i => i.Message)));

            var portraitNoCollection39 = PublishPreflight.Validate(
                ToutiaoDraft39(1080, 1920), new PlatformParams());
            Check("预检：竖版但没挂合集 → 不报竖版提示",
                portraitNoCollection39.All(i => !i.Message.Contains("只支持横版")),
                string.Join("；", portraitNoCollection39.Select(i => i.Message)));

            var unknownMeta39 = PublishPreflight.Validate(ToutiaoDraft39(null, null), collection39);
            Check("预检：视频信息未探测 → 不下结论（不误报）",
                unknownMeta39.All(i => !i.Message.Contains("只支持横版")),
                string.Join("；", unknownMeta39.Select(i => i.Message)));
        }
        finally { File.Delete(ttFile39); }

        // ── 40. 阶段 4：B站投稿链路（4.1 ~ 4.5）──
        sb.AppendLine("\n[40] 阶段 4：B站投稿链路（4.1 ~ 4.5）");

        bool BiliThrows(Action action, string? contains = null)
        {
            try { action(); return false; }
            catch (BilibiliApiException ex)
            {
                return contains is null
                    || ex.Message.Contains(contains, StringComparison.Ordinal);
            }
        }

        // 视频上传 = /upload/multipart/new → part → complete（2026-11-07 真账号实测）
        Check("multipart/new 体：profile / name / size（中文原文）",
            BilibiliUploadService.BuildMultipartNewBody("我的 视频.mp4", 22145021)
                == """{"profile":"ugcfx/bup","name":"我的 视频.mp4","size":22145021}""",
            BilibiliUploadService.BuildMultipartNewBody("我的 视频.mp4", 22145021));

        var newResp40 = """
        {"code":0,"message":"0","ttl":1,"data":{"uri":"upos://ugcfxever/n261104s31qle0m66nie5t2pxj39lx9a.mp4",
         "upload_token":"m1_abc","biz_id":43321527216,"retry":10,"retry_delay":3,"timeout":1200,
         "threads":4,"chunk_size":10485760,"filename":"n261104s31qle0m66nie5t2pxj39lx9a"}}
        """;
        var sess40 = BilibiliUploadService.ParseMultipartNew(newResp40);
        Check("multipart/new 解析：biz_id = 稿件 cid / filename / 分片参数",
            sess40.BizId == 43321527216
            && sess40.Filename == "n261104s31qle0m66nie5t2pxj39lx9a"
            && sess40.Uri == "upos://ugcfxever/n261104s31qle0m66nie5t2pxj39lx9a.mp4"
            && sess40.UploadToken == "m1_abc"
            && sess40.ChunkSize == 10485760 && sess40.Threads == 4 && sess40.Retry == 10,
            $"{sess40.BizId}/{sess40.Filename}");
        Check("multipart/new code≠0 → 明确报错（-101 登录引导）",
            BiliThrows(() => BilibiliUploadService.ParseMultipartNew(
                """{"code":-101,"message":"账号未登录"}"""), "重新登录"));
        Check("multipart/new 缺 biz_id → 明确报错",
            BiliThrows(() => BilibiliUploadService.ParseMultipartNew(
                """{"code":0,"data":{"uri":"u","upload_token":"t","filename":"f"}}"""), "biz_id"));

        Check("multipart/part 体：uri / upload_token / part_number",
            BilibiliUploadService.BuildMultipartPartBody(sess40, 2)
                == """{"uri":"upos://ugcfxever/n261104s31qle0m66nie5t2pxj39lx9a.mp4","upload_token":"m1_abc","part_number":2}""");

        var partResp40 = """
        {"code":0,"data":{"reqs":[
          {"method":"PUT","url":"https://jscs-luffy-upcdnbldsa.bilivideo.com/a?partNumber=1","cdn_name":"x"},
          {"method":"PUT","url":"https://jscs-luffy-upcdntx.bilivideo.com/a?partNumber=1","cdn_name":"y"}]}}
        """;
        var urls40 = BilibiliUploadService.ParsePresignedUrls(partResp40, "multipart/part 1");
        Check("multipart/part 解析：多个 CDN 候选按序返回",
            urls40.Count == 2
            && urls40[0].Contains("upcdnbldsa") && urls40[1].Contains("upcdntx"),
            string.Join("；", urls40));
        Check("multipart/part 没有 reqs → 明确报错",
            BiliThrows(() => BilibiliUploadService.ParsePresignedUrls(
                """{"code":0,"data":{}}""", "multipart/part 1"), "reqs"));

        var completeBody40 = BilibiliUploadService.BuildMultipartCompleteBody(
            sess40,
            [new BilibiliMultipartPart(1, "\"0fd06fa9\""),
             new BilibiliMultipartPart(2, "\"bbb\"")]);
        Check("multipart/complete 体：etag 带引号原样回传 / upload_params 带 biz_id",
            completeBody40.Contains(
                "\"parts\":[{\"part_number\":1,\"etag\":\"\\\"0fd06fa9\\\"\"}")
            && completeBody40.Contains(
                "\"upload_params\":{\"biz_id\":43321527216,\"profile\":\"ugcfx/bup\"}"),
            completeBody40);
        Check("multipart/complete：成功 code=0 / 失败 code≠0 明确报错",
            !BiliThrows(() => BilibiliUploadService.ParseMultipartComplete(
                """{"code":0,"data":{"etag":"\"a356-1\""}}"""))
            && BiliThrows(() => BilibiliUploadService.ParseMultipartComplete(
                """{"code":21001,"message":"bad"}"""), "21001"));

        var url40 = BilibiliPublishService.BuildAddV3Url("csrf123", 1793786078687);
        Check("add/v3 URL：web_location / t / csrf / locale 编码",
            url40.Contains("web_location=333.1024")
            && url40.Contains("t=1793786078687")
            && url40.Contains("csrf=csrf123")
            && url40.Contains("x-bili-locale-json=%7B%22c_locale%22"),
            url40);

        var addReq40 = new BilibiliPublishRequest
        {
            Title = "house",
            Description = "desc",
            Tag = "家居,室内设计",
            Filename = "n261104abc",
            Cid = 43321527216,
            Tid = 21,
            HumanType2 = 1015,
            Copyright = 3,
            CreationStatementId = 1,
            AiCover = 0,
            IsAbCover = 0,
            Watermark = true,
            AllowComment = false,
            AllowDanmaku = null,
            IsOnlySelf = true,
            SpaceHidden = 2,
            TopicId = null,
            MissionId = 4070872,
        };
        var body40 = BilibiliPublishService.BuildAddV3Body(addReq40);
        using (var bodyDoc40 = JsonDocument.Parse(body40))
        {
            var root40 = bodyDoc40.RootElement;
            Check("add/v3 体：核心字段与抓包同形",
                root40.GetProperty("title").GetString() == "house"
                && root40.GetProperty("tid").GetInt32() == 21
                && root40.GetProperty("human_type2").GetInt32() == 1015
                && root40.GetProperty("copyright").GetInt32() == 3
                && root40.GetProperty("creation_statement").GetProperty("id").GetInt32() == 1
                && root40.GetProperty("videos")[0].GetProperty("filename").GetString() == "n261104abc"
                && root40.GetProperty("videos")[0].GetProperty("cid").GetInt64() == 43321527216
                && root40.GetProperty("is_only_self").GetInt32() == 1
                && root40.GetProperty("watermark").GetProperty("state").GetInt32() == 1
                && root40.GetProperty("space_hidden").GetInt32() == 2
                && root40.GetProperty("web_os").GetInt32() == 1,
                body40);
            Check("add/v3 体：允许评论取反 / 无 topic_id 不发 / mission_id 有值才发",
                root40.GetProperty("up_close_reply").GetBoolean()
                && !root40.GetProperty("up_close_danmu").GetBoolean()
                && !root40.TryGetProperty("topic_id", out _)
                && root40.GetProperty("mission_id").GetInt64() == 4070872);
            Check("add/v3 体：ab_cover_info 空 → null",
                root40.GetProperty("ab_cover_info").ValueKind == JsonValueKind.Null);
            Check("add/v3 体：立即发布不下发 dtime（与抓包样本同形）",
                !root40.TryGetProperty("dtime", out _), body40);
        }

        // 未选声明（None → 0）不得下发 creation_statement —— 平台回 21517「选择的创作声明不存在」
        // 整条拒稿（2026-11-09 真账号实测）；抓包样本带的是 id=1，None ≠ 0 号声明。
        var body40NoDecl = BilibiliPublishService.BuildAddV3Body(
            addReq40 with { CreationStatementId = 0 });
        using (var noDeclDoc40 = JsonDocument.Parse(body40NoDecl))
        {
            Check("add/v3 体：未选声明（id=0 / null）不下发 creation_statement",
                !noDeclDoc40.RootElement.TryGetProperty("creation_statement", out _),
                body40NoDecl);
        }

        // 定时 = dtime（Unix 秒）—— 抓包时开关是关的，字段按平台模型 archive.dtime 补
        var body40Scheduled = BilibiliPublishService.BuildAddV3Body(
            addReq40 with { Dtime = 1793786078 });
        using (var schedDoc40 = JsonDocument.Parse(body40Scheduled))
        {
            Check("add/v3 体：定时 → dtime 为 Unix 秒",
                schedDoc40.RootElement.GetProperty("dtime").GetInt64() == 1793786078,
                body40Scheduled);
        }

        var addResp40 =
            """{"code":0,"message":"OK","ttl":1,"data":{"aid":117557549864689,"bvid":"BV1NSx96fEtS"}}""";
        var ok40 = BilibiliPublishService.ParseAddV3Response(addResp40);
        Check("add/v3 响应解析 aid / bvid",
            ok40.Aid == 117557549864689 && ok40.Bvid == "BV1NSx96fEtS");
        Check("add/v3 code=-101 → 登录过期引导",
            BiliThrows(() => BilibiliPublishService.ParseAddV3Response(
                """{"code":-101,"message":"账号未登录"}"""), "重新登录"));
        Check("add/v3 缺 aid/bvid → 带原文报错",
            BiliThrows(() => BilibiliPublishService.ParseAddV3Response("""{"code":0,"data":{}}"""),
                "aid"));

        Check("ab_cover_info：合法 JSON 透传 / 坏 JSON 上传前报错",
            BilibiliPublishService.ParseOptionalJson("""{"a":1}""", "AB 封面信息") is { } ab40
            && ab40.GetProperty("a").GetInt32() == 1
            && BiliThrows(
                () => BilibiliPublishService.ParseOptionalJson("{bad", "AB 封面信息"),
                "AB 封面信息"));

        var biliParams40 = PublishParamsFile.Parse("B站分区: 239\nB站二级分区: 1024");
        Check("params.txt：B站分区 / B站二级分区",
            biliParams40.BilibiliTid == 239 && biliParams40.BilibiliHumanType2 == 1024
            && biliParams40.Problems.Count == 0,
            string.Join("；", biliParams40.Problems.Select(p => p.Reason)));
        var badBiliParams40 = PublishParamsFile.Parse("B站分区: abc");
        Check("params.txt：B站分区写错 → 记问题不生效",
            badBiliParams40.BilibiliTid is null && badBiliParams40.Problems.Count == 1,
            string.Join("；", badBiliParams40.Problems.Select(p => p.Reason)));

        var biliFile40 = Path.Combine(Path.GetTempPath(), "newapp-selftest-t40.mp4");
        File.WriteAllText(biliFile40, "x");
        try
        {
            var biliDraft40 = new PublishDraft(biliFile40)
            {
                Title = "B站定时用例",
                IsScheduled = true,
                ScheduledAt = DateTime.Now.AddHours(3),
            };
            biliDraft40.Targets.Add(
                new PublishTarget(PlatformNames.Bilibili, Guid.NewGuid(), "B站号"));
            var biliIssues40 = PublishPreflight.Validate(biliDraft40,
                new PlatformParams { Tags = ["家居"] });
            Check("预检：B站 + 定时 3 小时 → 不再拦截（dtime 已接入）",
                !biliIssues40.Any(i => i.Severity == PreflightSeverity.Error
                    && (i.Message.Contains("定时") || i.Message.Contains("dtime"))),
                string.Join("；", biliIssues40.Select(i => i.Message)));

            // 窗口：B站 5 分钟 ~ 15 天；其余平台仍是 2 小时 ~ 14 天；多平台取交集
            var now40 = DateTime.Now;
            var biliOnly40 = new PublishDraft(biliFile40) { IsScheduled = true };
            biliOnly40.Targets.Add(
                new PublishTarget(PlatformNames.Bilibili, Guid.NewGuid(), "B站号"));
            var mixed40 = new PublishDraft(biliFile40) { IsScheduled = true };
            mixed40.Targets.Add(
                new PublishTarget(PlatformNames.Bilibili, Guid.NewGuid(), "B站号"));
            mixed40.Targets.Add(
                new PublishTarget(PlatformNames.Douyin, Guid.NewGuid(), "抖音号"));
            Check("定时窗口：B站 5 分钟 ~ 15 天（页面实测）",
                PublishPreflight.ScheduleWindowFor(PlatformNames.Bilibili)
                    == (TimeSpan.FromMinutes(5), TimeSpan.FromDays(15))
                && PublishPreflight.ScheduleWindowFor(PlatformNames.Douyin)
                    == (TimeSpan.FromHours(2), TimeSpan.FromDays(14)));
            Check("定时窗口：B站单平台 10 分钟合法 / 2 分钟太早 / 16 天太晚",
                PublishPreflight.ScheduleWindowErrorForTargets(biliOnly40, now40.AddMinutes(10), now40) is null
                && PublishPreflight.ScheduleWindowErrorForTargets(biliOnly40, now40.AddMinutes(2), now40) is not null
                && PublishPreflight.ScheduleWindowErrorForTargets(biliOnly40, now40.AddDays(16), now40) is not null);
            Check("定时窗口：B站 + 抖音 → 交集 2 小时 ~ 14 天",
                PublishPreflight.ScheduleWindowErrorForTargets(mixed40, now40.AddMinutes(30), now40) is not null
                && PublishPreflight.ScheduleWindowErrorForTargets(mixed40, now40.AddHours(3), now40) is null
                && PublishPreflight.ScheduleWindowErrorForTargets(mixed40, now40.AddDays(15), now40) is not null);
        }
        finally { File.Delete(biliFile40); }

        // ── 41. 阶段 5.1：ByteDance VOD 通用上传（抖音/头条差异）──
        sb.AppendLine("\n[41] 阶段 5.1：ByteDance VOD 通用上传（抖音/头条差异）");

        var dySettings41 = new BdvodUploadSettings(
            VodHost: "https://vod.bytedanceapi.com", SpaceName: "aweme");
        var ttSettings41 = new BdvodUploadSettings(
            VodHost: "https://vod.bytedanceapi.com",
            SpaceName: ToutiaoSpaceName.Horizontal,
            AppId: "1231", SendUserIdQuery: true, SendEnOid: true);

        var dyApply41 = BdvodUploadService.BuildApplyUrl(
            dySettings41, "uid-1", 123, "mp4", "abc");
        Check("抖音 apply：不带 app_id/user_id/EnOID（保持既有 wire）",
            dyApply41.Contains("SpaceName=aweme")
            && dyApply41.Contains("FileSize=123")
            && dyApply41.Contains("FileExtension=mp4")
            && dyApply41.Contains("s=abc")
            && !dyApply41.Contains("app_id=")
            && !dyApply41.Contains("user_id=")
            && !dyApply41.Contains("EnOID="),
            dyApply41);

        var ttApply41 = BdvodUploadService.BuildApplyUrl(
            ttSettings41, "uid-1", 123, "mp4", "abc");
        Check("头条 apply：app_id=1231 / user_id / EnOID=1（接口说明书 §4.2.2）",
            ttApply41.Contains("SpaceName=middlevideo_toutiao")
            && ttApply41.Contains("app_id=1231")
            && ttApply41.Contains("user_id=uid-1")
            && ttApply41.Contains("EnOID=1"),
            ttApply41);

        var dyCommit41 = BdvodUploadService.BuildCommitUrl(dySettings41, "uid-1");
        var ttCommit41 = BdvodUploadService.BuildCommitUrl(ttSettings41, "uid-1");
        Check("commit：抖音不带 app_id / 头条带 app_id + user_id",
            !dyCommit41.Contains("app_id=") && !dyCommit41.Contains("EnOID=")
            && ttCommit41.Contains("app_id=1231")
            && ttCommit41.Contains("user_id=uid-1"),
            dyCommit41 + " | " + ttCommit41);

        Check("头条 SpaceName：横版 middlevideo_toutiao / 竖版 short_video_toutiao",
            ToutiaoSpaceName.For(false) == "middlevideo_toutiao"
            && ToutiaoSpaceName.For(true) == "short_video_toutiao"
            && ToutiaoSpaceName.ForSize(1920, 1080) == "middlevideo_toutiao"
            && ToutiaoSpaceName.ForSize(1080, 1920) == "short_video_toutiao"
            && ToutiaoSpaceName.ForSize(0, 0) == "middlevideo_toutiao");

        // ── 42. 阶段 5：头条发布（5.3 ~ 5.6）──
        sb.AppendLine("\n[42] 阶段 5：头条发布（PublishVideo / 预检 / params）");

        bool ToutiaoThrows(Action action, string? contains = null)
        {
            try { action(); return false; }
            catch (ToutiaoPublishException ex)
            {
                return contains is null
                    || ex.Message.Contains(contains, StringComparison.Ordinal);
            }
        }

        var horiz42 = new ToutiaoPublishRequest
        {
            Title = "横版标题",
            Abstract = "横版简介 #家居",
            Vid = "v0dd16g10004dbltkmvog65qi41s947g",
            VName = "tt_test.mp4",
            ThumbUri = "tos-cn-i-e874c6/xxx",
            ThumbUrl = "blob:https://mp.toutiao.com/abc",
            DurationSeconds = 3,
            Width = 1920,
            Height = 1080,
            VideoType = 3,
            HideType = 1,
            TimerTime = 1793884980,
            ClaimOrigin = true,
            Praise = true,
            PrependPSeriesId = "7704513538005402155",
            Label = ["家居", "装修"],
            CoverTitles = ["封面标题"],
            Hashtags = [new ToutiaoHashtag("7425", "作品同步头条赢激励")],
        };
        var horizBody42 = ToutiaoPublishService.BuildPublishBody(horiz42);
        using (var doc42 = JsonDocument.Parse(horizBody42))
        {
            var root42 = doc42.RootElement;
            Check("头条横版体：VideoType=3 / 简介 / 定时 / 合集 / 赞赏 / 原创",
                root42.GetProperty("Title").GetString() == "横版标题"
                && root42.GetProperty("VideoType").GetInt32() == 3
                && root42.GetProperty("Abstract").GetString() == "横版简介 #家居"
                && root42.GetProperty("TimerTime").GetInt64() == 1793884980
                && root42.GetProperty("PrependPSeriesId").GetString() == "7704513538005402155"
                && root42.GetProperty("Praise").GetBoolean()
                && root42.GetProperty("ClaimOrigin").GetBoolean()
                && root42.GetProperty("VideoInfo").GetProperty("VideoWidth").GetInt32() == 1920
                && root42.GetProperty("VideoInfo").GetProperty("VideoHeight").GetInt32() == 1080
                && root42.GetProperty("VideoInfo").GetProperty("Vid").GetString()
                    == "v0dd16g10004dbltkmvog65qi41s947g",
                horizBody42);

            Check("头条体：可见性 / 标签 / 封面标题 / HashtagList / 常量",
                root42.GetProperty("HideInfo").GetProperty("HideType").GetInt32() == 1
                && root42.GetProperty("Label")[0].GetString() == "家居"
                && root42.GetProperty("CoverTitles")[0].GetString() == "封面标题"
                && root42.GetProperty("HashtagList")[0].GetProperty("HashtagId").GetString() == "7425"
                && root42.GetProperty("HashtagList")[0].GetProperty("HashtagName").GetString()
                    == "作品同步头条赢激励"
                && root42.GetProperty("PublishType").GetInt32() == 1
                && root42.GetProperty("From").GetString() == "mp"
                && root42.GetProperty("EnterFrom").GetInt32() == 5
                && root42.GetProperty("CreateSource").GetInt32() == 2
                && root42.GetProperty("TerminalType").GetString() == "01"
                && root42.GetProperty("OsType").GetString() == "09"
                && root42.GetProperty("DevicePlatform").GetString() == "pc",
                horizBody42);
        }

        var vert42 = horiz42 with { VideoType = 6, Width = 1080, Height = 1920, HideType = 0 };
        var vertBody42 = ToutiaoPublishService.BuildPublishBody(vert42);
        using (var docVert42 = JsonDocument.Parse(vertBody42))
        {
            var rootVert42 = docVert42.RootElement;
            Check("头条竖版体：没有简介 / 定时 / 合集（抓包 vertical 三样都不带）",
                rootVert42.GetProperty("VideoType").GetInt32() == 6
                && !rootVert42.TryGetProperty("Abstract", out _)
                && !rootVert42.TryGetProperty("TimerTime", out _)
                && !rootVert42.TryGetProperty("PrependPSeriesId", out _)
                && rootVert42.GetProperty("HideInfo").GetProperty("HideType").GetInt32() == 0,
                vertBody42);
        }

        var publishResp42 =
            """{"data":{"ItemId":"7704491911486980627","ItemType":2,"Code":0,"Message":"SUCCESS"},"status":0,"message":"SUCCESS"}""";
        Check("头条发布响应 → ItemId",
            ToutiaoPublishService.ParsePublishResponse(publishResp42).ItemId
                == "7704491911486980627");
        Check("头条发布业务失败 → 带平台原文报错",
            ToutiaoThrows(() => ToutiaoPublishService.ParsePublishResponse(
                """{"data":{"ItemId":"","Code":-1,"Message":"登录已失效"},"status":0}"""),
                "登录已失效"));
        Check("头条发布缺 ItemId → 报错",
            ToutiaoThrows(() => ToutiaoPublishService.ParsePublishResponse(
                """{"data":{"Code":0,"Message":"SUCCESS"},"status":0}"""), "ItemId"));
        Check("头条原始 JSON：合法透传 / 坏 JSON 报错",
            ToutiaoPublishService.ParseOptionalJson("""{"a":1}""", "长尾属性") is { } ttJson42
            && ttJson42.GetProperty("a").GetInt32() == 1
            && ToutiaoThrows(
                () => ToutiaoPublishService.ParseOptionalJson("{bad", "长尾属性"),
                "长尾属性"));

        var ttParams42 = PublishParamsFile.Parse(
            "头条赞赏: 是\n头条模板: 模板A\n头条滤镜: 滤镜B");
        Check("params.txt：头条赞赏 / 模板 / 滤镜",
            ttParams42.ToutiaoPraise == true
            && ttParams42.ToutiaoTemplate == "模板A"
            && ttParams42.ToutiaoFilter == "滤镜B"
            && ttParams42.Problems.Count == 0,
            string.Join("；", ttParams42.Problems.Select(p => p.Reason)));

        var ttFile42 = Path.Combine(Path.GetTempPath(), "newapp-selftest-t42.mp4");
        File.WriteAllText(ttFile42, "x");
        try
        {
            PublishDraft TtDraft42(bool portrait, bool scheduled)
            {
                var d = new PublishDraft(ttFile42)
                {
                    Title = "头条用例",
                    Description = "简介",
                    IsScheduled = scheduled,
                    ScheduledAt = scheduled ? DateTime.Now.AddHours(3) : null,
                };
                d.Targets.Add(new PublishTarget(PlatformNames.Toutiao, Guid.NewGuid(), "头条号"));
                d.Meta = new VideoMeta(ttFile42, 1, 10,
                    portrait ? 1080 : 1920, portrait ? 1920 : 1080);
                return d;
            }

            var portraitSched42 = PublishPreflight.Validate(TtDraft42(true, true),
                new PlatformParams { CollectionId = "album-1", CollectionName = "视频合集" });
            Check("预检：头条竖版 + 定时 → Error / 简介 → Warning / 合集 → Warning",
                portraitSched42.Any(i => i.Severity == PreflightSeverity.Error
                    && i.Message.Contains("竖版视频不支持定时"))
                && portraitSched42.Any(i => i.Severity == PreflightSeverity.Warning
                    && i.Message.Contains("没有简介字段"))
                && portraitSched42.Any(i => i.Severity == PreflightSeverity.Warning
                    && i.Message.Contains("只支持横版")),
                string.Join("；", portraitSched42.Select(i => i.Message)));

            var landscapeSched42 = PublishPreflight.Validate(TtDraft42(false, true),
                new PlatformParams { CollectionId = "album-1", CollectionName = "视频合集" });
            Check("预检：头条横版 + 定时 → 不报竖版错误",
                landscapeSched42.All(i => !i.Message.Contains("竖版视频不支持定时")),
                string.Join("；", landscapeSched42.Select(i => i.Message)));

            // 声明提示只出现一次（能力矩阵报；头条专项不再重复）—— 2026-11-07 用户截图实测
            var declDraft42 = TtDraft42(false, false);
            declDraft42.Declaration = new ContentDeclaration { Kind = DeclarationKind.SelfShot };
            var declIssues42 = PublishPreflight.Validate(declDraft42, new PlatformParams());
            Check("预检：头条声明提示只出现一次（去重）",
                declIssues42.Count(i => i.Severity == PreflightSeverity.Warning
                    && i.Message.Contains("声明")) == 1,
                string.Join("；", declIssues42.Select(i => i.Message)));

            // 话题无平台 ID：横版正文 #文本兜底 / 竖版连简介都没有 → 完全不会发送
            var topicParams42 = new PlatformParams
            {
                TopicRefs = [new TopicRef { Name = "头条" }],
            };
            var topicLand42 = PublishPreflight.Validate(TtDraft42(false, false), topicParams42);
            var topicPort42 = PublishPreflight.Validate(TtDraft42(true, false), topicParams42);
            Check("预检：头条话题缺 ID —— 横版说正文兜底 / 竖版说完全不会发送",
                topicLand42.Any(i => i.Message.Contains("话题「头条」")
                    && i.Message.Contains("正文 #文本 兜底"))
                && topicPort42.Any(i => i.Message.Contains("话题「头条」")
                    && i.Message.Contains("完全不会发送")),
                string.Join("；", topicLand42.Select(i => i.Message))
                + " || " + string.Join("；", topicPort42.Select(i => i.Message)));
        }
        finally { File.Delete(ttFile42); }

        // ── 43. 2026-11-09 真账号实测修正（风险 #30 / #31 / #32）──
        //   ① 快手标题并入 caption（快照没有独立标题字段，以前整段丢失）；
        //   ② 头条竖版 HideInfo 被平台忽略 → 竖版非公开可见性拦提交（Error，隐私风险）；
        //   ③ 头条视频必须有音轨 → VideoMeta 探测 + 预检提示。
        sb.AppendLine("\n[43] 真账号实测修正：快手标题并入正文 / 头条竖版可见性 / 头条音轨（#30 ~ #32）");
        {
            // ── ① 快手：标题进 caption ──
            var ks43 = new PublishTarget(PlatformNames.Kuaishou, Guid.NewGuid(), "快手主号");
            string KsCaption43(PublishDraft d, PlatformParams pp)
            {
                using var doc = JsonDocument.Parse(
                    PublishPayloadBuilder.Build(d, ks43, pp).ExtraDataJson ?? "{}");
                return doc.RootElement.GetProperty("caption").GetString() ?? "";
            }

            var titleDraft43 = new PublishDraft(@"D:\demo\t43-ks.mp4")
            {
                Title = "阳台改造的小书房",
                Description = "采光很好，坐下就不想动。",
            };
            Check("快手：标题并入 caption 首行（以前整段丢失）",
                KsCaption43(titleDraft43, new PlatformParams())
                    == "阳台改造的小书房\n采光很好，坐下就不想动。",
                KsCaption43(titleDraft43, new PlatformParams()));

            var sameDraft43 = new PublishDraft(@"D:\demo\t43-ks-same.mp4")
            {
                Title = "阳台改造的小书房",
                Description = "阳台改造的小书房，采光很好。",
            };
            Check("快手：简介已以标题开头 → 不重复加",
                KsCaption43(sameDraft43, new PlatformParams()) == "阳台改造的小书房，采光很好。",
                KsCaption43(sameDraft43, new PlatformParams()));

            // 标题里的 # 也占「最多 3 个」的名额（平台数的是整段正文）
            var hashTitle43 = new PublishDraft(@"D:\demo\t43-ks-hash.mp4")
            {
                Title = "#甲",
                Description = "正文 #乙 #丙 #丁",
            };
            var hashCaption43 = KsCaption43(hashTitle43, new PlatformParams());
            Check("快手：标题里的 #标签 同样计入 3 个上限",
                hashCaption43 == "#甲\n正文 #乙 #丙", hashCaption43);

            // 500 字上限：总 501 字，切点落在「#家居」中间 → 整个话题不要
            var longDraft43 = new PublishDraft(@"D:\demo\t43-ks-long.mp4")
            {
                Title = "标题标题标题标题标题",                        // 10 字
                Description = new string('字', 486) + " #家居",         // 486 + 空格 + 3
            };
            var longCaption43 = KsCaption43(longDraft43, new PlatformParams());
            Check("快手：标题+简介超 500 字 → 截断到 500 以内，且不切断 #话题",
                longCaption43.Length <= PlatformLimits.KuaishouCaptionChars
                && !longCaption43.Contains("#家居")
                && longCaption43 == longCaption43.TrimEnd(),
                $"{longCaption43.Length} 字：…{longCaption43[^12..]}");

            // 预检：超 500 字要提示（Warning，不拦提交）
            var longDraft43b = new PublishDraft(@"D:\demo\t43-ks-long2.mp4")
            {
                Title = "标题",
                Description = new string('字', 520),
            };
            longDraft43b.Targets.Add(ks43);
            var longIssues43 = PublishPreflight.Validate(longDraft43b, new PlatformParams());
            Check("预检：快手文案（标题+简介+话题）超 500 字 → Warning",
                longIssues43.Any(i => i.PlatformKey == PlatformNames.Kuaishou
                    && i.Severity == PreflightSeverity.Warning
                    && i.Message.Contains("500 字")),
                string.Join("；", longIssues43.Select(i => i.Message)));

            // ── ② 头条竖版可见性 / ③ 音轨 ──
            var ttFile43 = Path.Combine(Path.GetTempPath(), "newapp-selftest-t43.mp4");
            File.WriteAllText(ttFile43, "x");
            try
            {
                PublishDraft TtDraft43(bool portrait, PublishVisibility v, bool? hasAudio)
                {
                    var d = new PublishDraft(ttFile43)
                    {
                        Title = "头条用例",
                        Visibility = v,
                    };
                    d.Targets.Add(new PublishTarget(PlatformNames.Toutiao, Guid.NewGuid(), "头条号"));
                    d.Meta = new VideoMeta(ttFile43, 1, 10,
                        portrait ? 1080 : 1920, portrait ? 1920 : 1080) { HasAudio = hasAudio };
                    return d;
                }

                var portraitPrivate43 = PublishPreflight.Validate(
                    TtDraft43(true, PublishVisibility.Private, null), new PlatformParams());
                Check("预检：头条竖版 + 仅自己 → Error 拦提交「平台会忽略…请改用横版或改公开」",
                    portraitPrivate43.Any(i => i.Severity == PreflightSeverity.Error
                        && i.Message.Contains("头条竖版视频不支持发布前可见性")
                        && i.Message.Contains("发布后是公开作品")
                        && i.Message.Contains("改用横版"))
                    && !portraitPrivate43.Any(i => i.Message.Contains("竖版视频不支持发布前可见性")
                        && i.Severity == PreflightSeverity.Warning),
                    string.Join("；", portraitPrivate43.Select(i => $"[{i.Severity}] {i.Message}")));

                var portraitFollowers43 = PublishPreflight.Validate(
                    TtDraft43(true, PublishVisibility.FollowersOnly, null), new PlatformParams());
                Check("预检：头条竖版 + 仅粉丝可见 → 同样 Error（按生效可见性判，不只看「仅自己」）",
                    portraitFollowers43.Any(i => i.Severity == PreflightSeverity.Error
                        && i.Message.Contains("头条竖版视频不支持发布前可见性")
                        && i.Message.Contains("仅粉丝可见")),
                    string.Join("；", portraitFollowers43.Select(i => $"[{i.Severity}] {i.Message}")));

                var landscapePrivate43 = PublishPreflight.Validate(
                    TtDraft43(false, PublishVisibility.Private, null), new PlatformParams());
                Check("预检：头条横版 + 仅自己 → 不报竖版可见性提示（横版 HideType=1 有效）",
                    landscapePrivate43.All(i => !i.Message.Contains("竖版视频不支持发布前可见性")),
                    string.Join("；", landscapePrivate43.Select(i => i.Message)));

                var portraitPublic43 = PublishPreflight.Validate(
                    TtDraft43(true, PublishVisibility.Public, null), new PlatformParams());
                Check("预检：头条竖版 + 公开 → 不报（本来就是公开）",
                    portraitPublic43.All(i => !i.Message.Contains("竖版视频不支持发布前可见性")),
                    string.Join("；", portraitPublic43.Select(i => i.Message)));

                var noAudio43 = PublishPreflight.Validate(
                    TtDraft43(false, PublishVisibility.Public, hasAudio: false), new PlatformParams());
                Check("预检：无音轨 → Warning「缺少音频信息」",
                    noAudio43.Any(i => i.Severity == PreflightSeverity.Warning
                        && i.Message.Contains("没有音轨") && i.Message.Contains("缺少音频信息")),
                    string.Join("；", noAudio43.Select(i => i.Message)));

                var withAudio43 = PublishPreflight.Validate(
                    TtDraft43(false, PublishVisibility.Public, hasAudio: true), new PlatformParams());
                Check("预检：有音轨 / 未知（null）→ 不报音轨提示",
                    withAudio43.All(i => !i.Message.Contains("没有音轨"))
                    && PublishPreflight.Validate(
                            TtDraft43(false, PublishVisibility.Public, hasAudio: null),
                            new PlatformParams()).All(i => !i.Message.Contains("没有音轨")),
                    string.Join("；", withAudio43.Select(i => i.Message)));
            }
            finally { File.Delete(ttFile43); }
        }

        sb.AppendLine("\n[44] 小红书转码轮询预算（2026-11-09 真账号实测）");
        {
            // 2026-11-09：43 秒 / 29 MB 的视频在旧的固定 30 次（90 秒）里始终
            // status=0 / progress=0，靠「转码超时非致命」兜底才发出去。
            // 预算 = 3 分钟基线 + 0.6×时长（与视频号 clip 轮询同一口径）。

            // 180 + 43×0.6 = 205.8s → ceil(68.6) = 69
            var short43 = XhsUploadService.TranscodePollCount(43, 30_660_863);
            Check("小红书转码：43 秒视频 → 69 次预算（旧固定 30 次不够，实测没过）",
                short43 == 69, short43.ToString());

            // 180 + 2040×0.6 = 1404s → 468 次 → 夹到 400（约 20 分钟）
            var long34 = XhsUploadService.TranscodePollCount(2040, 492_000_000);
            Check("小红书转码：34 分钟视频 → 夹到 400 次上限（约 20 分钟）",
                long34 == 400, long34.ToString());

            // 时长未知 → 按体积回落（每 MB 约 1 秒）：180 + 10 = 190s → ceil(63.3) = 64
            var unknown = XhsUploadService.TranscodePollCount(null, 10_000_000);
            Check("小红书转码：时长未知按体积回落（10 MB → 64 次）",
                unknown == 64, unknown.ToString());

            // 极小视频：180 + 0.6 = 180.6s → ceil(60.2) = 61（基线 3 分钟，不会退回归零）
            var tiny = XhsUploadService.TranscodePollCount(1, 100_000);
            Check("小红书转码：极小视频 → 61 次（基线 3 分钟，不退回归零）",
                tiny == 61, tiny.ToString());
        }

        sb.AppendLine();
        sb.AppendLine(failures == 0 ? "═══ 全部通过 ═══" : $"═══ {failures} 项失败 ═══");

        var report = sb.ToString();
        Console.WriteLine(report);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "newapp_publish_selftest.txt"), report);
        return failures;
    }

    /// <summary>造一条队列明细行（状态机只关心状态/进度/错误几项，其余给常数）</summary>
    private static QueuePlatformRow Plat(
        Guid taskId, Guid platformId, int status, Guid? session = null, string? error = null,
        int pct = 0, string platformKey = PlatformNames.Douyin,
        string fileName = "夏日海边vlog.mp4", string videoPath = @"D:\不存在\夏日海边vlog.mp4",
        string? title = "夏日海边vlog", DateTime? publishedAt = null) =>
        new(new ServerPlatformItem(taskId, fileName, videoPath, platformId, platformKey,
                PlatformNames.GetDisplayName(platformKey), session, title,
                null, null, null, null, null, status, "", pct, error, 0,
                null, null, publishedAt, DateTime.Now, DateTime.Now),
            "#FF0049", "主号");

    /// <summary>造一条会话记录（排序只关心 平台 / 主号 / 登录时间 三件事，其余给常数）</summary>
    private static PlatformSessionItem Sess(string platformKey, string label, bool primary, int daysAgo)
        => new(Guid.NewGuid(), platformKey, label, primary, null, null, null,
            0, "正常", DateTime.Today.AddDays(-daysAgo), null, null, null);

    /// <summary>同一平台的行是否**连续成组** —— 排序页"分组不散"的核心不变量</summary>
    private static bool IsGrouped(IEnumerable<PlatformSessionItem> sessions)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prev = "";
        foreach (var s in sessions)
        {
            if (s.PlatformKey == prev) continue;
            if (!seen.Add(s.PlatformKey)) return false;   // 这个平台出现过、断开了、又出现
            prev = s.PlatformKey;
        }
        return true;
    }

    /// <summary>找一条样例视频：先看仓库 video/ 目录，再退到当前目录下的任意 mp4。</summary>
    private static string? FindSampleVideo()
    {
        var roots = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "..", "video", "video"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "video", "video"),
        };
        foreach (var root in roots)
        {
            try
            {
                var full = Path.GetFullPath(root);
                if (!Directory.Exists(full)) continue;
                var f = Directory.EnumerateFiles(full, "*.mp4").OrderBy(x => x).FirstOrDefault();
                if (f is not null) return f;
            }
            catch { /* 路径不存在/无权限 —— 试下一个 */ }
        }
        return null;
    }
}
