using CreatFlow.Platforms.Xiaohongshu;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 「草稿 → 提交请求」的共享组装件（发布页提交路径与合成自动发布路径**同一份实现**）。
///
/// 为什么必须共享而不是各写一份：播种顺序（预设先、params 后覆盖）、payload 自包含口径
/// （话题/位置/声明全进 extra data）、哈希硬前置 —— 两条路各写一遍必然漂移，
/// 而漂移的症状是「手动发布好好的、自动发布偶发丢参数」，最难查的一类。
/// </summary>
internal static class PublishDraftFactory
{
    /// <summary>
    /// 把一批目标写进草稿：按（平台 × 账号）建 <see cref="PublishTarget"/> 并把账号级预设
    /// clone 进 <c>Overrides</c>（= 发布页 `ApplyGlobalTargetsTo` 的核心）。
    /// 调用方负责先清空语义（本方法自己清）。
    /// </summary>
    internal static void SeedTargets(
        PublishDraft draft,
        IReadOnlyList<PublishTarget> targets,
        IReadOnlyDictionary<string, PlatformParams> defaults)
    {
        draft.Targets.Clear();
        draft.Overrides.Clear();
        draft.DisabledTargets.Clear();

        foreach (var target in targets)
        {
            draft.Targets.Add(target);

            var key = PublishDraftStore.KeyOf(target.PlatformKey, target.SessionId);
            if (defaults.TryGetValue(key, out var preset))
            {
                var clone = preset.Clone();
                // 兜底迁移：正常路径（PublishDraftStore.LoadDefaultsAsync）已迁过；
                // 这里再守一道，防止把内存里构造的旧形状预设播种进草稿时丢结构化话题/活动
                clone.MigrateLegacyTopics();
                clone.MigrateLegacyActivities();
                clone.MigrateLegacyKuaishouChapters();
                draft.Overrides[target.Key] = clone;

                // 话题**单一来源**（2026-11-06）：账号预设里的历史话题并入草稿的全局话题列表。
                // 不进草稿的后果：视频号只读 draft.Topics（纯文本，不消费解析结果）→ 预设话题
                // 只对抖音/快手/小红书生效、视频号静默漏发，编辑器顶部也看不到这些芯片。
                // 保留 p.Topics 里的 id 直到重新解析（解析成功才覆盖，失败仍能按旧 id 发）。
                foreach (var t in clone.Topics)
                    if (t.Name.Length > 0 && !draft.Topics.Contains(t.Name)) draft.Topics.Add(t.Name);
                // 结构化话题（2.6）同理：预设里已钉死的 ID 话题，名字也要出现在全局芯片区
                foreach (var t in clone.TopicRefs)
                    if (t.Name.Length > 0 && !draft.Topics.Contains(t.Name)) draft.Topics.Add(t.Name);
                foreach (var a in clone.Activities)
                    foreach (var t in a.Topics)
                        if (t.Name.Length > 0 && !draft.Topics.Contains(t.Name)) draft.Topics.Add(t.Name);
            }
        }
    }

