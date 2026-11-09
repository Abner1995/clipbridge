using System.Text.Json;
using System.Text.RegularExpressions;
using CreatFlow.Platforms.Douyin;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 提交时把「一个概念」映射成各平台的 wire 载荷（设计文档 §4.0 的落点）。
///
/// 这是整套字段合并的收口处：草稿里 位置/合集/声明/可见性 各只有一份，
/// 到这里才按平台各自展开。老版把这一步散在 <c>BuildVideoExtraData</c> 等四个
/// 4000 行方法里，同一个概念被实现了四遍 —— 字段爆炸与 L1/L5/L11 都是这么来的。
///
/// 产出直接写进 <c>PublishPlatform.ExtraDataJson</c>，**自包含**：
/// 队列页重试单条时不需要回查草稿（草稿提交后就移除了）。
/// </summary>
public static partial class PublishPayloadBuilder
{
    /// <summary>
    /// 为一个目标构建 wire DTO。
    /// </summary>
    public static PublishTargetDto Build(PublishDraft draft, PublishTarget target, PlatformParams p)
    {
        // 先清**不可见字符**（老版 SanitizeText 那张表）：NBSP / 零宽空格 / 零宽连字。
        // 肉眼看不见，却实打实占一个字符位 —— 抖音的 text_extra 是靠偏移量核对
        // #话题 位置的，差一个字符整条就被拒（status_code=35「视频描述参数不合法」）。
        // 这里统一清一次，下游（含抖音算偏移量的那条正文）用的都是清过的同一份文本。
        var title = SanitizeText(draft.Title);
        var raw = SanitizeText(draft.Description);

        // 2.6.5 话题落点：结构化 TopicRefs（平台卡 / params.txt / 活动自动带入）与旧
        // ResolvedTopic（联邦解析 / 旧档）合成**一份单一事实来源**。以前只读 Topics ——
        // params.txt 的「话题ID」只写 TopicRefs，平台上根本收不到；只读 TopicRefs 又会
        // 丢掉旧解析结果。两条写入口都必须进 wire。
        var (topics, flatTopics) = MergeTopics(p);
        // 视频号 / 头条不消费平台 ID，但要吃到平台卡话题 —— 名字并集（去重）
        var plainTopicNames = PlainTopicNames(draft, p);

        // 再把话题**合进正文**：不写进正文的话题在线上等于没设（抖音尤其 ——
        // text_extra 要正文里有那个 # 才落得下去）。抖音那份还要先删掉没解析出 ID 的 #xxx。
        var caption = ComposeCaption(raw, flatTopics, stripUnresolved: false);
        var douyinCaption = ComposeCaption(raw, flatTopics, stripUnresolved: true);

        // 快手的话题上限裁剪放在分支里算（局部函数 = 惰性）—— 不能提到 switch 外面：
        // 那样发抖音/小红书时也会跑一遍，白记一条「快手省略话题」的日志
        (string Title, string Description, string? ExtraJson) BuildKuaishouBranch()
        {
            // 快手没有独立标题字段（caption 是唯一文本落点）：标题必须并入正文首行，
            // 否则标题整段丢失（2026-11-09 真实作品实测，风险 #30）。
            // 口径收在 KuaishouCaption：预检与组装共用同一份
            //「标题 + 简介 + 话题 → 裁 3 个 #标签 → 截 500 字」。
            var ksCaption = KuaishouCaption(draft, p);
            return (title, ksCaption, BuildKuaishou(draft, p, title, ksCaption, flatTopics));
        }

        (string Title, string Description, string? ExtraJson) BuildToutiaoBranch()
        {
            // 头条没有可用的搜索接口（1.0.5）：与视频号同款纯文本。
            // 名字 = 草稿统一话题列表 ∪ 平台卡话题（2.6.5；头条不消费平台 ID）。
            var ttCaption = AppendTopicNames(raw, plainTopicNames);
            return (title, ttCaption,
                BuildToutiao(draft, p, title, ttCaption, topics, plainTopicNames));
        }

        (string Title, string Description, string? ExtraJson) BuildWechatBranch()
        {
            // 视频号没有标题字段，只有短标题（页面实测 ≥6 字，5 字整条被拒）。
            // 不足 6 字时**不下发 shortTitle**，改为把标题并入正文首行 —— 文字不丢，发布不失败。
            var shortTitle = PlatformLimits.WechatShortTitle(p.ShortTitle ?? title);
            var wechatCaption = caption;
            string? finalShortTitle = shortTitle;
            if (shortTitle.Length is > 0 and < PlatformLimits.WechatShortTitleMinChars)
            {
                finalShortTitle = null;
                wechatCaption = PrependTitleLine(shortTitle, caption);
            }
            return (title, wechatCaption,
                BuildWechatVideo(draft, p, title, wechatCaption, finalShortTitle, plainTopicNames));
        }

        var (outTitle, description, extraDataJson) = target.PlatformKey switch
        {
            PlatformNames.Douyin => (title, douyinCaption,
                BuildDouyin(draft, p, title, douyinCaption, flatTopics)),
            PlatformNames.Kuaishou => BuildKuaishouBranch(),
            PlatformNames.Xiaohongshu => (title, caption,
                BuildXhs(draft, p, title, caption, flatTopics)),
            // 视频号自己会做同一件事（WechatVideoContentService 里再追加一次，幂等），
            // 正文要先合成好 —— 它与 DTO 的 Description 是同一份，别让两边不一样
            PlatformNames.WechatVideo => BuildWechatBranch(),
            // B站 desc 不塞 #话题（话题走 topic_id，标签走 tag）—— 用未追加话题的 raw
            PlatformNames.Bilibili => (title, raw, BuildBilibili(draft, p, title, raw, topics)),
            // 头条 Abstract 要含 #话题 纯文本（1.4 落点表）—— 单独合成（见局部函数）
            PlatformNames.Toutiao => BuildToutiaoBranch(),
            _ => (title, caption, (string?)null),
        };

        return new PublishTargetDto(
            PlatformKey: target.PlatformKey,
            PlatformSessionId: target.SessionId,
            Title: outTitle,
            Description: description,
            CoverFilePath: draft.CoverPath,
            // 合集走 extra data 里的平台侧 ID，服务端列 CollectionId 客户端不再发送（1.6 删除）
            ExtraDataJson: extraDataJson);
    }

