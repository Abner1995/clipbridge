using CreatFlow.Platforms.Bilibili;
using CreatFlow.Platforms.Douyin;
using CreatFlow.Platforms.Topics;
using CreatFlow.Platforms.Xiaohongshu;

namespace CreatFlow.NewApp.Services;

/// <summary>
/// 发布预检 —— 提交前、重试前、编辑时共用同一套规则（设计文档 §5.2）。
///
/// 为什么必须收口：老版把校验写在 UI 层的 499 行巨方法里，于是**队列重试路径完全没有校验** ——
/// 小红书 ≥10 秒那条只有个软提示，不选视频连提示都不出（缺陷 L12）。
/// 规则收在这里，三个时机（导入后 / 编辑时 / 提交前）与重试路径共用。
/// </summary>
public static class PublishPreflight
{
    /// <summary>视频号标题最短字数（平台要求，HomeViewModel.cs:3736 实证）</summary>
    private const int WechatMinTitleChars = 6;

    /// <summary>定时最短提前量（老版 HomeViewModel.cs:937 同名常量照搬）</summary>
    private static readonly TimeSpan MinScheduleAdvance = TimeSpan.FromHours(2);

    /// <summary>定时最远提前量（老版 HomeViewModel.cs:940 同名常量照搬）</summary>
    private static readonly TimeSpan MaxScheduleAdvance = TimeSpan.FromDays(14);

    /// <summary>B站定时最短提前量（2026-11-07 用户实测投稿页：「最早 ≥5 分钟」）</summary>
    private static readonly TimeSpan BilibiliMinScheduleAdvance = TimeSpan.FromMinutes(5);

    /// <summary>B站定时最远提前量（同页：「最晚 ≤15 天」）</summary>
    private static readonly TimeSpan BilibiliMaxScheduleAdvance = TimeSpan.FromDays(15);

    /// <summary>
    /// 平台自己的定时窗口。<b>多平台一起定时时必须满足所有平台的窗口 → 取交集</b>
    /// （见 <see cref="ScheduleWindowErrorForTargets"/>）。
    /// 只有 B站 是页面实测窗口；其余五平台沿用老版 2 小时 ~ 14 天。
    /// </summary>
    public static (TimeSpan Min, TimeSpan Max) ScheduleWindowFor(string platformKey)
        => platformKey == PlatformNames.Bilibili
            ? (BilibiliMinScheduleAdvance, BilibiliMaxScheduleAdvance)
            : (MinScheduleAdvance, MaxScheduleAdvance);

    /// <summary>
    /// 按**本次启用的目标平台**取窗口交集校验定时（纯函数）：null = 合法。
    /// 没有启用目标时按默认窗口（ValidateCommon 另有「没有选择任何发布目标」的 Error）。
    ///
    /// 为什么不能只用一份全局窗口：B站 允许 5 分钟后就发、也允许排到 15 天后，
    /// 而其余平台是 2 小时 ~ 14 天 —— 用全局窗口会把合法的 B站定时误杀
    /// （2026-11-07 用户实测「B站投稿页有定时发布」时抓到的真 bug）。
    /// </summary>
    public static string? ScheduleWindowErrorForTargets(PublishDraft draft, DateTime at, DateTime now)
    {
        var keys = draft.Targets
            .Where(draft.IsTargetEnabled)
            .Select(t => t.PlatformKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var min = keys.Count == 0 ? MinScheduleAdvance : keys.Max(k => ScheduleWindowFor(k).Min);
        var max = keys.Count == 0 ? MaxScheduleAdvance : keys.Min(k => ScheduleWindowFor(k).Max);
        return ScheduleWindowError(at, now, min, max);
    }

    /// <summary>
    /// 定时发布的「下一个合法默认落点」：now+2h 再向上取整到整点。
    /// 老版不设默认（用户自己选、选完校验）；新版给默认值，就必须让它**天然满足** 2 小时下限 ——
    /// 否则拨到「定时」的瞬间就挂一条预检错误。跨天交给 <see cref="DateTime.AddHours"/> 算，别重拼年月日。
    /// </summary>
    public static DateTime NextScheduleSlot() => NextScheduleSlot(DateTime.Now);

    /// <inheritdoc cref="NextScheduleSlot()"/>
    public static DateTime NextScheduleSlot(DateTime now)
    {
        var t = now.Add(MinScheduleAdvance);
        // 先向下归零分秒，再 +1 小时 = 严格向上取整到整点（≥ now+2h，且不留「恰好 2 小时、
        // 下一秒就违规」的边界）
        return new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0).AddHours(1);
    }

    /// <summary>
    /// 定时窗口校验（纯函数）：null = 合法，否则给用户看的原因。
    /// 预检与**合成页提交前**共用同一口径 —— 开了自动发布却把定时拨到窗口外，
    /// 不该等到队列里才失败（那是「以为发了其实没发」的一类）。
    /// </summary>
    public static string? ScheduleWindowError(DateTime at, DateTime now)
        => ScheduleWindowError(at, now, MinScheduleAdvance, MaxScheduleAdvance);

    /// <inheritdoc cref="ScheduleWindowError(DateTime, DateTime)"/>
    /// <remarks>窗口可指定 —— 按平台取交集时用（<see cref="ScheduleWindowErrorForTargets"/>）。</remarks>
    public static string? ScheduleWindowError(
        DateTime at, DateTime now, TimeSpan minAdvance, TimeSpan maxAdvance)
    {
        if (at <= now) return "定时时间已过";
        if (at < now.Add(minAdvance))
            return $"定时发布时间不能早于当前时间 {DescribeWindow(minAdvance)}"
                 + $"（最早 {now.Add(minAdvance):MM-dd HH:mm}）";
        if (at > now.Add(maxAdvance))
            return $"定时发布时间不能超过 {DescribeWindow(maxAdvance)}"
                 + $"（最晚 {now.Add(maxAdvance):MM-dd HH:mm}）";
        return null;
    }

    /// <summary>窗口文案：2 小时 / 14 天 / 5 分钟（别把 2 小时写成「120 分钟」）。</summary>
    private static string DescribeWindow(TimeSpan span)
        => span.TotalDays >= 1 ? $"{span.TotalDays:0.#} 天"
         : span.TotalHours >= 1 ? $"{span.TotalHours:0.#} 小时"
         : $"{span.TotalMinutes:0.#} 分钟";