    /// <summary>
    /// params.txt 的字段 → 草稿。**必须在 <see cref="SeedTargets"/> 之后调**：
    /// 后者会把账号预设 clone 进 <c>Overrides</c>，顺序反了预设会把 params 盖掉，
    /// 而且 UI 上完全看不出来。
    /// 标题/简介/封面不在这里 —— 它们在展开阶段就并进候选了。
    /// </summary>
    internal static void ApplyUnitParams(PublishDraft draft, UnitParams p)
    {
        if (p.Visibility is { } visibility) draft.Visibility = visibility;
        if (p.AllowDownload is { } allowDownload) draft.AllowDownload = allowDownload;
        if (p.Declaration is { } declaration) draft.Declaration.Kind = declaration;

        if (p.IsScheduled is { } scheduled)
        {
            draft.IsScheduled = scheduled;
            // 解析器保证 IsScheduled=false 时 ScheduledAt 为 null ——
            // 否则预检会以「定时发布必须选时间」把整批拦下
            draft.ScheduledAt = p.ScheduledAt;
        }

        foreach (var topic in p.Topics)
            if (!draft.Topics.Contains(topic)) draft.Topics.Add(topic);

        // 平台标签（2026-11-10）：B站投稿必填 ≥1（预检 Error）、头条 Label 同源，
        // 其余平台不读。params 是内容级 → 写进每个目标的 Params.Tags（叠加在预设之上）。
        if (p.Tags.Count > 0)
        {
            foreach (var target in draft.Targets)
            {
                var tp = draft.ParamsFor(target);
                foreach (var tag in p.Tags)
                    if (!tp.Tags.Contains(tag)) tp.Tags.Add(tag);
            }
        }

        ApplyStructuredTopics(draft, p);

        // 原创 2026-11-06 上提为全局字段（原先是落在小红书的账号参数上）。
        // params.txt 的 `原创` 现在对小红书/B站/头条同时生效。
        if (p.IsOriginal is { } original) draft.IsOriginal = original;

        // B站分区（阶段 4）：params.txt 是内容级、分区是平台级 —— 只写 B站目标，
        // 不给其它平台凭空造覆盖（同话题 ID / 活动的分发口径）。
        if (p.BilibiliTid is not null || p.BilibiliHumanType2 is not null)
        {
            foreach (var target in draft.Targets)
            {
                if (target.PlatformKey != PlatformNames.Bilibili) continue;
                var bp = draft.ParamsFor(target);
                var extras = bp.Bilibili ??= new BilibiliExtras();
                if (p.BilibiliTid is { } tid) extras.Tid = tid;
                if (p.BilibiliHumanType2 is { } type2) extras.HumanType2 = type2;
            }
        }

        // B站字幕 / 互动引导卡片（B5，2026-11-15）：内容级 → 只写 B站目标。
        // 字幕路径与「抖音横版封面」同口径：相对路径按发布单元的文件夹解析
        // （params.txt 与 .srt 同目录时只写文件名就够）；解析不出来按原值走，
        // 预检会以「文件不存在」报错，不会静默当成没写。
        if (p.BilibiliSubtitleFile is not null || p.BilibiliSubtitleLan is not null
            || p.BilibiliGuideCard is not null || p.BilibiliGuideCardStartSeconds is not null
            || p.BilibiliRelatedVideo is not null || p.BilibiliRelatedVideoMsg is not null
            || p.BilibiliRelatedVideoStartSeconds is not null
            || p.BilibiliVoteQuestion is not null || p.BilibiliVoteOptions is not null
            || p.BilibiliVoteStartSeconds is not null || p.BilibiliVoteDurationSeconds is not null)
        {
            string? subtitlePath = null;
            if (!string.IsNullOrWhiteSpace(p.BilibiliSubtitleFile))
            {
                var raw = p.BilibiliSubtitleFile.Trim();
                var subtitleBaseDir = draft.SourceFolder ?? Path.GetDirectoryName(draft.VideoPath);
                subtitlePath = Path.IsPathRooted(raw) || string.IsNullOrEmpty(subtitleBaseDir)
                    ? raw
                    : Path.GetFullPath(Path.Combine(subtitleBaseDir, raw));
            }

            foreach (var target in draft.Targets)
            {
                if (target.PlatformKey != PlatformNames.Bilibili) continue;
                var bp = draft.ParamsFor(target);
                var extras = bp.Bilibili ??= new BilibiliExtras();
                if (subtitlePath is not null) extras.SubtitleFile = subtitlePath;
                if (!string.IsNullOrWhiteSpace(p.BilibiliSubtitleLan))
                    extras.SubtitleFileLan = p.BilibiliSubtitleLan.Trim();
                if (p.BilibiliGuideCard is { } card)
                {
                    extras.GuideCardEnabled = card;
                    // 关掉时别留着上一份样式 —— 下次再打开不会莫名变成「仅关注」
                    if (!card) extras.GuideCardFollowType = null;
                }
                if (p.BilibiliGuideCardFollowType is { } followType)
                    extras.GuideCardFollowType = followType;
                if (p.BilibiliGuideCardStartSeconds is { } startSeconds)
                    extras.GuideCardStartSeconds = startSeconds;

                // 卡片 v2（2026-11-15 B5.2）：关联视频 / 投票弹幕 ——
                // params 写了键就视为开启（B站关联视频 / B站投票 的值不可能为空 ——
                // 空值在解析层就按「没写」处理了）。
                if (p.BilibiliRelatedVideo is { } relatedBvid)
                {
                    extras.RelatedVideoEnabled = true;
                    extras.RelatedVideoBvid = relatedBvid.Trim();
                }
                if (p.BilibiliRelatedVideoMsg is { } relatedMsg)
                    extras.RelatedVideoMsg = relatedMsg.Trim();
                if (p.BilibiliRelatedVideoStartSeconds is { } relatedStart)
                    extras.RelatedVideoStartSeconds = relatedStart;

                if (p.BilibiliVoteQuestion is { } voteQuestion)
                {
                    extras.VoteEnabled = true;
                    extras.VoteQuestion = voteQuestion.Trim();
                }
                if (p.BilibiliVoteOptions is { Count: > 0 } voteOptions)
                {
                    extras.VoteOptionA = voteOptions.ElementAtOrDefault(0);
                    extras.VoteOptionB = voteOptions.ElementAtOrDefault(1);
                    extras.VoteOptionC = voteOptions.ElementAtOrDefault(2);
                    extras.VoteOptionD = voteOptions.ElementAtOrDefault(3);
                }
                if (p.BilibiliVoteStartSeconds is { } voteStart)
                    extras.VoteStartSeconds = voteStart;
                if (p.BilibiliVoteDurationSeconds is { } voteDuration)
                    extras.VoteDurationSeconds = voteDuration;
            }
        }

        // 头条长尾（阶段 5.6 / A2）：赞赏 / 模板 / 滤镜 / 视频生成图文 —— 同样只写头条目标
        if (p.ToutiaoPraise is not null || p.ToutiaoTemplate is not null
            || p.ToutiaoFilter is not null || p.ToutiaoVideoTransArticle is not null)
        {
            foreach (var target in draft.Targets)
            {
                if (target.PlatformKey != PlatformNames.Toutiao) continue;
                var tp = draft.ParamsFor(target);
                if (p.ToutiaoPraise is { } praise) tp.Praise = praise;
                if (p.ToutiaoTemplate is not null || p.ToutiaoFilter is not null
                    || p.ToutiaoVideoTransArticle is not null)
                {
                    var extras = tp.Toutiao ??= new ToutiaoExtras();
                    if (p.ToutiaoTemplate is { } template) extras.TemplateName = template;
                    if (p.ToutiaoFilter is { } filter) extras.FilterName = filter;
                    // 0 = 不生成（存 0；组装时归一到 null，不会下发）
                    if (p.ToutiaoVideoTransArticle is { } trans)
                        extras.VideoTransArticleFlag = trans;
                }
            }
        }

        // 抖音横版封面（D1，2026-11-14）：内容级 → 写进每个抖音目标。
        // 相对路径按**发布单元的文件夹**解析（params.txt 与图同目录时只写文件名就够）；
        // 绝对路径原样用。解析不出来（没 SourceFolder 也不是绝对路径）就按原值走，
        // 预检会以「文件不存在」报 Warning，不会静默当成没写。
        if (!string.IsNullOrWhiteSpace(p.DouyinLandscapeCover))
        {
            var raw = p.DouyinLandscapeCover.Trim();
            var baseDir = draft.SourceFolder ?? Path.GetDirectoryName(draft.VideoPath);
            var path = Path.IsPathRooted(raw) || string.IsNullOrEmpty(baseDir)
                ? raw
                : Path.GetFullPath(Path.Combine(baseDir, raw));

            foreach (var target in draft.Targets)
            {
                if (target.PlatformKey != PlatformNames.Douyin) continue;
                var dp = draft.ParamsFor(target);
                (dp.Douyin ??= new DouyinExtras()).CoverLandscapePath = path;
            }
        }

        // 快手封面文字样式（P3，2026-11-15）：内容级 → 只写快手目标。
        // 取值未验证（编辑提交里恒为空串）；留空/不写 = 不下发（官方发布页就是不发）。
        if (!string.IsNullOrWhiteSpace(p.KuaishouCoverTitleStyle))
        {
            foreach (var target in draft.Targets)
            {
                if (target.PlatformKey != PlatformNames.Kuaishou) continue;
                var kp = draft.ParamsFor(target);
                (kp.Kuaishou ??= new KuaishouExtras()).CoverTitleStyle =
                    p.KuaishouCoverTitleStyle.Trim();
            }
        }

        // 章节（D1）：抖音 / 快手 / 小红书共用 PlatformParams.Chapters —— 只写消费这个字段的
        // 三家，其余平台连 ParamsFor 都不碰（否则会给它们造出与预设相同的空覆盖，
        // UI 上莫名显示「已覆盖预设」，同 B站分区 / 头条长尾的分发口径）。
        if (p.Chapters.Count > 0)
        {
            foreach (var target in draft.Targets)
            {
                if (target.PlatformKey != PlatformNames.Douyin
                    && target.PlatformKey != PlatformNames.Kuaishou
                    && target.PlatformKey != PlatformNames.Xiaohongshu) continue;
                draft.ParamsFor(target).Chapters = [.. p.Chapters];
            }
        }

        // 视频号 @好友（W5，2026-11-14）：内容级 → 只写视频号目标；平台只认昵称。
        if (p.WechatMentionUsers.Count > 0)
        {
            foreach (var target in draft.Targets)
            {
                if (target.PlatformKey != PlatformNames.WechatVideo) continue;
                var wp = draft.ParamsFor(target);
                (wp.WechatVideo ??= new WechatVideoExtras()).MentionUsers =
                    [.. p.WechatMentionUsers.Select(n => new WechatVideoMentionRef(n))];
            }
        }

        // 小红书 @用户（P5，2026-11-15）：内容级 → 只写小红书目标。发布体 common.ats
        // 由 BuildAtsJson 按页面条目形状 {nickname,userId,userid} 组装；正文末尾的
        // 「@昵称」由 XhsCaption 按同一份 Ats 补写（与编辑器选择器同一条链）。
        if (p.XhsMentions.Count > 0 && XhsMentionService.BuildAtsJson(p.XhsMentions) is { } xhsAts)
        {
            foreach (var target in draft.Targets)
            {
                if (target.PlatformKey != PlatformNames.Xiaohongshu) continue;
                var xp = draft.ParamsFor(target);
                (xp.Xhs ??= new XhsExtras()).Ats = xhsAts;
            }
        }

        // 快手专属领域（P3/P5，2026-11-15）：内容级 → 只写快手目标；
        // submit.domain / submit.secondDomain 两个键，留空不下发（serviced 侧归一为空串）。
        if (!string.IsNullOrWhiteSpace(p.KuaishouDomain)
            || !string.IsNullOrWhiteSpace(p.KuaishouSecondDomain))
        {
            foreach (var target in draft.Targets)
            {
                if (target.PlatformKey != PlatformNames.Kuaishou) continue;
                var kp = draft.ParamsFor(target);
                var extras = kp.Kuaishou ??= new KuaishouExtras();
                if (!string.IsNullOrWhiteSpace(p.KuaishouDomain))
                    extras.Domain = p.KuaishouDomain.Trim();
                if (!string.IsNullOrWhiteSpace(p.KuaishouSecondDomain))
                    extras.SecondDomain = p.KuaishouSecondDomain.Trim();
            }
        }
    }