    // ═══ 抖音 ═══

    private static string BuildDouyin(PublishDraft d, PlatformParams p, string title, string caption,
        List<ResolvedTopic> flatTopics)
    {
        // 话题：两个 API 参数都是「只含有 ID 的那些」，格式由 SDK 的 BuildCreateItem 消费。
        // text 的坐标必须与 BuildCreateItem 里拼的 item.common.text 完全一致（标题 + 空格 + 简介），
        // 因为 text_extra 的 start/end 是以那条正文为坐标系校验的
        var douyinText = title + " " + (string.IsNullOrEmpty(caption) ? title : caption);
        var (textExtra, challenges) = BuildDouyinTopics(flatTopics, douyinText, caption);

        return PublishPayload.Serialize(new DouyinPayload
        {
            Caption = caption,
            Title = title,
            ScheduledAt = d.IsScheduled ? d.ScheduledAt : null,
            Declaration = d.DeclarationKind,
            DeclarationDetail = d.Declaration.Clone(),
            Visibility = EffectiveVisibility(d, p),
            AllowDownload = p.AllowDownload ?? d.AllowDownload,
            MixId = p.CollectionId,
            CollectionOrder = p.CollectionOrder,
            Location = p.Location,
            HotSentence = p.HotspotSentence,
            TextExtra = textExtra,
            Challenges = challenges,
            // 封面（1.0 / 2.5.1）：帧时间来自草稿，裁剪与工具信息走抖音专有分组
            CoverFrameMs = d.CoverFrameMs,
            CoverCropWidth = p.Douyin?.CoverCropWidth,
            CoverCropHeight = p.Douyin?.CoverCropHeight,
            CoverToolsInfo = p.Douyin?.CoverToolsInfo,
            CoverToolsExtendInfo = p.Douyin?.CoverToolsExtendInfo,
            // 长尾（1.3 抖音表）
            Mentions = p.Douyin?.Mentions,
            InteractionStickers = p.Douyin?.InteractionStickers,
            MusicId = p.MusicId,
            Chapters = NonEmpty(p.Chapters),
            // 2.6.5 活动落点：结构化 Activities 优先，空表才回落旧 ActivityIds
            ActivityIds = EffectiveActivityIds(p),
            SyncTargets = NonEmpty(p.SyncTargets),
        });
    }

    /// <summary>
    /// 抖音话题的两个 API 形态：<c>text_extra</c> + <c>challenges</c>。
    ///
    /// ⚠️ **不要自己拼**：这里曾经手拼过一份，把 <c>start</c>/<c>end</c> 写成 0，
    /// 而抖音会拿它们去正文里核对那个 #话题 的位置 —— 对不上就整条拒：
    /// `status_code=35，视频描述参数不合法`（2026-09-20 实测，网页代理与 HTTP 两条路都倒在这）。
    /// 现在直接复用 douyin 那条**已经跑通**的实现（<see cref="DouyinVideoService.BuildHashtagExtras"/>）：
    ///   · 偏移量以 <c>item.common.text</c> 为坐标系，caption 里才有的用 caption 坐标兜底
    ///   · 按 cid 去重
    ///   · <c>challenges</c> 是**数字数组**（cid 数字），不是对象数组
    /// **只放命中 ID 的话题** —— 没命中的早在 <see cref="ComposeCaption"/> 里就从正文删掉了
    /// （抖音正文里留一个对不上话题的 `#` 会被整条拒收）。
    /// </summary>
    private static (string? TextExtra, string? Challenges) BuildDouyinTopics(
        List<ResolvedTopic> topics, string text, string caption)
    {
        var results = topics
            .Where(t => !string.IsNullOrWhiteSpace(t.PlatformId))
            .Select(t => new DouyinHashtagResult(t.Name, t.PlatformId!, 0))
            .ToList();
        if (results.Count == 0) return (null, null);

        var (textExtra, challenges) = DouyinVideoService.BuildHashtagExtras(text, caption, results);
        return (textExtra, challenges);
    }

    // ═══ 快手 ═══