    /// <summary>
    /// 校验一条草稿的全部目标。返回值按目标聚合，平台为 null 的是条目级问题。
    /// 不抛异常 —— 预检本身绝不该成为失败的来源。
    /// </summary>
    public static List<PreflightIssue> Validate(
        PublishDraft draft, PlatformParams? presetFor = null)
    {
        var issues = new List<PreflightIssue>();
        try
        {
            ValidateCommon(draft, issues);
            foreach (var target in draft.Targets)
            {
                if (!draft.IsTargetEnabled(target)) continue;
                var p = draft.ParamsFor(target, presetFor);
                ValidateForPlatform(draft, target, p, issues);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Preflight] 校验异常（按无问题继续）");
        }
        return issues;
    }

    private static void ValidateCommon(PublishDraft draft, List<PreflightIssue> issues)
    {
        if (!File.Exists(draft.VideoPath))
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Error,
                $"视频文件不存在：{draft.FileName}"));
            return;
        }

        if (draft.Targets.Count == 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Error, "没有选择任何发布目标"));
            return;
        }

        if (string.IsNullOrWhiteSpace(draft.Title))
            issues.Add(new PreflightIssue(PreflightSeverity.Error, "标题不能为空"));

        if (draft.IsScheduled && draft.ScheduledAt is null)
            issues.Add(new PreflightIssue(PreflightSeverity.Error, "已选定时发布但没设时间"));

        // 定时窗口：**按本次启用的目标平台取交集**（窗口外平台侧会拒收）。
        //   · B站：5 分钟 ~ 15 天（2026-11-07 用户实测投稿页）；
        //   · 其余五平台：老版 2 小时 ~ 14 天（HomeViewModel.cs:936-966）。
        // 边界恰等于端点合法，用严格 < / >。
        if (draft.IsScheduled && draft.ScheduledAt is { } at
            && ScheduleWindowErrorForTargets(draft, at, DateTime.Now) is { } scheduleError)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Error, scheduleError));
        }

        // 白名单只在「只给谁看 / 不给谁看」下有意义 —— 选了这两个态却没填人，
        // 平台会拒（小红书 privacy_info 要求 user_ids 必填）
        if (draft.Visibility is PublishVisibility.OnlyUsers or PublishVisibility.ExcludeUsers
            && draft.VisibilityUserIds.Count == 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Error,
                "可见性选了「只给谁看 / 不给谁看」，但没有选任何人"));
        }

        if (draft.VisibilityUserIds.Count > VisibilityMapping.XhsMaxPrivacyUsers)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Error,
                $"可见性白名单超过 {VisibilityMapping.XhsMaxPrivacyUsers} 人上限"));
        }
    }

    private static void ValidateForPlatform(
        PublishDraft draft, PublishTarget target, PlatformParams p, List<PreflightIssue> issues)
    {
        var key = target.PlatformKey;
        var name = PlatformNames.GetDisplayName(key);

        if (!target.IsLoggedIn)
            issues.Add(new PreflightIssue(PreflightSeverity.Error,
                $"{name} · {target.AccountLabel} 登录已过期，请重新登录", key));

        // ── 可见性回退：**要说明白，不静默**（设计文档 §4.2） ──
        if (VisibilityMapping.FallbackNote(key, draft.Visibility) is { } note)
            issues.Add(new PreflightIssue(PreflightSeverity.Warning, $"{name}：{note}", key));

        // ── 声明能力空洞：不支持就置灰说明，不静默丢弃（§4.3） ──
        // 头条有平台专有覆盖（A1）：只要覆盖非 null（含「不声明」），就按平台卡的选择走，
        // 不再对全局语义报「不支持」—— 否则选了「投资观点」也会被全局营销声明提示打扰。
        var toutiaoOverride = key == PlatformNames.Toutiao
            ? p.Toutiao?.DeclarationOverride
            : null;
        if (toutiaoOverride is null
            && DeclarationMapping.UnsupportedReason(key, draft.DeclarationKind) is { } reason)
            issues.Add(new PreflightIssue(PreflightSeverity.Warning, $"{name}：{reason}，该平台将不做声明", key));

        // ── 位置：抖音/小红书要求 ID 与名称同时非空，否则整个位置不发（缺陷 L10） ──
        if (p.Location is { } loc && !loc.IsUsable && !string.IsNullOrWhiteSpace(loc.Name))
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：位置「{loc.Name}」缺少平台侧 ID，将不带位置", key));

        // ── 话题 / 活动（2.6.6）：结构化话题缺 ID、活动已结束、平台没有活动能力 ——
        //    全是「用户以为设了、其实会被静默丢掉」的地方，必须说清楚 ──
        ValidateTopics(draft, p, name, key, issues);
        ValidateActivities(p, name, key, issues);

        switch (key)
        {
            case PlatformNames.Douyin:
                ValidateDouyin(draft, p, name, key, issues);
                break;
            case PlatformNames.Kuaishou:
                ValidateKuaishou(draft, p, name, key, issues);
                break;
            case PlatformNames.WechatVideo:
                ValidateWechatVideo(draft, p, name, key, issues);
                break;
            case PlatformNames.Xiaohongshu:
                ValidateXhs(draft, p, name, key, issues);
                break;
            case PlatformNames.Bilibili:
                ValidateBilibili(draft, p, name, key, issues);
                break;
            case PlatformNames.Toutiao:
                ValidateToutiao(draft, p, name, key, issues);
                break;
        }
    }

    /// <summary>
    /// 头条：视频合集（pSeries）只支持横版视频（<c>VideoType=3</c>），竖版（<c>6</c>）挂不上。
    /// 宽高来自后台 ffprobe，未就绪时不下结论（避免误报）。
    /// </summary>
    private static void ValidateToutiao(
        PublishDraft draft, PlatformParams p, string name, string key, List<PreflightIssue> issues)
    {
        var portrait = IsPortrait(draft);

        if (!string.IsNullOrWhiteSpace(p.CollectionId) && portrait)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：头条视频合集只支持横版视频（当前 {draft.Meta!.Width}×{draft.Meta.Height} 竖版），不会带合集", key));
        }

        // 标题 1~30（平台要求）；执行端会截断并继续，这里提前说明
        if ((draft.Title ?? "").Trim().Length > PlatformLimits.ToutiaoTitleChars)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：头条标题最多 {PlatformLimits.ToutiaoTitleChars} 字，超出部分不会发送", key));
        }

        // 竖版：没有简介 / 定时字段（抓包 `video_publish_vertical.json` 三样都没有）。
        // 定时是**错误**：定时了却立即发出去是最糟的失败模式（执行端同样会拦）。
        if (portrait)
        {
            if (!string.IsNullOrWhiteSpace(draft.Description))
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：头条竖版视频没有简介字段，简介不会发送", key));
            }
            if (draft.ScheduledAt is not null)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Error,
                    $"{name}：头条竖版视频不支持定时发布 —— 取消定时或改用横版视频", key));
            }

            // 可见性（2026-11-09 实测）：同一「仅自己」草稿，横版回读 HideType=1、
            // 竖版回读 HideType=0（公开）—— **平台忽略竖版的 HideInfo**。
            // 用户以为设了「仅自己」、发出去却是公开：隐私风险。这不是「效果打折」，
            // 是**违背用户明确意图**，所以拦提交（Error）：两条出路 —— 改横版，
            // 或把可见性改成公开再发。只提示照发等于把隐私事故推给用户。
            var visibility = VisibilityMapping.Coerce(PlatformNames.Toutiao,
                p.Visibility ?? draft.Visibility);
            if (visibility != PublishVisibility.Public)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Error,
                    $"{name}：头条竖版视频不支持发布前可见性 —— 平台会忽略"
                    + $"「{VisibilityMapping.VisibilityLabel(visibility)}」，发布后是公开作品；"
                    + "请改用横版视频，或把可见性改为公开后再发", key));
            }
        }

        // 原创声明（2026-11-10 复审定论）：平台侧视频原创能力不可用 —— 发布页
        // GetPublishAuth 返回 ClaimOriginPermission=false + CanNotClaimOriginReason=原创功能已下线；
        // 受控发布提交「原创: 是」、审核完成后回读仍是 ClaimOrigin=0 / isOriginal=false。
        // 发布体里字段照发（无害），但用户会以为勾了就生效 —— 必须点名说清楚。
        if (p.IsOriginal ?? draft.IsOriginal)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：视频原创声明当前不可用（平台侧「原创功能已下线」），勾选的原创不会生效", key));
        }

        // 视频生成图文=仅保存草稿（2026-11-11 真账号定论）：平台只在视频本身存草稿
        // （PublishType=0）时才保留图文草稿；当前发布流程恒发 PublishType=1，该值被静默
        // 忽略、图文随视频发布（可见性同视频）。这不是「效果打折」，要让用户明确知道。
        if (p.Toutiao?.VideoTransArticleFlag is 2)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：头条「仅保存草稿」只在视频本身存草稿时生效；当前发布流程会立即发布视频，"
                + "平台会忽略该选项，生成的图文随视频发布", key));
        }

        // 互动贴纸（A3）：页面「+添加贴纸」对竖版与 <10 秒视频置灰（2026-11-10 灰文案实测）。
        // 贴纸是可选项、创建失败也不拦发布 —— 这里只提示，不拦提交。
        if (p.Toutiao?.FollowSticker == true)
        {
            if (portrait)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：头条互动贴纸只支持横版视频（当前竖版），关注引导不会添加", key));
            }
            if (draft.Meta is { DurationS: < 10 })
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：视频时长不足 10 秒，头条不支持互动贴纸，关注引导不会生效", key));
            }
        }

        // 音轨（2026-11-09 实测）：无音轨视频 PublishVideo 接口成功，但审核判
        // 「视频源文件缺少音频信息」，作品不可用（两条实测均中）。Meta 未探测时不下结论。
        if (draft.Meta is { HasAudio: false })
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：视频没有音轨，头条审核会判「缺少音频信息」，作品发布后不可用", key));
        }

        // 封面必填：没选封面图又读不到视频信息 → 自动抽帧可能失败，发布会被平台拒
        if (string.IsNullOrWhiteSpace(draft.CoverPath) && draft.Meta is null)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：头条封面必填；未选封面时靠自动抽首帧，需要先读到视频信息", key));
        }

        // 作品声明（A1）：能力空洞统一由 DeclarationMapping.UnsupportedReason 报一次
        // （2026-11-07 用户截图实测，不要重复）；这里只补「自行拍摄」的二级信息缺失 ——
        // 抓包 CreativeInfo 是 {City, CreativeTime(毫秒)}，缺项时不编造，只提示。
        if (DeclarationMapping.ToutiaoComplianceType(
                draft.DeclarationKind, p.Toutiao?.DeclarationOverride) == 1)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(p.Toutiao?.ComplianceCity)) missing.Add("拍摄城市");
            if (p.Toutiao?.ComplianceTime is null) missing.Add("拍摄时间");
            if (missing.Count > 0)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：头条「自行拍摄」声明缺少{string.Join("、", missing)}，CreativeInfo 将不完整", key));
            }
        }
    }

    /// <summary>
    /// 话题落点预检（2.6.6）：选中的话题没有平台 ID 时，各平台会**静默降级或丢弃**
    /// （落点见 2.6.5）—— 在这里点名说清楚，不让用户以为「选了就发出去了」。
    /// </summary>
    private static void ValidateTopics(
        PublishDraft draft, PlatformParams p, string name, string key, List<PreflightIssue> issues)
    {
        var (refs, _) = PublishPayloadBuilder.MergeTopics(p);

        // 视频号没有话题列表接口（2.6.1）：话题按正文纯文本下发是**设计**，不是缺 ID 的缺陷。
        // 有话题时给一条 Info 说明，不按条报 Warning。
        var cap = TopicServiceCatalog.CapabilityOf(key);
        if (!cap.HasAnyTopicApi)
        {
            if (refs.Count > 0 || draft.Topics.Count > 0)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Info,
                    $"{name}：没有话题列表接口，已选话题只按正文「#话题」纯文本发送", key));
            }
            return;
        }

        var missing = refs
            .Where(t => !t.HasPlatformId)
            .Select(t => t.Name.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count == 0) return;

        // 各平台对「只有名字」的话题处理（2.6.5 落点）：
        //   抖音：正文里的 # 也会被清掉（对不上 text_extra 会被 status_code=35 拒收）
        //   快手：不进 recTagIdList（最多只剩正文文本）
        //   小红书：不进 hash_tag（只能手写在正文里）
        //   B站：没有正文话题落点，这条话题完全不会发出去
        //   头条：不进 HashtagList（正文 #文本 兜底）
        var effect = key switch
        {
            PlatformNames.Douyin => "不会作为抖音话题下发（正文里的 #标签 也会被清掉）",
            PlatformNames.Kuaishou => "不会进快手结构化话题列表（最多只剩正文文本）",
            PlatformNames.Xiaohongshu => "不会关联小红书话题（只能手写在正文里）",
            PlatformNames.Bilibili => "B站不会带上这条话题（话题必须来自平台接口或 params.txt 的「话题ID」）",
            // 头条：横版有 Abstract（正文 #文本 兜底），**竖版没有简介字段**（Caption 整个不下发），
            // 所以竖版连文本兜底都不存在 —— 提示必须说清楚「完全不会发送」
            PlatformNames.Toutiao => IsPortrait(draft)
                ? "不会进头条结构化话题列表，且竖版没有简介 → 该话题完全不会发送"
                : "不会进头条结构化话题列表（横版正文 #文本 兜底）",
            _ => "只会按纯文本处理",
        };
        issues.Add(new PreflightIssue(PreflightSeverity.Warning,
            $"{name}：话题「{string.Join("、", missing)}」缺少平台 ID，{effect}", key));
    }

    /// <summary>
    /// 活动落点预检（2.6.6）：已结束的活动不会带来曝光；平台没有活动能力时明确「不会生效」；
    /// 小红书（A6 起）活动有独立绑定 —— 缺带 ID 话题只是少一份赛道曝光，不再等于没参加。
    /// </summary>
    private static void ValidateActivities(
        PlatformParams p, string name, string key, List<PreflightIssue> issues)
    {
        if (p.Activities.Count == 0) return;

        if (!TopicServiceCatalog.SupportsActivities(key))
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：该平台没有官方活动，已选 {p.Activities.Count} 个活动不会生效", key));
            return;
        }

        var ended = p.Activities
            .Where(a => a.IsEnded)
            .Select(a => string.IsNullOrWhiteSpace(a.Name) ? a.Id : a.Name)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ended.Count > 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：活动「{string.Join("、", ended)}」已结束，发布后不会获得活动曝光", key));
        }

        // 小红书（A6，2026-11-11）：活动现在有独立落点 —— business_binds.optionRelationList
        // 的 ACTIVITY_COMPONENT，不再「必须靠关联话题才生效」。缺带 ID 话题只是少了
        // 活动赛道话题的曝光（活动关联话题进 hash_tag 仍是有效曝光手段，保留提示）。
        if (key == PlatformNames.Xiaohongshu)
        {
            var (refs, _) = PublishPayloadBuilder.MergeTopics(p);
            if (!refs.Any(t => t.HasPlatformId))
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：活动已按 ACTIVITY_COMPONENT 绑定，但当前没有带平台 ID 的话题"
                    + " —— 缺少活动赛道话题的曝光（可选：在话题块添加，或在 params.txt 写「话题ID」）",
                    key));
            }
        }
    }

    /// <summary>
    /// B站标签 1~10（2.6.6）：投稿至少要 1 个标签（Error）；超过 10 个 builder 会裁掉，
    /// 说清楚「超出部分不会发送」（Warning，不拦提交）。
    /// </summary>
    private static void ValidateBilibili(
        PublishDraft draft, PlatformParams p, string name, string key, List<PreflightIssue> issues)
    {
        var tags = PlatformLimits.BilibiliAllTags(p.Tags);
        if (tags.Count == 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Error,
                $"{name}：B站投稿至少需要 1 个标签", key));
        }
        else if (tags.Count > PlatformLimits.BilibiliMaxTags)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：B站标签最多 {PlatformLimits.BilibiliMaxTags} 个（当前 {tags.Count} 个），"
                + "超出部分不会发送", key));
        }

        // 定时：窗口校验在 ValidateCommon 按目标平台取交集（B站 5 分钟 ~ 15 天），
        // 执行端写 add/v3 的 dtime —— 这里不再单独拦（曾经的「不支持定时」是误判，
        // 抓包时定时开关是关的，不代表平台没有这个字段）。

        // 封面：没选封面图又读不到视频信息 → 自动抽帧可能失败（平台会用默认帧兜底）
        if (string.IsNullOrWhiteSpace(draft.CoverPath) && draft.Meta is null)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：未选封面，自动抽首帧需要先读到视频信息（失败时用平台默认封面）", key));
        }

        // 分区默认值兜底（params.txt 的「B站分区 / B站二级分区」可覆盖）
        var tid = p.Bilibili?.Tid ?? 21;
        var type2 = p.Bilibili?.HumanType2 ?? 1015;
        if (tid <= 0 || type2 <= 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Error,
                $"{name}：B站分区 ID 不合法（tid={tid} / human_type2={type2}）", key));
        }

        // 字幕（B5，2026-11-15）：用户明确配了字幕，文件就必须真的能传 ——
        // 只支持 .srt（页面 input accept + JS 正则双重校验），文件缺失直接拦
        // （发布后才发现没字幕比拦截更伤人）。
        var b = p.Bilibili;
        if (!string.IsNullOrWhiteSpace(b?.SubtitleFile))
        {
            var subtitlePath = b.SubtitleFile.Trim();
            if (!subtitlePath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Error,
                    $"{name}：B站字幕只支持 .srt 文件（当前：{Path.GetFileName(subtitlePath)}）", key));
            }
            else if (!File.Exists(subtitlePath))
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Error,
                    $"{name}：B站字幕文件不存在：{subtitlePath}", key));
            }
        }

        // 个性化卡片（B5 / B5.2）：互动引导 / 关联视频 / 投票弹幕。
        // 非 H.264 MP4 时投稿页的按钮本身就是灰的 —— **照实提示、不硬拦**（wire §0）。
        var guideEnabled = b?.GuideCardEnabled == true;
        var relatedEnabled = b?.RelatedVideoEnabled == true;
        var voteEnabled = b?.VoteEnabled == true;

        if (guideEnabled || relatedEnabled || voteEnabled)
        {
            if (draft.Meta is { } cardMeta)
            {
                var formatOk = cardMeta.FormatName is null
                    || cardMeta.FormatName.Contains("mp4", StringComparison.OrdinalIgnoreCase);
                var codecOk = cardMeta.CodecName is null
                    || string.Equals(cardMeta.CodecName, "h264", StringComparison.OrdinalIgnoreCase);
                if (!formatOk || !codecOk)
                {
                    issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                        $"{name}：B站个性化卡片只在 H.264 MP4 视频上可用"
                        + $"（当前 {cardMeta.CodecName ?? "未知编码"} / {cardMeta.FormatName ?? "未知格式"}），"
                        + "发布时可能不生效", key));
                }
            }
        }

        if (guideEnabled)
        {
            var followType = b!.GuideCardFollowType ?? 2;
            if (followType is < 0 or > 2)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Error,
                    $"{name}：B站互动引导样式只能选 关注 / 三连 / 三连+关注（当前 {followType}）", key));
            }

            if (draft.Meta is { } meta)
            {
                var startSeconds = b.GuideCardStartSeconds ?? 0;
                if (startSeconds > 0 && meta.DurationS > 0 && startSeconds > meta.DurationS)
                {
                    issues.Add(new PreflightIssue(PreflightSeverity.Error,
                        $"{name}：B站互动引导时间 {startSeconds} 秒超出视频时长 "
                        + $"{meta.DurationS:0.#} 秒", key));
                }
            }
        }

        // 关联视频（B5.2）：开了就必须有目标 BVID（create 体的 oid）。
        // 「仅自己」的目标稿件平台会回 79025 —— 本地无法判断目标可见性，不硬拦，
        // 失败是非致命的（稿件照发，只是这张卡片不生效）。
        if (relatedEnabled)
        {
            if (string.IsNullOrWhiteSpace(b!.RelatedVideoBvid))
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Error,
                    $"{name}：已开启B站关联视频卡片，但没填目标视频 BVID", key));
            }

            if (draft.Meta is { } meta)
            {
                var startSeconds = Math.Max(0, b.RelatedVideoStartSeconds ?? 0);
                if (startSeconds > 0 && meta.DurationS > 0 && startSeconds > meta.DurationS)
                {
                    issues.Add(new PreflightIssue(PreflightSeverity.Error,
                        $"{name}：B站关联视频时间 {startSeconds} 秒超出视频时长 "
                        + $"{meta.DurationS:0.#} 秒", key));
                }
            }
        }

        // 投票弹幕（B5.2）：页面同款规则（问题 ≤12 字、选项 2~4 个且 ≤6 字、
        // 不许含分号 / 重复）；选项必须从 A 开始连续（C 空不能单独填 D）。
        if (voteEnabled)
        {
            if (BilibiliCardService.HasOptionGap(
                    b!.VoteOptionA, b.VoteOptionB, b.VoteOptionC, b.VoteOptionD))
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Error,
                    $"{name}：B站投票选项必须从 A 开始连续填写（C 为空时不能只填 D）", key));
            }

            var voteOptions = BilibiliCardService.VoteOptionsFrom(
                b.VoteOptionA, b.VoteOptionB, b.VoteOptionC, b.VoteOptionD);
            foreach (var problem in BilibiliCardService.ValidateVote(b.VoteQuestion, voteOptions))
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Error,
                    $"{name}：B站{problem}", key));
            }

            if (draft.Meta is { } meta)
            {
                var startSeconds = Math.Max(0, b.VoteStartSeconds ?? 0);
                var durationSeconds = Math.Max(0, b.VoteDurationSeconds ?? 7);
                if (startSeconds > 0 && meta.DurationS > 0 && startSeconds > meta.DurationS)
                {
                    issues.Add(new PreflightIssue(PreflightSeverity.Error,
                        $"{name}：B站投票卡片时间 {startSeconds} 秒超出视频时长 "
                        + $"{meta.DurationS:0.#} 秒", key));
                }
                else if (meta.DurationS > 0 && startSeconds + durationSeconds > meta.DurationS + 0.05)
                {
                    issues.Add(new PreflightIssue(PreflightSeverity.Error,
                        $"{name}：B站投票卡片的起止（{startSeconds} 秒 + {durationSeconds} 秒）"
                        + $"超出视频时长 {meta.DurationS:0.#} 秒", key));
                }
            }
        }

        // 同一时间段两张卡片：页面会 toast「该时间段已配置卡片」，后建的那张可能失败。
        // 卡片失败本来就不拦稿件，这里只提醒（不硬拦）。
        var cardStarts = new List<(string Label, int Start)>();
        if (guideEnabled) cardStarts.Add(("互动引导", Math.Max(0, b?.GuideCardStartSeconds ?? 0)));
        if (relatedEnabled) cardStarts.Add(("关联视频", Math.Max(0, b?.RelatedVideoStartSeconds ?? 0)));
        if (voteEnabled) cardStarts.Add(("投票弹幕", Math.Max(0, b?.VoteStartSeconds ?? 0)));
        var conflicts = cardStarts
            .GroupBy(c => c.Start)
            .Where(g => g.Count() > 1)
            .Select(g => $"{(g.Key == 0 ? "开头" : g.Key + " 秒")}（{string.Join(" / ", g.Select(c => c.Label))}）")
            .ToList();
        if (conflicts.Count > 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：B站多张卡片配在同一时间段 {string.Join("、", conflicts)}，"
                + "平台会提示「该时间段已配置卡片」，后建的卡片可能不生效", key));
        }
    }

    /// <summary>
    /// 竖版判定 —— 与 <see cref="PublishPayloadBuilder"/> 组装头条载荷同口径：
    /// 元数据未探测（Meta=null）按横版处理，避免在「不知道」时误报竖版限制。
    /// </summary>
    private static bool IsPortrait(PublishDraft draft)
        => draft.Meta is { } meta && meta.Height > meta.Width;

    private static void ValidateDouyin(
        PublishDraft draft, PlatformParams p, string name, string key, List<PreflightIssue> issues)
    {
        // 转载选了「抖音作品」却没挑具体作品：from_item_id 为 null，这条必然发不出去
        // （PublishMapping.cs 组装 declare_user_info 时会传空）——提交前拦下
        var d = draft.Declaration;
        if (d.Kind == DeclarationKind.Repost && d.DouyinRepostFromInside
            && string.IsNullOrWhiteSpace(d.DouyinRepostItemId))
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Error,
                $"{name}：转载来源选了「抖音作品」但还没选具体作品", key));
        }

        // 封面帧 vs 自定义封面图：2026-11-05 抓包里带 poster 的那条 poster_delay 恒 0 ——
        // 上传封面图后页面不再用「封面帧」。我们按同一口径不下发帧，这里说清楚，
        // 免得用户以为「选的那一帧」和「自己的图」都生效了。
        if (!string.IsNullOrWhiteSpace(draft.CoverPath) && File.Exists(draft.CoverPath)
            && draft.CoverFrameMs is > 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：已选自定义封面图，抖音不会使用「封面帧」设置（页面同样 poster_delay=0）", key));
        }

        // 添加标签（B1 抓包 2026-11-11）：四类互斥（页面同一个 anchor 单选）。
        // 位置仍走旧 POI 路径，与标签同时设置时按标签发布 —— 必须说清楚，不能静默丢位置。
        if (p.Douyin?.Anchor is { } anchor)
        {
            var typeLabel = DouyinAnchorTypes.Label(anchor.Type);
            if (string.IsNullOrWhiteSpace(anchor.Id) || !DouyinAnchorTypes.IsSupported(anchor.Type))
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：「{typeLabel}」标签参数不完整，发布时不会带上", key));
            }
            else if (DouyinAnchorService.BuildAnchorJson(anchor.Type, anchor.Id, anchor.Content) is null)
            {
                // 游戏 / 小程序：发布体 content 只能整段透传平台返回的挂载信息，缺了就组不出来
                var mountLabel = anchor.Type == DouyinAnchorTypes.Applet ? "小程序" : "游戏";
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：{mountLabel}标签缺少平台挂载信息，发布时不会带上（请重新选择）", key));
            }
            else if (p.Location is { IsUsable: true } pos)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：抖音「添加标签」与「位置」互斥，已按「{typeLabel} · {anchor.Title}」发布，"
                    + $"位置「{pos.Name}」不会下发", key));
            }
        }

        // 横版 4:3 封面（B1 抓包 2026-11-11）：文件被删 / 被移走时执行端会跳过
        // （非致命、不拦发布），提前点名，别让用户以为横版槽设上了。
        if (!string.IsNullOrWhiteSpace(p.Douyin?.CoverLandscapePath)
            && !File.Exists(p.Douyin.CoverLandscapePath))
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：抖音横版 4:3 封面文件不存在，发布时不会单独下发横版封面", key));
        }

        // @好友（2026-11-11 补抓 discover/search）：缺 uid / 昵称的条目发布时跳过
        // （DouyinMentionRules.Usable 同一口径），提前点名，别让用户以为带上去了。
        if (p.Douyin?.MentionUsers is { Count: > 0 } mentions)
        {
            var usable = DouyinMentionRules.Usable(mentions);
            if (usable.Count < mentions.Count)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：抖音 @好友里有 {mentions.Count - usable.Count} 条缺用户 ID / 昵称，"
                    + "发布时不会带上", key));
            }
        }
    }

    private static void ValidateKuaishou(
        PublishDraft draft, PlatformParams p, string name, string key, List<PreflightIssue> issues)
    {
        // 话题标签最多 3 个（2026-10-22 实测：4 个时 submit 返回
        // result=1000「话题标签数量超过上限：4」）。组装载荷时会裁掉超出的部分 ——
        // 这里提前说清楚，别让用户以为 5 个都发出去了。
        // 与组装载荷用同一份最终文案：**标题也并入 caption**（风险 #30），标题里写的
        // #标签 同样占平台名额；话题来源同 2.6.5 的合并口径（TopicRefs ∪ Topics）。
        var composed = PublishPayloadBuilder.KuaishouComposedCaption(draft, p);
        var count = PublishPayloadBuilder.KuaishouHashtagCount(composed);
        if (count > PlatformLimits.KuaishouMaxTopics)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：话题标签最多 {PlatformLimits.KuaishouMaxTopics} 个（当前 {count} 个），"
                + "超出部分不会发送", key));
        }

        // 500 字上限（发布页计数器实测 0/500）：标题 + 简介 + 话题合并后数这一份。
        // 3 个标签裁剪之后再量（裁掉的标签不算），超了才会被组装端截断。
        var trimmed = PublishPayloadBuilder.ComposeKuaishouCaption(composed, [], int.MaxValue);
        if (trimmed.Length > PlatformLimits.KuaishouCaptionChars)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：快手文案（标题 + 简介 + 话题）共 {trimmed.Length} 字，"
                + $"超过 {PlatformLimits.KuaishouCaptionChars} 字上限，超出部分不会发送", key));
        }

        // @好友（2026-11-15 submit 抓包定案）：caption 里是纯文本 token
        // @昵称(O<userId>)，要 userId + 昵称两件套。坏条目发布端会跳过（不拦发布），
        // 这里点名，免得用户以为 @ 挂上了。
        var mentions = p.Kuaishou?.MentionUsers ?? new List<KuaishouMentionRef>();
        var badMentions = mentions.Count(m => m.UserId <= 0 || string.IsNullOrWhiteSpace(m.UserName));
        if (badMentions > 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：{badMentions} 个 @好友 参数不完整（缺用户 ID / 昵称），发布时不会带上", key));
        }

        // 关联小程序（2026-11-15 真页面抓包）：wire 是完整的 associateTaskId 对象，
        // bindId 是关键标识（taskId 观测为 0 也算有效）。半截数据发布端会跳过，这里点名。
        if (p.Kuaishou?.AssociateTask is { } associate
            && string.IsNullOrWhiteSpace(associate.BindId))
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：关联小程序参数不完整（缺 bindId），发布时不会带上", key));
        }

        // 共创（2026-11-15 页面 bundle）：元素 {userId, userName, userHead, coPublishRole}，
        // 坏条目（缺 ID / 昵称）发布端会跳过，这里点名。
        var badCoPublish = (p.Kuaishou?.CoPublishUsers ?? [])
            .Count(u => u.UserId <= 0 || string.IsNullOrWhiteSpace(u.UserName));
        if (badCoPublish > 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：{badCoPublish} 个共创用户参数不完整（缺用户 ID / 昵称），发布时不会带上", key));
        }

        // 章节（B3 抓包 2026-11-11，bundle 页面规则）—— 只提示，不拦发布：
        // 页面侧校验是否会由 submit 同样执行尚未实测（抓包缺口≠平台不支持）。
        if (p.Chapters.Count > 0)
        {
            var chapters = p.Chapters.OrderBy(c => c.StartMs).ToList();

            if (chapters.Count > PlatformLimits.KuaishouMaxChapters)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：快手最多 {PlatformLimits.KuaishouMaxChapters} 个章节（当前 {chapters.Count} 个），"
                    + "超出部分可能不保存", key));
            }

            var overTitle = chapters.FirstOrDefault(
                c => (c.Title ?? "").Trim().Length > PlatformLimits.KuaishouChapterTitleChars);
            if (overTitle is not null)
            {
                var title = (overTitle.Title ?? "").Trim();
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：快手章节标题最多 {PlatformLimits.KuaishouChapterTitleChars} 字，"
                    + $"「{title}」有 {title.Length} 字，可能被截断", key));
            }

            var tooClose = false;
            for (var i = 1; i < chapters.Count; i++)
            {
                if ((chapters[i].StartMs - chapters[i - 1].StartMs) / 1000
                    <= PlatformLimits.KuaishouChapterMinGapS)
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：快手相邻章节间隔需大于 {PlatformLimits.KuaishouChapterMinGapS} 秒，"
                    + "过近的章节可能被拒", key));
            }

            if (chapters.Count == 1 && chapters[0].StartMs <= 0)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：快手不支持「一个章节包含全部视频时长」（从 0:00 开始且只有一章）", key));
            }

            if (draft.Meta is { DurationS: > 0 } meta
                && meta.DurationS <= PlatformLimits.KuaishouChapterMinDurationS)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：视频仅 {meta.DurationS:0.#} 秒，快手页面在 ≤"
                    + $"{PlatformLimits.KuaishouChapterMinDurationS:0} 秒时不显示章节入口，章节可能不生效", key));
            }
        }

        // 封面帧 vs 自定义封面图：上传封面图后 submit 的 coverTimeStamp 归 0
        // （页面默认组合 coverTimeStamp=0；见 KuaishouCoverService.ResolveCover）。
        // 说清楚，免得用户以为「选的那一帧」和「自己的图」都生效了。
        if (!string.IsNullOrWhiteSpace(draft.CoverPath) && File.Exists(draft.CoverPath)
            && draft.CoverFrameMs is > 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：已选自定义封面图，快手不会使用「封面帧」设置（coverTimeStamp=0）", key));
        }
    }

    private static void ValidateWechatVideo(
        PublishDraft draft, PlatformParams p, string name, string key, List<PreflightIssue> issues)
    {
        // 官方活动：提交体 objectDesc.event 是「活动 ID + 活动名 + 发起人」三件套
        // （2026-11-11 B2），后两项只存在于活动搜索接口的选中项里。
        // params.txt 的「官方活动」只给 ID → 组不齐，这次不挂 —— 说清楚，别让用户以为参加了。
        var activities = PublishPayloadBuilder.EffectiveActivities(p);
        var incomplete = activities?
            .Where(a => string.IsNullOrWhiteSpace(a.CreatorNickname)
                || string.IsNullOrWhiteSpace(a.Name))
            .Select(a => a.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (incomplete is { Count: > 0 })
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：活动缺活动名/发起人（{string.Join("、", incomplete)}），视频号这次不会挂活动 —— "
                + "到上方「官方活动」里重新选一次（活动搜索接口会带回这两项）", key));
        }
        else if (activities is { Count: > 1 })
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：视频号一次只能挂一个活动，实际只用第一个「{activities[0].Name}」", key));
        }

        // 标题 <6 字：**不再拦截**（2026-11-06）。视频号的标题其实是「短标题」，平台要 ≥6 字；
        // builder 现在会不下发 shortTitle、把标题并入正文首行 —— 发得出去，只是短标题缺省。
        var title = (draft.Title ?? "").Trim();
        if (title.Length is > 0 and < WechatMinTitleChars)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：标题不足 {WechatMinTitleChars} 字（当前 {title.Length} 字），"
                + "不会作为短标题下发（会自动并入正文首行）", key));
        }

        // 封面：新版管线会用视频首帧自动兜底（老版塞的是 1×1 绿图，见缺陷 L1 同批修正），
        // 所以「没选封面」不再是错误 —— 但读不到视频信息时确实有风险，那时才提示
        if (string.IsNullOrWhiteSpace(draft.CoverPath) && draft.Meta is null)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：未选封面，自动抽首帧需要先读到视频信息", key));
        }
    }

    private static void ValidateXhs(
        PublishDraft draft, PlatformParams p, string name, string key, List<PreflightIssue> issues)
    {
        var units = PlatformLimits.XhsTitleLength(draft.Title ?? "");
        if (units > PlatformLimits.XhsTitleUnits)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Error,
                $"{name}：标题 {units} 字位，超出上限 {PlatformLimits.XhsTitleUnits}", key));
        }

        // 正文上限 1000 字（2026-11-05 字段映射 §2.1）。话题会被合进正文 —— 按**最终正文**数，
        // 只数用户写的简介会漏掉「加完话题超限」的情况（2.6.6）。
        var caption = PublishPayloadBuilder.XhsCaption(draft, p);
        if (caption.Length > PlatformLimits.XhsDescChars)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Error,
                $"{name}：正文（含话题）共 {caption.Length} 字，超出上限 {PlatformLimits.XhsDescChars} 字", key));
        }

        // ≥10 秒才能用原创声明与视频标注 —— 老版只有软提示、不入队列也不入重试（缺陷 L12）
        var isOriginal = p.IsOriginal ?? draft.IsOriginal;
        if (draft.Meta is { DurationS: > 0 } meta && meta.DurationS < PlatformLimits.XhsMinDurationS)
        {
            if (isOriginal)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Error,
                    $"{name}：视频仅 {meta.DurationS:0.#} 秒，不足 {PlatformLimits.XhsMinDurationS:0} 秒不能用原创声明", key));
            }
            else if (draft.DeclarationKind == DeclarationKind.SelfShot)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：视频仅 {meta.DurationS:0.#} 秒，不足 {PlatformLimits.XhsMinDurationS:0} 秒，视频标注可能不生效", key));
            }
        }

        // 原创声明与「内容为转载」互斥（老版 HomeViewModel.cs:2295 / :2288 的联动规则）
        if (isOriginal && draft.DeclarationKind == DeclarationKind.Repost)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Error,
                $"{name}：开启原创声明时不能选「内容为转载」", key));
        }

        // 封面帧 vs 自定义封面图：上传封面图后 cover.frame 变成 {ts:0, is_upload:true}，
        // 帧设置不生效（与抖音/快手同口径，见 XhsPublishService.ResolveCover）。
        if (!string.IsNullOrWhiteSpace(draft.CoverPath) && File.Exists(draft.CoverPath)
            && draft.CoverFrameMs is > 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：已选自定义封面图，小红书不会使用「封面帧」设置", key));
        }

        // 互动投票（A4）：配了一半（缺问题 / 选项不足 2 个 / 超过 5 个）只提示、不拦发布 ——
        // 发布端用同一份 XhsPollRules.Analyze 决定「跳过投票」，不把可选组件变成发布阻塞。
        var poll = XhsPollRules.Analyze(p.Xhs?.PollQuestion, p.Xhs?.PollOptions);
        if (poll.State == XhsPollState.Invalid)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：添加投票未生效 —— {poll.Error}（发布时会跳过投票）", key));
        }

        // 引用笔记（A5）/ 关联活动（A6）：坏 ID 与快照不全只提示、不拦发布 ——
        // 发布端用同一份 XhsRelationBinds 规则跳过坏条目。
        if (p.Xhs?.ReferencedNote is { } note
            && !XhsRelationBinds.IsValidNoteId((note.Id ?? "").Trim()))
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：引用笔记 ID 格式不对（{(string.IsNullOrWhiteSpace(note.Id) ? "空" : note.Id)}），"
                + "发布时会跳过引用", key));
        }

        var badActivityIds = p.Activities
            .Select(a => (a.Id ?? "").Trim())
            .Where(id => !XhsRelationBinds.IsValidActivityId(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (badActivityIds.Count > 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：活动 ID 不是数字（{string.Join("、", badActivityIds)}），发布时会跳过这些活动", key));
        }

        var incompleteActivities = p.Activities
            .Where(a => XhsRelationBinds.IsValidActivityId((a.Id ?? "").Trim())
                && (string.IsNullOrWhiteSpace(a.Name)
                    || a.StartTime is null || a.EndTime is null))
            .Select(a => a.Id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (incompleteActivities.Count > 0)
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：活动快照缺名称或起止时间（{string.Join("、", incompleteActivities)}），"
                + "发布体只带已有字段", key));
        }

        // 选择群聊（2026-11-15）：快照缺群 ID / 群名只提示、不拦发布 —— 发布端跳过。
        if (p.Xhs?.GroupBind is { } group
            && (string.IsNullOrWhiteSpace(group.GroupId)
                || string.IsNullOrWhiteSpace(group.GroupName)))
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：群聊快照不完整（缺群 ID / 群名），发布时不会带上", key));
        }

        // PK 封面（2026-11-15）：开关开了但没有附加图 = 平台空列表（页面同款），只提示不拦。
        if (p.Xhs?.PkCoverEnabled == true)
        {
            var pkImages = p.Xhs.PkCoverImages ?? [];
            if (pkImages.Count == 0)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：PK 封面已开启但没有选附加图，平台不会生成 PK 封面", key));
            }
            else
            {
                var missingPk = pkImages.Where(x => !File.Exists(x)).ToList();
                if (missingPk.Count > 0)
                {
                    issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                        $"{name}：PK 封面有 {missingPk.Count} 张图片不存在，发布时会跳过", key));
                }

                if (!string.IsNullOrWhiteSpace(p.Xhs.PkCoverBizRelations))
                {
                    issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                        $"{name}：PK 封面结构化附加图会覆盖开发者选项里的原始 JSON", key));
                }
            }
        }

        // 添加路线（2026-11-15）：快照缺 travelId / 路线名只提示、不拦发布 —— 发布端跳过。
        if (p.Xhs?.Route is { } route
            && (string.IsNullOrWhiteSpace(route.TravelId)
                || string.IsNullOrWhiteSpace(route.RouteName)))
        {
            issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                $"{name}：路线快照不完整（缺 travelId / 路线名），发布时不会带上", key));
        }

        // 视频标记（2026-11-15）：缺 ID / 名称跳过；起止异常只提示，不拦发布。
        foreach (var marker in p.Xhs?.Markers ?? [])
        {
            if (string.IsNullOrWhiteSpace(marker.Id) || string.IsNullOrWhiteSpace(marker.Name))
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：视频标记快照不完整（缺 ID / 名称），发布时会跳过该条", key));
                continue;
            }

            if (marker.EndSeconds < marker.StartSeconds)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：视频标记「{marker.Name}」结束时间早于开始时间，发布时按开始时间处理", key));
            }

            if (draft.Meta is { DurationS: > 0 } metaMarker
                && marker.StartSeconds > metaMarker.DurationS)
            {
                issues.Add(new PreflightIssue(PreflightSeverity.Warning,
                    $"{name}：视频标记「{marker.Name}」起始 {marker.StartSeconds} 秒超过视频时长"
                    + $" {metaMarker.DurationS:0.#} 秒", key));
            }
        }
    }
}