    /// <summary>
    /// <c>话题ID</c> / <c>官方活动</c> → 各目标的 <see cref="PlatformParams.TopicRefs"/> / <see cref="PlatformParams.Activities"/>。
    ///
    /// params.txt 是**内容级**的（一份文件对所有账号），但话题 ID / 活动 ID 是**平台级**的：
    /// 所以解析时就要求写平台前缀（<c>抖音:家居=1658…</c>），这里按 target 的平台分发。
    /// 不匹配的目标连 <see cref="PublishDraft.ParamsFor"/> 都不碰 —— 否则会给它们造出
    /// 一份与预设完全相同的空覆盖，UI 上莫名其妙出现「已覆盖预设」。
    /// </summary>
    private static void ApplyStructuredTopics(PublishDraft draft, UnitParams unit)
    {
        if (unit.TopicRefs.Count == 0 && unit.Activities.Count == 0) return;

        foreach (var target in draft.Targets)
        {
            var platform = PlatformOf(target.Key);
            var refs = unit.TopicRefs.Where(r => r.PlatformKey == platform).ToList();
            var acts = unit.Activities.Where(a => a.PlatformKey == platform).ToList();
            if (refs.Count == 0 && acts.Count == 0) continue;

            var p = draft.ParamsFor(target);
            foreach (var u in refs)
            {
                p.UpsertTopic(u.Topic);
                if (!draft.Topics.Contains(u.Topic.Name)) draft.Topics.Add(u.Topic.Name);
            }

            foreach (var u in acts)
            {
                p.UpsertActivity(u.Activity);
                foreach (var t in u.Activity.Topics)
                    if (t.Name.Length > 0 && !draft.Topics.Contains(t.Name)) draft.Topics.Add(t.Name);
            }
        }

        static string PlatformOf(string targetKey)
        {
            var i = targetKey.IndexOf(':');
            return i > 0 ? targetKey[..i] : targetKey;
        }
    }