    private static string BuildKuaishou(PublishDraft d, PlatformParams p, string title, string caption,
        List<ResolvedTopic> flatTopics)
    {
        // recTagIdList 同样受「话题标签最多 3 个」约束 —— 正文裁了这里也得裁，
        // 否则平台按 ID 列表再数一遍照样拒（2026-10-22 实测见 PlatformLimits.KuaishouMaxTopics）
        var recTags = flatTopics
            .Select(t => long.TryParse(t.PlatformId, out var id) ? id : 0)
            .Where(id => id > 0)
            .Take(PlatformLimits.KuaishouMaxTopics)
            .ToList();

        return PublishPayload.Serialize(new KuaishouPayload
        {
            Title = title,
            Caption = caption,
            ScheduledAt = d.IsScheduled ? d.ScheduledAt : null,
            Declaration = d.DeclarationKind,
            DeclarationDetail = d.Declaration.Clone(),
            Visibility = EffectiveVisibility(d, p),
            AllowDownload = p.AllowDownload ?? d.AllowDownload,
            CollectionId = long.TryParse(p.CollectionId, out var cid) ? cid : null,
            CollectionOrder = p.CollectionOrder,
            Location = p.Location,
            ShowInNearby = p.ShowInNearby,
            AllowSameFrame = p.AllowSameFrame,
            NotifyFollowers = p.NotifyFollowers,
            RecTagIds = recTags.Count > 0 ? recTags : null,
            // 封面（1.0 / 2.5.2）：帧时间来自草稿，文字与 PK 封面走快手专有分组
            CoverFrameMs = d.CoverFrameMs,
            CoverTitle = p.CoverTitle,
            CoverTitleStyle = p.Kuaishou?.CoverTitleStyle,
            PkCoverKey = p.Kuaishou?.PkCoverKey,
            PkCoverTimeStamp = p.Kuaishou?.PkCoverTimeStamp,
            PkCoverType = p.Kuaishou?.PkCoverType,
            PkCoverSize = p.Kuaishou?.PkCoverSize,
            ProfileCoverKey = p.Kuaishou?.ProfileCoverKey,
            // 长尾（1.3 快手表）
            RiseQuality = p.Kuaishou?.RiseQuality,
            UseAiCaption = p.Kuaishou?.UseAiCaption,
            UseAiCaptionCover = p.Kuaishou?.UseAiCaptionCover,
            UseAiCover = p.Kuaishou?.UseAiCover,
            UseIdealTime = p.Kuaishou?.UseIdealTime,
            DisallowRecreation = p.Kuaishou?.DisallowRecreation,
            Domain = p.Kuaishou?.Domain,
            SecondDomain = p.Kuaishou?.SecondDomain,
            MovieId = p.Kuaishou?.MovieId,
            ProjectId = p.Kuaishou?.ProjectId,
            // 章节 / 共创：快手抓包这两个数组恒为空，元素结构未知 —— 原始 JSON 透传，不猜字段名
            ChaptersJson = p.Kuaishou?.ChaptersJson,
            CoPublishUsersJson = p.Kuaishou?.CoPublishUsersJson,
            CoPublishRole = p.Kuaishou?.CoPublishRole,
            // 2.6.5 活动落点：结构化 Activities 优先，空表才回落旧 ActivityIds
            ActivityIds = EffectiveActivityIds(p),
        });
    }

    // ═══ 小红书 ═══

    private static string BuildXhs(PublishDraft d, PlatformParams p, string title, string caption,
        List<ResolvedTopic> flatTopics)
    {
        // hash_tag 的 name 用**平台侧名字**（TopicRef 已按平台名存；旧解析保留 PlatformName），
        // 正文里追加的仍是用户输入的名字 —— 两者在旧实现里就刻意分开。
        var topics = flatTopics
            .Where(t => !string.IsNullOrWhiteSpace(t.PlatformId))
            .Select(t => new XhsTopicRef(t.PlatformId!, t.PlatformName ?? t.Name))
            .ToList();

        // 活动落点：小红书没有独立的活动字段 —— 活动 = 关联话题进 hash_tag。
        // 只写「官方活动」没写任何带 ID 的话题时活动不会生效，这里明说一句（不拦发布）。
        if (p.Activities.Count > 0 && !topics.Any())
        {
            Log.Warning("[Publish] 小红书：活动 {Ids} 没有可下发的话题，发布后不会参与活动"
                + "（请在 params.txt 的「话题ID」补该平台话题）",
                string.Join(",", p.Activities.Select(a => a.Id)));
        }

        return PublishPayload.Serialize(new XhsPayload
        {
            Caption = caption,
            Title = title,
            ScheduledAt = d.IsScheduled ? d.ScheduledAt : null,
            Declaration = d.DeclarationKind,
            DeclarationDetail = d.Declaration.Clone(),
            Visibility = EffectiveVisibility(d, p),
            AllowDownload = p.AllowDownload ?? d.AllowDownload,
            CollectionId = p.CollectionId,
            Location = p.Location,
            // 全局原创（d.IsOriginal）+ 旧账号预设覆盖（p.IsOriginal）—— 覆盖优先
            IsOriginal = p.IsOriginal ?? d.IsOriginal,
            PrivacyUserIds = [.. d.VisibilityUserIds],
            Topics = topics.Count > 0 ? topics : null,
            // 封面（1.0 / 2.5.3）：帧时间来自草稿（执行端换算成秒）
            CoverFrameMs = d.CoverFrameMs,
            CollectionOrder = p.CollectionOrder,
            // 长尾（1.3 小红书表）
            Summary = p.Summary,
            AllowComment = p.AllowComment,
            Chapters = NonEmpty(p.Chapters),
            ProductIds = NonEmpty(p.ProductIds),
            Ats = p.Xhs?.Ats,
            GoodsInfo = p.Xhs?.GoodsInfo,
            BizRelations = p.Xhs?.BizRelations,
            SketchCollectionId = p.Xhs?.SketchCollectionId,
            OrderBind = p.Xhs?.OrderBind,
            ChapterSyncText = p.Xhs?.ChapterSyncText,
            PkCoverBizRelations = p.Xhs?.PkCoverBizRelations,
        });
    }