    /// <summary>
    /// 把一条草稿组装成 submit 请求 —— 每个启用目标一份 wire 载荷。
    /// 这里**不做网络解析**：话题/位置已经在外面解析好并存进了 PlatformParams，
    /// 提交只是本地组装 + 算一次视频哈希。返回 null = 视频读不到 / 哈希失败 / 没有启用目标。
    /// </summary>
    internal static async Task<SubmitPublishRequest?> BuildSubmitRequestAsync(
        PublishDraft draft, IReadOnlyDictionary<string, PlatformParams> defaults)
    {
        if (!File.Exists(draft.VideoPath))
        {
            Log.Warning("[Publish] 视频不存在，跳过：{Path}", draft.VideoPath);
            return null;
        }

        var fi = new FileInfo(draft.VideoPath);

        // 视频哈希给服务端做记录（**不能为空**，见 VideoHasher 的注释）
        string videoHash;
        try
        {
            videoHash = await VideoHasher.Sha256Async(draft.VideoPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Publish] 视频哈希失败，跳过 {File}", draft.FileName);
            return null;
        }

        var targets = new List<PublishTargetDto>();
        foreach (var target in draft.EnabledTargets)
        {
            var key = PublishDraftStore.KeyOf(target.PlatformKey, target.SessionId);
            var p = draft.ParamsFor(target, defaults.GetValueOrDefault(key));
            targets.Add(PublishPayloadBuilder.Build(draft, target, p));
        }

        if (targets.Count == 0) return null;

        return new SubmitPublishRequest(
            VideoFilePath: draft.VideoPath,
            VideoFileName: draft.FileName,
            VideoFileSize: fi.Length,
            VideoHash: videoHash,
            VideoDurationS: draft.Meta is { } m ? (int)Math.Round(m.DurationS) : null,
            VideoWidth: draft.Meta?.Width,
            VideoHeight: draft.Meta?.Height,
            CoverFilePath: draft.CoverPath,
            CommonTitle: draft.Title,
            CommonDesc: draft.Description,
            ScheduledAt: draft.IsScheduled ? draft.ScheduledAt : null,
            Targets: targets);
    }
}