    // ═══ 视频号 ═══

    private static string BuildWechatVideo(
        PublishDraft d, PlatformParams p, string title, string caption, string? shortTitle,
        List<string> topicNames)
    {
        return PublishPayload.Serialize(new WechatVideoPayload
        {
            Title = title,
            Caption = caption,
            ShortTitle = shortTitle,
            // ⚠️ 视频号的 ScheduledAt 约定是 **UTC 墙上时间**：SDK 那边
            // （WechatVideoContentService，老版也用它）拿它 `SpecifyKind(Utc)` 换算秒数，
            // 而草稿里存的是本地墙上时间 —— 直接传过去会**晚 8 小时**（老版是先转 epoch
            // 再还原成 UTC 墙上时间，绕了一圈反而对）。这里就地对齐成 UTC。
            ScheduledAt = d.IsScheduled ? d.ScheduledAt?.ToUniversalTime() : null,
            Declaration = d.DeclarationKind,
            DeclarationDetail = d.Declaration.Clone(),
            // 视频号没有可见性设置 —— 恒公开（UI 上会写明，不静默忽略）
            Visibility = PublishVisibility.Public,
            // 视频号请求体没有「允许下载」字段 —— 以前载荷上带着它是假支持（1.6 删除）
            CollectionId = p.CollectionId,
            CollectionName = p.CollectionName,
            Location = p.Location,
            // 声明 → tagType 在这里定死；tagKey 是会话级 token，执行时随账号缓存取
            TagType = DeclarationMapping.WechatTagType(d.DeclarationKind),
            TagKey = p.WechatTagKey,
            // 视频号话题是纯文本（铁律三）：名字 = 草稿统一列表 ∪ 平台卡话题（2.6.5）。
            // 不消费平台 ID —— 平台卡里带 ID 的话题也只取名字，由执行端写进正文 +
            // finderTopicInfo（WechatVideoContentService.BuildPostBody）。
            Topics = topicNames.Count > 0 ? topicNames : null,
            // 长尾（1.3 视频号表）
            CollectionOrder = p.CollectionOrder,
            ExternalLink = p.ExternalLink,
            ExternalLinkTitle = p.WechatVideo?.ExtReadingTitle,
            ExternalLinkType = p.WechatVideo?.ExtReadingType ?? 1,
            CardShowStyle = p.WechatVideo?.CardShowStyle,
            // 封面（2.5.7）：没选封面图时按这个时间点抽帧当封面（留空 = 0.5 秒）
            CoverFrameMs = d.CoverFrameMs,
        });
    }

    /// <summary>标题并入正文首行 —— 正文已以标题开头时不重复（短标题 &lt;6 字的下沉路径）。</summary>
    private static string PrependTitleLine(string title, string caption)
    {
        var c = caption ?? "";
        if (c.StartsWith(title, StringComparison.Ordinal)) return c;
        return string.IsNullOrWhiteSpace(c) ? title : title + "\n" + c;
    }

    // ═══ B站（1.7 只组装，执行在阶段 4） ═══

    private static string BuildBilibili(PublishDraft d, PlatformParams p, string title, string caption,
        List<TopicRef> topics)
    {
        var b = p.Bilibili;
        var tags = PlatformLimits.BilibiliTags(p.Tags);

        // 话题（2.6.5）：B站的 tag ≠ 话题 —— 话题走 add_v3 的 topic_id，活动走话题的 mission_id。
        // 只带结构化 ID 的话题（纯文本话题在 B站没有落点，预检 2.6.6 会提示）。
        var bTopics = topics
            .Where(t => t.HasPlatformId)
            .Select(t => new BilibiliTopicRef(t.Id!, t.Name, t.MissionId))
            .ToList();

        // 活动任务号 = 话题自带 + 已选活动 ID（B站活动就是从话题 mission_id 聚合出来的，
        // params.txt 的「官方活动」同样按 mission_id 写）
        var missionIds = new List<long>();
        var seenMission = new HashSet<long>();
        foreach (var t in topics)
            if (t.MissionId > 0 && seenMission.Add(t.MissionId)) missionIds.Add(t.MissionId);
        foreach (var a in p.Activities)
            if (long.TryParse(a.Id, out var m) && m > 0 && seenMission.Add(m)) missionIds.Add(m);

        return PublishPayload.Serialize(new BilibiliPayload
        {
            Title = title,
            // B站 desc **不塞 #话题**（话题是 topic_id，阶段 2.6）；标签走 tag
            Caption = caption,
            // 定时 = add/v3.dtime（Unix 秒）：抓包时开关是关的，字段名取自平台模型 archive.dtime；
            // 执行端把 ScheduledAt 转成 dtime，null = 立即（不下发该字段）。
            ScheduledAt = d.IsScheduled ? d.ScheduledAt : null,
            Declaration = d.DeclarationKind,
            DeclarationDetail = d.Declaration.Clone(),
            Visibility = EffectiveVisibility(d, p),
            // 封面（2.5.7）：没选封面图时按这个时间点自动抽帧，再裁 16:9 / 4:3（留空 = 0.5 秒）
            CoverFrameMs = d.CoverFrameMs,
            Tags = tags.Count > 0 ? tags : null,
            // 2.6.5 话题/活动落点（wire 名待阶段 4 探针：add_v3 样本没有话题字段）
            Topics = bTopics.Count > 0 ? bTopics : null,
            MissionIds = missionIds.Count > 0 ? missionIds : null,
            Tid = b?.Tid ?? 21,
            HumanType2 = b?.HumanType2 ?? 1015,
            Copyright = CopyrightMapping.Bilibili(d.DeclarationKind, p.IsOriginal ?? d.IsOriginal),
            CreationStatementId = DeclarationMapping.BilibiliCreationStatement(d.DeclarationKind),
            Dynamic = b?.Dynamic,
            NoReprint = b?.NoReprint ?? false,
            Watermark = b?.Watermark ?? true,
            SubtitleOpen = b?.SubtitleOpen ?? false,
            SubtitleLan = b?.SubtitleLan,
            AllowComment = p.AllowComment,
            AllowDanmaku = p.AllowDanmaku,
            UpSelectionReply = b?.UpSelectionReply ?? false,
            ActReserve = b?.ActReserve ?? false,
            ActReserveTitle = b?.ActReserveTitle,
            AiCover = b?.AiCover,
            IsAbCover = b?.IsAbCover,
            AbCoverInfo = b?.AbCoverInfo,
            Interactive = b?.Interactive ?? false,
            SpaceHidden = b?.SpaceHidden,
            Dolby = b?.Dolby ?? false,
            LosslessMusic = b?.LosslessMusic ?? false,
            IsOriginal = p.IsOriginal ?? d.IsOriginal,
        });
    }

    // ═══ 头条（1.7 只组装，执行在阶段 5） ═══

    private static string BuildToutiao(PublishDraft d, PlatformParams p, string title, string caption,
        List<TopicRef> topics, List<string> topicNames)
    {
        var t = p.Toutiao;

        // 横竖版在**提交时**定死：执行时拿不到视频宽高（PublishPlatformItem 没有宽高字段）。
        // 元数据缺失（仅自检/异常路径）按横版处理 —— 与平台的默认投稿形态一致。
        var horizontal = d.Meta is null || d.Meta.Width >= d.Meta.Height;

        // 结构化话题（2.6.5）：详情回读的元素形状 = {HashtagId:"…", HashtagName:"…"}。
        // 头条没有可用的推荐接口（2.6.1 探针返回空），ID 目前只能来自 params.txt「话题ID」；
        // PublishVideo 是否接受该字段待阶段 5 探针，正文里的 #话题 是兜底。
        var hashtags = topics
            .Where(tp => tp.HasPlatformId)
            .Select(tp => new ToutiaoHashtagRef(tp.Id!, tp.Name))
            .ToList();

        return PublishPayload.Serialize(new ToutiaoPayload
        {
            Title = title,
            // 竖版没有简介（1.3 头条表：Abstract 仅横版）
            Caption = horizontal ? caption : null,
            // 竖版不支持定时；横版才带 TimerTime
            ScheduledAt = horizontal && d.IsScheduled ? d.ScheduledAt : null,
            // 作品声明提交字段名未抓 → v1 不下发；载荷仍带语义供阶段 5 接字段
            Declaration = d.DeclarationKind,
            DeclarationDetail = d.Declaration.Clone(),
            Visibility = EffectiveVisibility(d, p),
            // 封面（2.5.7）：头条封面必填，没选图时按这个时间点自动抽帧（留空 = 0.5 秒）
            CoverFrameMs = d.CoverFrameMs,
            VideoType = horizontal ? 3 : 6,
            IsOriginal = p.IsOriginal ?? d.IsOriginal,
            Praise = p.Praise ?? false,
            // PrependPSeriesId 仅横版生效 —— 载荷保留意图，执行端按 VideoType 决定是否下发
            CollectionId = p.CollectionId,
            // 纯文本名字（草稿统一列表 ∪ 平台卡话题）—— 正文 # 的兜底口径
            Topics = topicNames.Count > 0 ? topicNames : null,
            // 结构化 HashtagList（2.6.5）：元素形状取自详情回读，wire 位置待阶段 5 探针
            Hashtags = hashtags.Count > 0 ? hashtags : null,
            ExternalLink = p.ExternalLink,
            SyncTargets = NonEmpty(p.SyncTargets),
            Tags = NonEmpty([.. p.Tags]),
            CoverTitle = p.CoverTitle,
            TemplateName = t?.TemplateName,
            FilterName = t?.FilterName,
            Commodity = t?.Commodity,
            CompassVideo = t?.CompassVideo,
            AttrsValueMap = t?.AttrsValueMap,
            EtData = t?.EtData,
        });
    }

    // ═══ 共用 ═══

    /// <summary>空列表不进载荷（序列化时被 <c>WhenWritingNull</c> 省掉，旧队列记录也更干净）</summary>
    private static List<T>? NonEmpty<T>(List<T> items) => items.Count > 0 ? items : null;

    /// <summary>
    /// 2.6.5 话题**单一事实来源**：结构化 <see cref="PlatformParams.TopicRefs"/>
    /// ∪ 旧 <see cref="PlatformParams.Topics"/>。返回两份视图：
    ///   · <c>Refs</c> —— 结构化引用（带 Type / MissionId / Source，B站/头条需要）；
    ///   · <c>Flat</c> —— 旧 <see cref="ResolvedTopic"/>（带 PlatformName，正文合成与
    ///     抖音/快手/小红书 ID 下发沿用既有口径）。
    ///
    /// 为什么要合：params.txt 的「话题ID」只写 TopicRefs，平台卡（2.6.3）写 TopicRefs +
    /// 桥接旧字段，联邦解析/旧档只写 Topics —— 只读一边就会静默漏发。
    /// 同名（忽略大小写）只留一份，**优先带平台 ID / 任务号的那份**。
    /// </summary>
    internal static (List<TopicRef> Refs, List<ResolvedTopic> Flat) MergeTopics(PlatformParams p)
    {
        var refs = new List<TopicRef>();
        var flat = new List<ResolvedTopic>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in p.TopicRefs)
        {
            var name = t.Name.Trim();
            if (name.Length == 0) continue;
            if (index.TryGetValue(name, out var i))
            {
                // 同名结构化话题：带 ID / 任务号的优先
                if ((!refs[i].HasPlatformId && t.HasPlatformId)
                    || (refs[i].MissionId <= 0 && t.MissionId > 0))
                {
                    refs[i] = t with { Name = name };
                    flat[i] = new ResolvedTopic(name, t.Id, name);
                }
                continue;
            }
            index[name] = refs.Count;
            refs.Add(t.Name == name ? t : t with { Name = name });
            flat.Add(new ResolvedTopic(name, t.Id, name));
        }

        foreach (var t in p.Topics)
        {
            var name = t.Name.Trim();
            if (name.Length == 0) continue;
            var platformName = string.IsNullOrWhiteSpace(t.PlatformName) ? name : t.PlatformName;
            if (index.TryGetValue(name, out var i))
            {
                // 结构化只有名字（手动/平台卡）而旧解析命中了 ID → 补上 ID 与平台名
                if (!refs[i].HasPlatformId && !string.IsNullOrWhiteSpace(t.PlatformId))
                {
                    refs[i] = refs[i] with { Id = t.PlatformId };
                    flat[i] = flat[i] with { PlatformId = t.PlatformId, PlatformName = platformName };
                }
                continue;
            }
            index[name] = refs.Count;
            // 旧档/旧解析：保留 PlatformName —— 小红书 hash_tag 用的是平台侧名字
            refs.Add(new TopicRef { Id = t.PlatformId, Name = name, Source = TopicRefSources.Legacy });
            flat.Add(new ResolvedTopic(name, t.PlatformId, platformName));
        }

        return (refs, flat);
    }

    /// <summary>
    /// 纯文本话题名（视频号 / 头条用）：草稿统一列表 ∪ 平台卡结构化话题的名字。
    /// 不取 <see cref="PlatformParams.Topics"/> —— 那是解析缓存，预设话题已经由
    /// <c>PublishDraftStore</c> / <c>PublishDraftFactory</c> 并回草稿列表。
    /// </summary>
    internal static List<string> PlainTopicNames(PublishDraft d, PlatformParams p)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in d.Topics)
        {
            var v = n.Trim();
            if (v.Length > 0 && seen.Add(v)) names.Add(v);
        }
        foreach (var t in p.TopicRefs)
        {
            var v = t.Name.Trim();
            if (v.Length > 0 && seen.Add(v)) names.Add(v);
        }
        return names;
    }

    /// <summary>
    /// 该目标要下发的官方活动 ID：结构化 <see cref="PlatformParams.Activities"/>（2.6）优先，
    /// 空表才回落旧 <see cref="PlatformParams.ActivityIds"/>（1.4 透传口 / 升级前旧档）。
    /// **只回退不合并** —— UI 与 params.txt 都只写 Activities，旧 ID 只是存量兼容。
    /// </summary>
    internal static List<string>? EffectiveActivityIds(PlatformParams p)
    {
        var source = p.Activities.Count > 0
            ? p.Activities.Select(a => a.Id)
            : p.ActivityIds.AsEnumerable();
        var ids = source
            .Select(id => id?.Trim() ?? "")
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return ids.Count > 0 ? ids : null;
    }

    /// <summary>
    /// 把话题名以 `#名字` 追加进正文（纯文本平台用 —— 头条）。
    /// 与 <see cref="ComposeCaption"/> 的区别：**不看平台 ID**，用户写的话题一律追加。
    /// 正文里已有的不重复追加（按整词匹配）。
    /// </summary>
    private static string AppendTopicNames(string caption, IEnumerable<string> names)
    {
        var body = caption;
        foreach (var name in names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (HasHashtag(body, name)) continue;
            body = body.Length == 0 ? "#" + name : body + " #" + name;
        }
        return body;
    }

    /// <summary>
    /// 把话题**合进正文**：`#名字` 追加在末尾（正文里已经写过的就不重复追加）。
    ///
    /// 为什么必须合：话题在线上不是一个独立字段。抖音的 <c>item.common.text</c> 里得真有那个
    /// `#话题`，<c>text_extra</c> 的偏移量才落得下去 —— 正文里没有 `#`，这条话题就是**静默不发**。
    /// 老版是反过来的：用户把 `#话题` 写在简介里，软件按平台过滤
    /// （<c>FilterDescriptionForPlatform</c>）。改用 params.txt 后话题单独写一行，
    /// 「一行话题」→「正文里的 #话题」这一步就落在这里。
    ///
    /// <paramref name="stripUnresolved"/> = 正文里那些**没解析出 ID** 的 `#xxx` 要不要删掉。
    /// **只有抖音传 true**：抖音会拿正文里的 `#` 去核对 <c>text_extra</c>，对不上整条拒收
    /// ——「status_code=35，视频描述参数不合法」（2026-09-20 实测：`简介: 美丽的大自然 #大自然`
    /// 连拒三次，把那半截 `#大自然` 去掉就过了）。老版对抖音/快手/小红书都删，这里只删抖音：
    /// 其余平台不拒，那几行字是用户自己写的。删掉的那部分会记一条日志，不静默。
    /// </summary>
    private static string ComposeCaption(string caption, List<ResolvedTopic> topics, bool stripUnresolved)
    {
        // 「已解析」= 该平台查到了 ID，能当话题标记发出去
        var resolved = topics.Where(t => !string.IsNullOrWhiteSpace(t.PlatformId)).ToList();
        var body = caption;

        if (stripUnresolved)
        {
            var keep = resolved.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dropped = new List<string>();
            body = HashtagRegex().Replace(body, m =>
            {
                var name = m.Groups[1].Value;
                if (keep.Contains(name)) return m.Value;
                dropped.Add(name);
                return "";
            });
            // 删完会留下两个连着的空格（老版 FilterDescriptionForPlatform 也是这么收的尾）
            body = SpaceRunRegex().Replace(body, " ").Trim();
            if (dropped.Count > 0)
                Log.Warning("[Publish] 抖音：正文里的 {Tags} 没解析出话题 ID，已从抖音正文里去掉"
                    + "（其余平台保留）", string.Join(" ", dropped.Select(d => "#" + d)));
        }

        var missing = resolved
            .Where(t => t.Name.Length > 0 && !HasHashtag(body, t.Name))
            .Select(t => t.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count == 0) return body;

        var tags = string.Join(" ", missing.Select(n => "#" + n));
        // 正文可能是空的（简介没写）—— 那就别在前面留个空格
        return body.Length == 0 ? tags : body + " " + tags;
    }

    /// <summary>
    /// 快手最终文案（**标题 + 简介 + 话题**）—— 预检与组装的唯一口径。
    ///
    /// 快手没有独立标题字段，<c>caption</c> 是唯一文本落点：标题不进正文就整段丢失
    /// （2026-11-09 真实作品实测，风险 #30）。裁剪/截断规则见
    /// <see cref="ComposeKuaishouCaption"/>。
    /// </summary>
    internal static string KuaishouCaption(PublishDraft draft, PlatformParams p)
    {
        var (_, flat) = MergeTopics(p);
        return ComposeKuaishouCaption(
            PrependTitleLine(SanitizeText(draft.Title), SanitizeText(draft.Description)), flat);
    }

    /// <summary>
    /// 快手合并后的文案（标题 + 简介 + 话题），**未做** 3 标签裁剪与 500 字截断 ——
    /// 预检数「标签超限 / 字数超限」用它（裁完就永远数不出超限了）。
    /// </summary>
    internal static string KuaishouComposedCaption(PublishDraft draft, PlatformParams p)
    {
        var (_, flat) = MergeTopics(p);
        return ComposeCaption(
            PrependTitleLine(SanitizeText(draft.Title), SanitizeText(draft.Description)),
            flat, stripUnresolved: false);
    }

    /// <summary>
    /// 快手正文合成：已解析话题补进正文 → 把 <c>#话题</c> 裁到前
    /// <see cref="PlatformLimits.KuaishouMaxTopics"/> 个 → 按
    /// <see cref="PlatformLimits.KuaishouCaptionChars"/> 截断。
    ///
    /// 快手平台侧的硬限：2026-10-22 实测 4 个话题直接拒
    /// <c>result=1000, msg=话题标签数量超过上限：4</c>。数的是**正文里全部 #标签**
    /// （用户自己写在标题/简介里的也算），所以裁剪放在合成之后，而不是只截话题列表。
    /// 500 字上限来自发布页计数器实测 `0/500`（docs/kuaishou/20261105/字段映射.md）——
    /// 标题并入正文后更容易超，超出按「不切断 #话题」的边界截断。超出部分记日志，不静默丢。
    /// </summary>
    /// <param name="maxChars">截断上限（默认 500；预检传 <see cref="int.MaxValue"/> 只为数长度）</param>
    public static string ComposeKuaishouCaption(string caption, List<ResolvedTopic> topics,
        int maxChars = PlatformLimits.KuaishouCaptionChars)
    {
        var body = ComposeCaption(caption, topics, stripUnresolved: false);

        var kept = 0;
        var dropped = new List<string>();
        body = KuaishouHashtagRegex().Replace(body, m =>
        {
            if (kept++ < PlatformLimits.KuaishouMaxTopics) return m.Value;
            dropped.Add(m.Groups[1].Value);
            return "";
        });

        if (dropped.Count > 0)
        {
            body = SpaceRunRegex().Replace(body, " ").Trim();
            Log.Warning("[Publish] 快手：话题标签最多 {Max} 个，已省略 {Tags}（平台侧会拒收超限提交）",
                PlatformLimits.KuaishouMaxTopics, string.Join(" ", dropped.Select(d => "#" + d)));
        }

        if (body.Length <= maxChars) return body;

        var cut = CutAtHashtagBoundary(body, maxChars);
        Log.Warning("[Publish] 快手：标题+简介+话题合并后 {Len} 字，超过 {Max} 字上限，已截断到 {Cut} 字",
            body.Length, maxChars, cut.Length);
        return cut;
    }

    /// <summary>
    /// 按长度截断，但**不切断正在写的 #话题**：切点落在话题中间时退回该话题的 # 之前。
    /// 半个话题会被平台解析成一个不存在的话题标签，还会占掉「最多 3 个」里的一个名额。
    /// </summary>
    private static string CutAtHashtagBoundary(string body, int max)
    {
        var cut = body[..max];
        var hash = cut.LastIndexOf('#');
        if (hash >= 0
            && max < body.Length
            && !char.IsWhiteSpace(body[max])
            && body[max] != '#'
            && !cut[(hash + 1)..].Any(char.IsWhiteSpace))
        {
            return cut[..hash].TrimEnd();
        }
        return cut.TrimEnd();
    }

    /// <summary>正文里有多少个 <c>#话题</c>（预检提示用；含用户自己写在标题/简介里的）</summary>
    public static int KuaishouHashtagCount(string composedCaption) =>
        KuaishouHashtagRegex().Matches(composedCaption).Count;

    /// <summary>
    /// 小红书最终正文（简介 + 带 ID 话题的 <c>#话题</c> 追加）—— 预检核对 1000 字上限用（2.6.6）。
    /// 与 <see cref="BuildXhs"/> 收到的 caption 同源：同一份 <see cref="SanitizeText"/> +
    /// <see cref="MergeTopics"/> + <see cref="ComposeCaption"/>，两处口径必须一致，
    /// 否则预检按一段文本数、实际发的却是另一段。
    /// </summary>
    internal static string XhsCaption(PublishDraft draft, PlatformParams p)
    {
        var (_, flat) = MergeTopics(p);
        return ComposeCaption(SanitizeText(draft.Description), flat, stripUnresolved: false);
    }

    /// <summary>
    /// 快手话题的切法 —— 与抖音共用同一段名字字符集，但**多吃一个尾缀 `#`**：
    /// 快手网页版的话题是 <c>#话题#</c> 形状，用户从网页复制到简介里会带尾缀；
    /// 裁第 4 个时若不连尾缀一起删，正文里会留一个孤零零的 `#`。
    /// </summary>
    [GeneratedRegex(@"#([\w一-鿿㐀-䶿]+)#?")] private static partial Regex KuaishouHashtagRegex();

    /// <summary>正文里已经有这个 `#话题` 了吗（按整词匹配，`#海` 不该拦住 `#海边`）</summary>
    private static bool HasHashtag(string text, string name) =>
        HashtagRegex().Matches(text)
            .Any(m => string.Equals(m.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>`#话题` 的切法 —— 与 <see cref="DouyinVideoService"/> 算偏移量用的是**同一个**模式，
    /// 两边一旦不一致，追加进正文的名字就会跟 text_extra 对不上（抖音直接拒收）</summary>
    [GeneratedRegex(DouyinVideoService.HashtagPattern)] private static partial Regex HashtagRegex();

    /// <summary>删掉话题后收尾用：两个及以上连续空格/制表符 → 一个空格</summary>
    [GeneratedRegex(@"[ \t]{2,}")] private static partial Regex SpaceRunRegex();

    /// <summary>
    /// 清掉会坑平台的**不可见字符**（与老版 <c>HomeViewModel.SanitizeText</c> 同一张表）：
    /// U+00A0 NBSP → 普通空格；U+200B/200C/200D 零宽空格/连字/不连字 → 直接删。
    ///
    /// 为什么必须做：这些字符肉眼看不见，但**实打实占一个字符位**。抖音的 text_extra 是拿
    /// 偏移量去正文里核对 #话题 位置的，差一个字符整条就被拒
    /// （`status_code=35，视频描述参数不合法`）；四个平台的标题/简介都过这一道，行为与老版一致。
    /// </summary>
    internal static string SanitizeText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        // （下面四个就是那四个字符本身，与老版 HomeViewModel.SanitizeText 同一份表）
        return s.Replace(' ', ' ')      // NBSP（中文输入法、网页复制常见）
                .Replace("​", "")       // 零宽空格
                .Replace("‌", "")       // 零宽不连字
                .Replace("‍", "");      // 零宽连字
    }

    /// <summary>
    /// 该目标实际生效的可见性：条目覆盖 &gt; 账号预设 &gt; 全局。
    /// 不支持的平台在此**回退公开**（预检会提示，不静默）。
    /// </summary>
    private static PublishVisibility EffectiveVisibility(PublishDraft d, PlatformParams p) =>
        p.Visibility ?? d.Visibility;
}
