using CreatFlow.Platforms.Bilibili;
using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Douyin;
using CreatFlow.Platforms.Kuaishou;
using CreatFlow.Platforms.Topics;
using CreatFlow.Platforms.Toutiao;
using CreatFlow.Platforms.WechatVideo;
using CreatFlow.Platforms.Xiaohongshu;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 发布页 · 条目编辑器（视图二）—— <see cref="PublishViewModel"/> 的 partial。
///
/// 布局三块（设计文档 §2.2）：
///   左栏 = 全局区（内容 / 话题 / 位置 / 合集 / 内容声明，各自成卡）；
///   右栏 = 平台卡（默认折叠成一行摘要，点开才是长尾开关）；
///   底栏 = 恢复账号默认 / 保存。
///
/// **声明二级跟声明卡走，不塞进平台卡**：它由语义驱动而非平台驱动，一次只出现一套；
/// 放进平台卡会让用户在四个卡片间来回找，也会让人误以为字段丢了。
/// </summary>
public sealed partial class PublishViewModel
{
    // ═══════════════════════════════════════════════════════════════
    //  当前编辑对象
    // ═══════════════════════════════════════════════════════════════

    [ObservableProperty] private PublishDraft? _selectedDraft;

    /// <summary>预设模式下被编辑的账号 key（保存时写回 <c>_defaults</c>）</summary>
    private string? _presetTargetKey;

    partial void OnSelectedDraftChanged(PublishDraft? value)
    {
        BuildTargetRows();
        SyncDeclarationSelection();
        SyncVisibilitySelection();
        RefreshDeclarationSecondary();
        RefreshTopicRows();
        RefreshPoiRows();
        CollectionRows.Clear();   // 每账号的合集状态行属于上一条，换个条目就不作数了
        RefreshCollectionSelection();
        RefreshPrivacy();
        // 换条目：作品选择面板收起、列表作废 —— 上一条的列表对新条目不作数（防串号）
        ShowDouyinRepostPicker = false;
        DouyinRepostItems.Clear();
        _douyinRepostTitle = null;
        OnPropertyChanged(nameof(DouyinRepostItemId));
        OnPropertyChanged(nameof(DouyinRepostPickedText));
        OnPropertyChanged(nameof(HasDouyinRepostItem));
        OnPropertyChanged(nameof(IsOriginal));
        // 视频号拍摄地区：面板收起；地区树已加载过的，把新条目的已存编码回填到三个下拉
        IsWechatShootRegionPanelOpen = false;
        if (WechatShootCountries.Count > 0) RestoreWechatShootRegionSelection();
        NotifyWechatShootRegionChanged();
        RefreshCoverSize();
        NotifyEditorComputed();
    }

    /// <summary>
    /// 播报编辑器里那些**从草稿算出来的**字段：计数器、发布时间的几种视图、封面预览、
    /// 声明的二级分组开关。它们没有 setter，草稿变了自己不会通知。
    ///
    /// 为什么漏不得：编辑器面板是**常驻可视树**（四个视图靠 <c>IsVisible</c> 互斥，§2.1），
    /// 绑定只在窗口加载时求值一次 —— 不播报就等于「界面上永远停在初始值」。
    /// 2026-09-19 实测四处同时中招：标题/简介计数器恒为「0 字」、导入的封面图不显示、
    /// 导入的定时发布打开后仍显示「立即」、声明选了「转载」二级字段不出来。
    /// </summary>
    private void NotifyEditorComputed()
    {
        OnPropertyChanged(nameof(TitleCounterText));
        OnPropertyChanged(nameof(DescriptionCounterText));
        OnPropertyChanged(nameof(CoverPathForPreview));
        OnPropertyChanged(nameof(CoverFrameText));
        OnPropertyChanged(nameof(IsOriginal));
        OnPropertyChanged(nameof(IsImmediate));
        OnPropertyChanged(nameof(IsScheduled));
        OnPropertyChanged(nameof(ScheduledDate));
        OnPropertyChanged(nameof(ScheduledTime));
        OnPropertyChanged(nameof(ScheduleHint));
        OnPropertyChanged(nameof(NoTargetSelected));
        OnPropertyChanged(nameof(IsRepost));
        OnPropertyChanged(nameof(IsSelfShot));
        OnPropertyChanged(nameof(ShowDouyinRepostSecondary));
        OnPropertyChanged(nameof(ShowKuaishouRepostSecondary));
        OnPropertyChanged(nameof(ShowXhsSecondary));
        OnPropertyChanged(nameof(ShowWechatSecondary));
        OnPropertyChanged(nameof(ShowKuaishouTopicLimitHint));
        OnPropertyChanged(nameof(KuaishouTopicLimitText));
    }

    /// <summary>
    /// 快手话题标签上限提示（>3 个且选了快手时出现）。
    ///
    /// 快手平台侧硬限 3 个：2026-10-22 实测 4 个话题 submit 直接拒
    /// <c>result=1000，话题标签数量超过上限：4</c>。组装载荷时会自动裁到前 3 个，
    /// 这里提前把「只发前 3 个」说清楚，别让用户以为 5 个全发出去了。
    /// </summary>
    public bool ShowKuaishouTopicLimitHint =>
        SelectedDraft is { } d
        && KuaishouTopicCount(d) > PlatformLimits.KuaishouMaxTopics
        && d.EnabledTargets.Any(t => t.PlatformKey == PlatformNames.Kuaishou);

    public string KuaishouTopicLimitText =>
        $"快手话题标签最多 {PlatformLimits.KuaishouMaxTopics} 个，只发送前 {PlatformLimits.KuaishouMaxTopics} 个"
        + $"（当前 {KuaishouTopicCount(SelectedDraft)} 个）";

    /// <summary>
    /// 快手要发的话题个数 —— 全局话题 + 快手平台卡里勾的结构化话题，**按名字取并集**
    /// （2.6.3：平台卡也能加话题，只数 draft.Topics 会漏报超限）。
    /// </summary>
    private static int KuaishouTopicCount(PublishDraft? draft)
    {
        if (draft is null) return 0;
        var names = new HashSet<string>(draft.Topics, StringComparer.Ordinal);
        foreach (var t in draft.Targets.Where(t => t.PlatformKey == PlatformNames.Kuaishou))
            foreach (var topic in draft.Overrides.GetValueOrDefault(t.Key)?.TopicRefs ?? [])
                names.Add(topic.Name);
        return names.Count;
    }

    /// <summary>话题增删后播报上面那对计算属性（编辑器常驻可视树，不播报就停在旧值）</summary>
    private void NotifyKuaishouTopicLimit()
    {
        OnPropertyChanged(nameof(ShowKuaishouTopicLimitHint));
        OnPropertyChanged(nameof(KuaishouTopicLimitText));
    }

    // ═══════════════════════════════════════════════════════════════
    //  左栏「内容」卡的展示辅助
    // ═══════════════════════════════════════════════════════════════

    /// <summary>标题计数器 —— 小红书按**字位**算（与预检共用同一个函数，不各写各的）</summary>
    public string TitleCounterText
    {
        get
        {
            var title = SelectedDraft?.Title ?? "";
            var units = PlatformLimits.XhsTitleLength(title);
            // 只有选了小红书才需要盯着 20 字位；否则给纯字数免得让人以为超了
            return SelectedDraft?.EnabledTargets.Any(t => t.PlatformKey == PlatformNames.Xiaohongshu) == true
                ? $"{units} / {PlatformLimits.XhsTitleUnits} 字位"
                : $"{title.Length} 字";
        }
    }

    public string DescriptionCounterText => $"{SelectedDraft?.Description.Length ?? 0} 字";

    /// <summary>定时发布的日期 / 时间（拆开因为 Avalonia 的 DatePicker / TimePicker 各管一半）</summary>
    public DateTimeOffset? ScheduledDate
    {
        get => SelectedDraft?.ScheduledAt is { } t ? new DateTimeOffset(t) : null;
        set
        {
            if (SelectedDraft is null) return;
            var time = SelectedDraft.ScheduledAt ?? PublishPreflight.NextScheduleSlot();
            SelectedDraft.ScheduledAt = (value?.Date ?? DateTime.Today).Add(time.TimeOfDay);
            OnPropertyChanged();
            RunPreflight(SelectedDraft);
        }
    }

    public TimeSpan? ScheduledTime
    {
        get => SelectedDraft?.ScheduledAt?.TimeOfDay;
        set
        {
            if (SelectedDraft is null) return;
            var date = SelectedDraft.ScheduledAt?.Date ?? DateTime.Today;
            SelectedDraft.ScheduledAt = date.Add(value ?? TimeSpan.Zero);
            OnPropertyChanged();
            RunPreflight(SelectedDraft);
        }
    }

    public string ScheduleHint => SelectedDraft?.ScheduledAt is { } t && SelectedDraft.IsScheduled
        ? $"将于 {t:yyyy-MM-dd HH:mm} 发布"
        : "";

    /// <summary>「立即」单选圈的选中态（<c>IsScheduled</c> 的取反）。</summary>
    public bool IsImmediate => SelectedDraft?.IsScheduled != true;

    public bool IsScheduled => SelectedDraft?.IsScheduled == true;

    /// <summary>
    /// 切换发布方式。**必须走命令而不是双向绑 <c>!IsScheduled</c>** ——
    /// 绑定里的取反是只读的，ToggleButton 会写不回去，表现为「点了没反应」。
    /// </summary>
    [RelayCommand]
    private void SelectScheduleMode(bool scheduled)
    {
        var draft = SelectedDraft;
        if (draft is null) return;

        draft.IsScheduled = scheduled;
        // 默认给「下一个合法落点」（now+2h 向上取整到整点）——免得用户还要自己点两个选择器，
        // 也免得默认值一拨出来就违反 2 小时下限挂预检错误
        if (scheduled && draft.ScheduledAt is null)
            draft.ScheduledAt = PublishPreflight.NextScheduleSlot();

        NotifyEditorComputed();
        RunPreflight(draft);
    }

    // ═══ 封面（选图需要 Window 的 StorageProvider → 回调由 View 注入） ═══

    /// <summary>选封面回调（View 在挂载时注入；null = 视图还没准备好）</summary>
    public Func<Task<string?>>? CoverPicker { get; set; }

    [RelayCommand]
    private async Task PickCoverAsync()
    {
        if (SelectedDraft is null || CoverPicker is null) return;
        var path = await CoverPicker();
        if (string.IsNullOrWhiteSpace(path)) return;
        SelectedDraft.CoverPath = path;
        RefreshCoverSize();
        NotifyEditorComputed();
        RunPreflight(SelectedDraft);
    }

    [RelayCommand]
    private void ClearCover()
    {
        if (SelectedDraft is null) return;
        SelectedDraft.CoverPath = null;
        RefreshCoverSize();
        NotifyEditorComputed();
        RunPreflight(SelectedDraft);
    }

    /// <summary>封面预览源：给了就用给的，没给就退回抽出来的首帧</summary>
    public string? CoverPathForPreview =>
        SelectedDraft?.CoverPath ?? SelectedDraft?.Meta?.ThumbPath;

    // ── 抖音封面双槽（B1 抓包 2026-11-11）──
    // 竖版 3:4 用左栏主封面（poster + 裁剪宽高）；这里选横版 4:3，
    // 上传后落 cover.horizontal_custom_cover_image_uri。选图复用同一个 CoverPicker。

    [RelayCommand]
    private async Task PickDouyinLandscapeCoverAsync(TargetRowViewModel? row)
    {
        if (row is null || SelectedDraft is null || CoverPicker is null) return;
        var path = await CoverPicker();
        if (string.IsNullOrWhiteSpace(path)) return;
        row.ApplyDouyinLandscapeCover(path);
        RunPreflight(SelectedDraft);
    }

    [RelayCommand]
    private void ClearDouyinLandscapeCover(TargetRowViewModel? row)
    {
        if (row is null || SelectedDraft is null) return;
        row.ClearDouyinLandscapeCover();
        RunPreflight(SelectedDraft);
    }

    // ── B站字幕（B5 抓包 2026-11-15）──
    // 与封面不同：选的是本地 .srt，发布时才上传（upload/web/image → preSave）。
    // 文件选择器由 View 注入（.srt 过滤器），与 CoverPicker 同一套机制。

    /// <summary>选字幕文件回调（View 挂载时注入；null = 视图还没准备好）</summary>
    public Func<Task<string?>>? SubtitlePicker { get; set; }

    [RelayCommand]
    private async Task PickBilibiliSubtitleAsync(TargetRowViewModel? row)
    {
        if (row is null || SelectedDraft is null || SubtitlePicker is null || !row.IsBilibili) return;
        var path = await SubtitlePicker();
        if (string.IsNullOrWhiteSpace(path)) return;
        row.ApplyBilibiliSubtitleFile(path);
        RunPreflight(SelectedDraft);
    }

    [RelayCommand]
    private void ClearBilibiliSubtitle(TargetRowViewModel? row)
    {
        if (row is null || SelectedDraft is null) return;
        row.ClearBilibiliSubtitleFile();
        RunPreflight(SelectedDraft);
    }

    // ── 封面图实际像素（选图/换条目时读一次文件头；不在每次键击的 NotifyEditorComputed 里读盘）──
    private string _coverSizeText = "";

    /// <summary>「封面尺寸 1051×788」—— 抖音裁剪尺寸留空时按它自动下发；没选/读不出为空</summary>
    public string CoverSizeText => _coverSizeText;

    public bool HasCoverSize => _coverSizeText.Length > 0;

    private void RefreshCoverSize()
    {
        _coverSizeText = ImageDimensionProbe.TryReadFile(SelectedDraft?.CoverPath) is { } d
            ? $"封面尺寸 {d.Width}×{d.Height}"
            : "";
        OnPropertyChanged(nameof(CoverSizeText));
        OnPropertyChanged(nameof(HasCoverSize));
    }

    /// <summary>
    /// 封面帧（界面按**秒**填，模型存毫秒）—— 抖音 <c>cover.poster_delay</c>（wire 也是秒）、
    /// 快手 <c>coverTimeStamp</c>、小红书 <c>cover.frame.ts</c> 共用一份（1.4 的封面帧是内容级选择）。
    /// 2.5.7 起它还是**六平台自动抽帧的时间点**：没选封面图时各平台按它抽一帧当封面。
    /// 留空 = 0.5 秒（避开 0 秒黑场）；0 = 明确第 0 秒。
    /// </summary>
    public string CoverFrameText
    {
        get => PublishFieldText.FormatMsAsSeconds(SelectedDraft?.CoverFrameMs);
        set
        {
            if (SelectedDraft is null) return;
            var ms = PublishFieldText.ParseSecondsToMs(value);
            if (SelectedDraft.CoverFrameMs == ms) return;
            SelectedDraft.CoverFrameMs = ms;
            OnPropertyChanged();
        }
    }

    /// <summary>打开某条草稿进编辑器</summary>
    [RelayCommand]
    private void EditDraft(PublishDraft? draft)
    {
        if (draft is null) return;
        IsPresetMode = false;
        _presetTargetKey = null;
        SelectedDraft = draft;
        EditorTitle = draft.Title;
        ViewMode = PublishViewMode.Editor;
        WarmXhsSigner(draft);   // 后台把小红书签名宿主热起来，别等用户点搜索才开始
    }

    /// <summary>
    /// 打开某账号的发布预设进编辑器 —— **复用同一套表单**（设计文档 §2.3）。
    /// 做法：造一条只有该账号一个目标、参数取自预设的临时草稿，保存时写回预设。
    /// 这样「预设编辑器」不需要第二套 UI，也不会与条目编辑器的行为漂移。
    /// </summary>
    [RelayCommand]
    private void EditPreset(PresetRowViewModel? row)
    {
        if (row is null) return;

        var phantom = new PublishDraft("")
        {
            Title = "",
            Description = "",
        };
        phantom.Targets.Add(new PublishTarget(row.PlatformKey, row.SessionId, row.AccountLabel));
        var key = PublishDraftStore.KeyOf(row.PlatformKey, row.SessionId);
        if (_defaults.TryGetValue(key, out var preset))
        {
            phantom.Overrides[phantom.Targets[0].Key] = preset.Clone();
            // 预设编辑器里全局原创显示预设值（保存时再写回账号预设）
            phantom.IsOriginal = preset.IsOriginal ?? false;
        }

        IsPresetMode = true;
        _presetTargetKey = key;
        SelectedDraft = phantom;
        EditorTitle = $"{row.PlatformName} · @{row.AccountLabel} 的发布预设";
        ViewMode = PublishViewMode.Editor;
    }

    /// <summary>
    /// 保存当前编辑态：预设模式写回账号预设，否则写回草稿（草稿是引用类型，本来就是实时改的，
    /// 这里只负责持久化与重跑预检）。
    /// </summary>
    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        var draft = SelectedDraft;
        if (draft is null) return;

        if (IsPresetMode && _presetTargetKey is { } key)
        {
            var target = draft.Targets[0];
            var preset = draft.ParamsFor(target);
            // 全局原创写回账号预设（显式 true/false）—— 下次导入时按覆盖位继续生效
            preset.IsOriginal = draft.IsOriginal;
            _defaults[key] = preset;
            await _store.SaveDefaultsAsync(_defaults);
            SubmitMessage = "预设已保存 —— 之后导入的条目会自动套用";
            BuildPresetRows();
        }
        else
        {
            RunPreflight(draft);
            await PersistDraftsAsync();
            SubmitMessage = draft.IsSubmittable ? "已保存" : "已保存（仍有待处理问题）";
        }

        RefreshSummaryCounts();
        BackToList();
    }

    /// <summary>把本条的所有目标退回账号预设</summary>
    [RelayCommand]
    private void ResetToAccountDefaults()
    {
        var draft = SelectedDraft;
        if (draft is null) return;

        foreach (var t in draft.Targets)
        {
            var key = PublishDraftStore.KeyOf(t.PlatformKey, t.SessionId);
            var preset = _defaults.GetValueOrDefault(key);
            if (preset is null) draft.Overrides.Remove(t.Key);
            else draft.Overrides[t.Key] = preset.Clone();
        }

        foreach (var row in TargetRows) row.ResetToPreset();
        // 活动合并在全局区（2.6.9）：恢复预设后这一行也要跟着刷新，否则芯片停在旧值
        foreach (var row in ActivityRows) row.RefreshActivityChips();
        SyncVisibilitySelection();
        RefreshCollectionSelection();
        RefreshDeclarationSecondary();
        SubmitMessage = "已恢复账号默认";
    }

    // ═══════════════════════════════════════════════════════════════
    //  右栏：平台卡
    // ═══════════════════════════════════════════════════════════════

    public ObservableCollection<TargetRowViewModel> TargetRows { get; } = [];

    /// <summary>
    /// 全局话题卡「平台推荐」区（2026-11-09 恢复）：每账号一行，只列有推荐能力的平台。
    ///
    /// 旧入口随右侧平台卡话题块一起被删（2026-11-06），`RecommendTopicSuggestionsCommand`
    /// 因此没有 UI 可达路径 —— 小红书 `recommend/suggest/topics`、B站 `topic/type/v2`
    /// 这两个「按标题推荐」的接口等于没接。这里接回公共话题卡：
    /// 推荐按标题/简介走平台接口，选中即写进该账号的结构化话题（<c>Params.TopicRefs</c>，带平台 ID），
    /// 发布时下发给平台（小红书 hash_tag / B站 topic_id …）。
    /// 快手/头条的推荐要等视频上传完成后的上下文（editSessionId / Vid），行内给说明不误导。
    /// </summary>
    public ObservableCollection<TargetRowViewModel> RecommendRows { get; } = [];

    /// <summary>推荐区是否出现（没有可推荐平台的草稿整块不渲染）</summary>
    public bool HasPlatformRecommendRows => RecommendRows.Count > 0;

    /// <summary>
    /// 左栏全局「官方活动」区（2.6.9）：**每平台一行**（抖音/快手/小红书/B站/视频号），
    /// 与话题一样合并到公共区；头条没有活动接口，不出现行。
    /// </summary>
    public ObservableCollection<PlatformActivityRowViewModel> ActivityRows { get; } = [];
    public bool HasActivityRows => ActivityRows.Count > 0;

    [ObservableProperty] private int _targetOnCount;

    private void BuildTargetRows()
    {
        TargetRows.Clear();
        var draft = SelectedDraft;
        if (draft is null) return;

        foreach (var t in draft.Targets)
        {
            var key = PublishDraftStore.KeyOf(t.PlatformKey, t.SessionId);
            var preset = _defaults.GetValueOrDefault(key);
            var p = draft.ParamsFor(t, preset);

            var row = new TargetRowViewModel
            {
                Target = t,
                Params = p,
                Preset = preset,
                PlatformName = PlatformNames.GetDisplayName(t.PlatformKey),
                AccountLabel = t.AccountLabel,
                BrandHex = ChannelBrandHex(t.PlatformKey),
                IsOn = draft.IsTargetEnabled(t),
                GlobalVisibility = draft.Visibility,
                GlobalIsOriginal = draft.IsOriginal,
                // 头条「跟随全局」预览（A1）
                GlobalDeclarationKind = draft.DeclarationKind,
                // 话题能力（2.6.3）：六平台统一目录，视频号也会返回一个「无接口」的能力对象
                TopicCapability = TopicServiceCatalog.CapabilityOf(t.PlatformKey),
            };
            // 头条作品声明（A1）：单选态从 Params.Toutiao 种子化（无覆盖 = 选中「跟随全局」）
            row.SyncToutiaoDeclarationSelection();
            // 头条视频生成图文（A2）：单选态从 Params.Toutiao 种子化（未选 = 不生成）
            row.SyncToutiaoTransArticleSelection();
            row.RefreshTopicChips();
            // 已选活动（2.6.4）：草稿/预设里已有的活动（params.txt 导入、旧档迁移）装配时就要显示
            row.RefreshActivityChips();
            // 已选 @用户（2026-11-10）：存档里可能已有 common.ats（旧档 / 预设），装配时种子化
            row.RefreshXhsMentions();
            // 已选 @好友（2026-11-11 抖音）：结构化列表或旧 mentions JSON 都要在装配时显示
            row.RefreshDouyinMentions();
            // 已选 @好友（2026-11-14 视频号）：存档 / 预设里的 mentionedUser 装配即显示
            row.RefreshWechatMentions();
            // 已选 @好友（快手，B3 抓包 2026-11-11）：存档 / 预设里的 MentionUsers 装配即显示
            row.RefreshKsMentions();
            // 已选共创（2026-11-15 页面 bundle）：存档 / 预设里的 CoPublishUsers 装配即显示
            row.RefreshKsCoPublishUsers();
            // 投票输入（A4）：存档里已有问题/选项时装配即显示
            row.RefreshXhsPoll();
            row.RefreshSummary();
            row.VisibilityNote = VisibilityMapping.FallbackNote(t.PlatformKey, draft.Visibility);
            TargetRows.Add(row);
        }

        BuildRecommendRows();
        BuildActivityRows();
        RefreshTargetOnCount();
    }

    /// <summary>
    /// 重建全局话题卡的「平台推荐」行（2026-11-09）：只收有推荐能力的平台。
    /// 与活动区不同：推荐结果写进**每个账号自己的** TopicRefs（平台话题 ID 按账号
    /// 各自持有），所以这里保持逐账号一行，不按平台合并。
    /// </summary>
    private void BuildRecommendRows()
    {
        RecommendRows.Clear();
        foreach (var row in TargetRows)
            if (row.TopicSupportsRecommend) RecommendRows.Add(row);
        OnPropertyChanged(nameof(HasPlatformRecommendRows));
    }

    /// <summary>
    /// 重建左栏全局「官方活动」行（2.6.9）：按平台分组，只列有活动接口的平台。
    /// 活动值仍逐账号存（<see cref="PlatformParams.Activities"/>），行只是合并展示。
    /// </summary>
    private void BuildActivityRows()
    {
        ActivityRows.Clear();
        foreach (var group in TargetRows.GroupBy(r => r.Target.PlatformKey))
        {
            if (!TopicServiceCatalog.CapabilityOf(group.Key).SupportsActivities) continue;
            var row = new PlatformActivityRowViewModel
            {
                PlatformKey = group.Key,
                PlatformName = PlatformNames.GetDisplayName(group.Key),
                BrandHex = ChannelBrandHex(group.Key),
            };
            row.Targets.AddRange(group);
            row.RefreshActivityChips();
            ActivityRows.Add(row);
        }
        OnPropertyChanged(nameof(HasActivityRows));
    }

    /// <summary>平台卡上的勾 → 草稿的禁用集；全不勾时给出提示而不是静默</summary>
    [RelayCommand]
    private void ToggleTarget(TargetRowViewModel? row)
    {
        var draft = SelectedDraft;
        if (draft is null || row is null) return;

        if (row.IsOn) draft.DisabledTargets.Remove(row.Target.Key);
        else draft.DisabledTargets.Add(row.Target.Key);

        RefreshTargetOnCount();
        // 目标变了，算出来的东西也变了：标题计数器按平台换算法（小红书按字位）、
        // 声明的二级分组按「发哪些平台」显隐
        NotifyEditorComputed();
        RunPreflight(draft);
        RefreshSummaryCounts();
    }

    private void RefreshTargetOnCount()
    {
        var draft = SelectedDraft;
        TargetOnCount = draft?.EnabledTargets.Count() ?? 0;
        OnPropertyChanged(nameof(NoTargetSelected));
    }

    /// <summary>一个目标都没勾 —— 提交会直接失败，UI 要提前说</summary>
    public bool NoTargetSelected => SelectedDraft is not null && TargetOnCount == 0;

    // ═══════════════════════════════════════════════════════════════
    //  右栏：平台卡 · 话题块（2.6.3）
    //
    //  与左栏全局「话题」卡的分工：
    //    · 全局卡 = 一次输入、各平台并行解析（旧路径：纯名字，解析器给每个平台找 ID）；
    //    · 平台卡 = 该平台**结构化话题**：搜索 / 「获取平台推荐」/ 手动输入，
    //      带 cid / topic_id / mission_id（B站），视频号无接口只能手动。
    //
    //  存两处、各有用途：
    //    · PlatformParams.TopicRefs = 结构化单一来源（2.6.5 起发布体读它）；
    //    · PlatformParams.Topics（旧 ResolvedTopic）= 桥接，让 2.6.5 之前的 builder
    //      也能立刻把话题发出去（否则用户加完话题、发布时静默不带 —— 最难查的一类）。
    //  **不动 draft.Topics**：全局列表是跨平台纯文本，把平台专属 ID 灌进去会发错平台。
    // ═══════════════════════════════════════════════════════════════

    /// <summary>展开 / 收起「添加话题」面板</summary>
    [RelayCommand]
    private void ToggleTopicPanel(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsTopicPanelOpen = !row.IsTopicPanelOpen;
        if (row.IsTopicPanelOpen) row.RefreshTopicPickStates();
    }

    /// <summary>手动输入一个话题（回车 / 点「添加」）—— 没有平台 ID，发布时按平台降级纯文本</summary>
    [RelayCommand]
    private void AddManualTopic(TargetRowViewModel? row)
    {
        if (row is null || SelectedDraft is null) return;

        var name = TopicSelection.NormalizeName(row.TopicManualInput);
        if (name.Length == 0)
        {
            row.TopicStatusText = "先输入话题名";
            row.TopicStatusIsError = false;
            return;
        }
        if (row.Params.TopicRefs.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal)))
        {
            row.TopicManualInput = "";
            row.TopicStatusText = $"「{name}」已在话题列表里";
            row.TopicStatusIsError = false;
            return;
        }

        ApplyTopicToRow(row, TopicSelection.Manual(name));
        row.TopicManualInput = "";
        row.TopicStatusText = row.TopicHasNoApi
            ? $"已添加「{name}」（纯文本话题）"
            : $"已添加「{name}」";
        row.TopicStatusIsError = false;
    }

    /// <summary>移除一个话题芯片</summary>
    [RelayCommand]
    private void RemoveTopicChip(TopicChipViewModel? chip)
    {
        if (chip is null || SelectedDraft is not { } draft) return;
        var row = TargetRows.FirstOrDefault(r => r.TopicChips.Contains(chip));
        if (row is null) return;

        var name = chip.Topic.Name;
        row.Params.TopicRefs.RemoveAll(t => t.Name == name);
        row.Params.Topics.RemoveAll(t => t.Name == name);
        row.RefreshTopicChips();
        row.RefreshSummary();
        row.TopicStatusText = $"已移除「{name}」";
        row.TopicStatusIsError = false;
        RunPreflight(draft);
        NotifyKuaishouTopicLimit();
    }

    // ═══ 抖音热点词选择器（2026-11-10：接平台 hotspot/search 接口）═══
    // 「热点词」不是自由文本 —— 平台只认它自己的热词（common.hot_sentence），
    // 所以做成「搜索 / 热门 → 点选」；不选 = 不参与热点（hashtag_source 走默认推荐）。

    /// <summary>展开 / 收起热点选择器；首次展开自动拉推荐热点（点开即有内容）。</summary>
    [RelayCommand]
    private async Task ToggleHotspotPanelAsync(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsHotspotPanelOpen = !row.IsHotspotPanelOpen;
        if (row.IsHotspotPanelOpen && !row.HasHotspotCandidates && !row.IsHotspotBusy)
            await FetchHotspotsAsync(row, trending: true);
    }

    /// <summary>按关键词搜索热点（空关键词不请求，提示改用「热门」）。</summary>
    [RelayCommand]
    private async Task SearchHotspotsAsync(TargetRowViewModel? row)
    {
        if (row is null) return;
        if (string.IsNullOrWhiteSpace(row.HotspotQuery))
        {
            row.HotspotStatusText = "先输入关键词，或点「热门」直接看推荐";
            row.HotspotStatusIsError = false;
            return;
        }
        await FetchHotspotsAsync(row, trending: false);
    }

    /// <summary>拉推荐热点（不传关键词）。</summary>
    [RelayCommand]
    private async Task FetchTrendingHotspotsAsync(TargetRowViewModel? row) =>
        await FetchHotspotsAsync(row, trending: true);

    /// <summary>选中一个热点词 → 写 <c>common.hot_sentence</c> 并收起面板。</summary>
    [RelayCommand]
    private void PickHotspot(HotspotPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.HotspotCandidates.Contains(pick));
        if (row is null) return;

        row.HotspotSentence = pick.Word;
        row.IsHotspotPanelOpen = false;
        row.HotspotStatusText = $"已选热点「{pick.Word}」";
        row.HotspotStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清除已选热点（回到不参与热点）。</summary>
    [RelayCommand]
    private void ClearHotspot(TargetRowViewModel? row)
    {
        if (row is null || !row.HasHotspot) return;
        row.HotspotSentence = null;
        row.HotspotStatusText = "已清除热点 —— 本条不参与热点";
        row.HotspotStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>
    /// 拉一批热点候选（搜索 / 推荐）。**永不抛** —— 失败写状态行，不打断编辑
    /// （热点是可选曝光项，接口挂了不该拦发布）。
    /// </summary>
    private async Task FetchHotspotsAsync(TargetRowViewModel? row, bool trending)
    {
        if (row is null || row.IsHotspotBusy || !row.IsDouyin) return;

        var keyword = row.HotspotQuery.Trim();

        row.IsHotspotBusy = true;
        row.HotspotStatusText = trending ? "正在获取热门热点…" : "正在搜索热点…";
        row.HotspotStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.HotspotStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试（不选热点也不影响发布）";
                row.HotspotStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };
            var service = new DouyinHotspotService(options);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            // 「热门」走 recommend（search 不传关键词平台回空列表，2026-11-10 实测）
            var result = trending
                ? await service.QueryTrendingSignedAsync(creds.Cookie, count: 30, ct: cts.Token)
                : await service.QuerySignedAsync(creds.Cookie, keyword, count: 30, ct: cts.Token);

            row.SetHotspotCandidates(result.Items);
            row.HotspotStatusText = result.Items.Count > 0
                ? trending ? $"热门热点 {result.Items.Count} 个" : $"找到 {result.Items.Count} 个热点"
                : trending ? "平台这次没有返回热门热点 —— 可换个关键词搜" : "没有搜到热点 —— 换个关键词再试";
            row.HotspotStatusIsError = false;
        }
        catch (OperationCanceledException)
        {
            row.HotspotStatusText = "请求超时 —— 可点按钮重试（不选热点也不影响发布）";
            row.HotspotStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.HotspotStatusText = "拉取失败：" + ex.Message + " —— 稍后重试（不选热点也不影响发布）";
            row.HotspotStatusIsError = true;
            Log.Warning(ex, "[Publish] 抖音热点拉取失败");
        }
        finally
        {
            row.IsHotspotBusy = false;
        }
    }

    // ═══ 抖音背景音乐选择器（2026-11-10：接平台 music/list + tsearch music/search）═══
    // 与热点词同一套交互：展开面板 → 搜索 / 推荐 → 点选；选中后只写 common.music_id。

    /// <summary>展开 / 收起音乐选择器；首次展开自动拉「推荐」（分类面板第一页）。</summary>
    [RelayCommand]
    private async Task ToggleMusicPanelAsync(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsMusicPanelOpen = !row.IsMusicPanelOpen;
        if (row.IsMusicPanelOpen && !row.HasMusicCandidates && !row.IsMusicBusy)
            await FetchMusicAsync(row, useSearch: false);
    }

    /// <summary>按关键词搜索音乐（空关键词不请求，提示改用「推荐」）。</summary>
    [RelayCommand]
    private async Task SearchMusicAsync(TargetRowViewModel? row)
    {
        if (row is null) return;
        if (string.IsNullOrWhiteSpace(row.MusicQuery))
        {
            row.MusicStatusText = "先输入歌名 / 歌手，或点「推荐」直接看列表";
            row.MusicStatusIsError = false;
            return;
        }
        await FetchMusicAsync(row, useSearch: true);
    }

    /// <summary>
    /// 拉推荐音乐：刷新**当前分类页签**（没选过任何页签时落到平台下发的第一个分类，
    /// 与发布页默认页签一致）。
    /// </summary>
    [RelayCommand]
    private async Task FetchRecommendedMusicAsync(TargetRowViewModel? row) =>
        await FetchMusicAsync(row, useSearch: false);

    /// <summary>
    /// 候选列表底部的「加载更多」—— 用上一页响应的 cursor 追加下一页。
    /// 来源（分类 / 关键词搜索）沿用上一次列表请求，不读当前输入框
    /// （用户可能已经在改关键词，但列表里还是旧关键词的结果）。
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreMusicAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsMusicBusy || !row.MusicHasMore) return;
        await FetchMusicAsync(row, useSearch: row.MusicSourceIsSearch, loadMore: true);
    }

    /// <summary>切换音乐分类页签 → 重拉该分类第一页（分类列表已拉过就不重复拉）。</summary>
    [RelayCommand]
    private async Task PickMusicCategoryAsync(MusicCategoryPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.MusicCategories.Contains(pick));
        if (row is null || row.IsMusicBusy) return;
        await FetchMusicAsync(row, useSearch: false, category: pick.Category);
    }

    /// <summary>选中一首歌 → 写 <c>common.music_id</c> 并收起面板。</summary>
    [RelayCommand]
    private void PickMusic(MusicPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.MusicCandidates.Contains(pick));
        if (row is null) return;

        row.ApplyPickedMusic(pick.Item);
        row.IsMusicPanelOpen = false;
        row.MusicStatusText = $"已选「{pick.Title}」";
        row.MusicStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清除已选音乐（回到不配乐）。</summary>
    [RelayCommand]
    private void ClearMusic(TargetRowViewModel? row)
    {
        if (row is null || !row.HasMusic) return;
        row.ClearMusic();
        row.MusicStatusText = "已清除背景音乐 —— 本条不配乐";
        row.MusicStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>
    /// 拉一批音乐候选（搜索 / 推荐 / 切换分类）。**永不抛** —— 失败写状态行，不打断编辑
    /// （配乐是可选项，接口挂了不该拦发布）。超时 30s：首次推荐要连打「分类 + 列表」两枪。
    /// </summary>
    /// <param name="category">
    /// 指定分类页签（切换页签时传）；null = 用当前已选页签，没选过则用平台下发的第一个分类。
    /// 关键词搜索忽略该参数。
    /// </param>
    private async Task FetchMusicAsync(
        TargetRowViewModel? row, bool useSearch, MusicCategory? category = null,
        bool loadMore = false)
    {
        if (row is null || row.IsMusicBusy || !row.IsDouyin) return;
        if (loadMore && !row.MusicHasMore) return;

        // 首页用输入框；加载更多沿用**上一页的来源**（关键词 / 分类），不受期间输入改动影响
        var keyword = loadMore ? row.MusicSourceKeyword : row.MusicQuery.Trim();

        row.IsMusicBusy = true;
        row.MusicStatusText = loadMore
            ? "正在加载更多音乐…"
            : useSearch
                ? "正在搜索音乐…"
                : category is null ? "正在获取推荐音乐…" : $"正在切换分类「{category.Name}」…";
        row.MusicStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.MusicStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试（不选音乐也不影响发布）";
                row.MusicStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };
            var service = new DouyinMusicService(options);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            MusicListPage page;
            string? categoryName = null;
            MusicCategory? chosenCategory = null;
            if (useSearch)
            {
                page = await service.SearchAsync(
                    creds.Cookie, keyword, loadMore ? row.MusicNextCursor : 0, ct: cts.Token);
                // 搜索结果不属于任何分类 → 页签取消高亮（下次「推荐」回落到第一个分类）
                if (!loadMore) row.ClearMusicCategorySelection();
            }
            else
            {
                // 页签拉过一次就复用（切换分类不再重复拉分类接口）；首次展开才拉分类，
                // 默认停在平台下发的第一个分类（与发布页默认页签一致）。
                if (row.MusicCategories.Count == 0)
                {
                    var categories = await service.GetCategoriesAsync(creds.Cookie, cts.Token);
                    if (categories.Count == 0)
                    {
                        row.SetMusicCandidates([]);
                        row.MusicStatusText = "平台没有返回音乐分类 —— 可输入歌名搜索";
                        row.MusicStatusIsError = true;
                        return;
                    }
                    row.SetMusicCategories(categories);
                }

                // 加载更多按**上一页的来源分类**重放（页签已在上一次拉列表时记在行上）
                chosenCategory = loadMore
                    ? row.MusicSourceCategory
                        ?? row.MusicCategories.FirstOrDefault(c => c.IsSelected)?.Category
                        ?? row.MusicCategories[0].Category
                    : category
                        ?? row.MusicCategories.FirstOrDefault(c => c.IsSelected)?.Category
                        ?? row.MusicCategories[0].Category;
                categoryName = chosenCategory.Name;
                page = await service.ListAsync(
                    creds.Cookie, chosenCategory, loadMore ? row.MusicNextCursor : 0,
                    ct: cts.Token);
                if (!loadMore) row.MarkMusicCategory(chosenCategory);
            }

            if (loadMore)
            {
                var added = row.AppendMusicCandidates(page.Items, page.Cursor, page.HasMore);
                if (added > 0)
                {
                    row.MusicStatusText = $"已加载 {row.MusicCandidates.Count} 首"
                        + (row.MusicHasMore ? "（还有更多）" : "（已全部加载）");
                    row.MusicStatusIsError = false;
                }
                else if (row.MusicHasMore)
                {
                    // 平台说还有下一页，但这一页没带新条目（游标原地打转）——
                    // 给个了断，别让「加载更多」变成无限点击；用户可重新搜索 / 点「推荐」
                    row.MusicHasMore = false;
                    row.MusicStatusText =
                        $"没有拿到新的歌曲（共 {row.MusicCandidates.Count} 首）—— 可重新搜索 / 点「推荐」";
                    row.MusicStatusIsError = true;
                }
                else
                {
                    row.MusicStatusText = $"没有更多音乐了（共 {row.MusicCandidates.Count} 首）";
                    row.MusicStatusIsError = false;
                }
            }
            else
            {
                row.SetMusicCandidates(
                    page.Items, page.Cursor, page.HasMore,
                    sourceIsSearch: useSearch,
                    sourceKeyword: useSearch ? keyword : "",
                    sourceCategory: useSearch ? null : chosenCategory);
                row.MusicStatusText = page.Items.Count > 0
                    ? (useSearch
                        ? $"找到 {page.Items.Count} 首"
                        : $"{categoryName} · {page.Items.Count} 首")
                      + (row.MusicHasMore ? "（还有更多）" : "")
                    : useSearch
                        ? "没有搜到音乐 —— 换个关键词再试"
                        : $"{categoryName} 暂无内容 —— 可输入歌名搜索";
                row.MusicStatusIsError = false;
            }
        }
        catch (OperationCanceledException)
        {
            row.MusicStatusText = loadMore
                ? "加载更多超时 —— 已加载的歌曲保留，可重新搜索 / 点「推荐」"
                : "请求超时 —— 可重试（不选音乐也不影响发布）";
            row.MusicStatusIsError = true;
            if (loadMore) row.MusicHasMore = false;
        }
        catch (Exception ex)
        {
            row.MusicStatusText = loadMore
                ? "加载更多失败：" + ex.Message + " —— 已加载的歌曲保留，可重新搜索 / 点「推荐」"
                : "拉取失败：" + ex.Message + " —— 稍后重试（不选音乐也不影响发布）";
            row.MusicStatusIsError = true;
            if (loadMore) row.MusicHasMore = false;
            Log.Warning(ex, "[Publish] 抖音音乐拉取失败");
        }
        finally
        {
            row.IsMusicBusy = false;
        }
    }

    // ═══ 抖音「添加标签」选择器（2026-11-11 抓包：影视演艺 / 游戏手柄 / 标记万物 / 小程序）═══
    // 页面同一个 anchor 是单选（位置也在其中）；本选择器做四类。
    // 小程序输入的是分享链接（anchor/search，anchor_type=4），UI 先按页面同款规则拦链接格式。
    // 选中前先过平台 anchor/check —— 平台说不行就不进发布体；
    // 能力位 anchor/list 只拉一次，拉不到就 fail-open（四类都可点，不拦功能）。

    /// <summary>展开 / 收起标签选择器；首次展开拉能力位（SPU 默认给一页推荐）。</summary>
    [RelayCommand]
    private async Task ToggleAnchorPanelAsync(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsAnchorPanelOpen = !row.IsAnchorPanelOpen;
        if (!row.IsAnchorPanelOpen) return;

        await EnsureAnchorCapabilitiesAsync(row);

        // 标记万物空关键词也返回推荐（页面首屏同款）；影视/游戏要用户先输入
        if (row.SelectedAnchorType == DouyinAnchorTypes.Spu
            && !row.HasAnchorCandidates && !row.IsAnchorBusy)
        {
            await FetchAnchorsAsync(row, DouyinAnchorTypes.Spu, "");
        }
    }

    /// <summary>
    /// 按关键词搜索当前类型的标签（影视 / 游戏 / 小程序空值先拦；SPU 允许空 = 推荐）。
    /// 小程序按页面同款规则校验链接（http/https、非 creator.douyin.com）。
    /// </summary>
    [RelayCommand]
    private async Task SearchAnchorsAsync(TargetRowViewModel? row)
    {
        if (row is null) return;
        var type = row.SelectedAnchorType;
        var keyword = row.AnchorQuery.Trim();

        if (type == DouyinAnchorTypes.Applet)
        {
            if (keyword.Length == 0)
            {
                row.AnchorStatusText = "先粘贴抖音小程序分享链接，再点「搜索」";
                row.AnchorStatusIsError = false;
                return;
            }
            if (!DouyinAnchorService.IsAppletLink(keyword))
            {
                row.AnchorStatusText = "链接格式不对 —— 粘贴抖音小程序分享链接（http/https，不能是创作中心地址）";
                row.AnchorStatusIsError = true;
                return;
            }
        }
        else if (type != DouyinAnchorTypes.Spu && keyword.Length == 0)
        {
            row.AnchorStatusText = type == DouyinAnchorTypes.Movie
                ? "先输入影视名称，再点「搜索」"
                : "先输入游戏名称，再点「搜索」";
            row.AnchorStatusIsError = false;
            return;
        }
        await FetchAnchorsAsync(row, type, keyword);
    }

    /// <summary>切换标签类型（清空上一类候选；SPU 切过去立即拉推荐）。</summary>
    [RelayCommand]
    private async Task PickAnchorTypeAsync(AnchorTypePickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.AnchorTypes.Contains(pick));
        if (row is null || row.IsAnchorBusy) return;

        if (!pick.IsEnabled)
        {
            row.AnchorStatusText = pick.Reason.Length > 0
                ? pick.Reason.Replace("{anchor_name}", pick.Name)
                : $"「{pick.Name}」本账号暂不可用";
            row.AnchorStatusIsError = true;
            return;
        }

        if (row.SelectedAnchorType == pick.Type && row.HasAnchorCandidates) return;
        row.SelectAnchorType(pick.Type);
        row.SetAnchorCandidates([]);
        row.AnchorStatusText = "";
        row.AnchorStatusIsError = false;

        if (pick.Type == DouyinAnchorTypes.Spu)
            await FetchAnchorsAsync(row, pick.Type, row.AnchorQuery.Trim());
    }

    /// <summary>选中一个标签：先 anchor/check，通过才写进抖音长尾并收起面板。</summary>
    [RelayCommand]
    private async Task PickAnchorAsync(AnchorPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.AnchorCandidates.Contains(pick));
        if (row is null || row.IsAnchorBusy) return;

        row.IsAnchorBusy = true;
        row.AnchorStatusText = "正在确认标签…";
        row.AnchorStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.AnchorStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试";
                row.AnchorStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };
            var service = new DouyinAnchorService(options);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var check = await service.CheckAsync(
                creds.Cookie, pick.Item.Type, pick.Item.Id, cts.Token);
            if (!check.Ok)
            {
                row.AnchorStatusText = "平台未通过该标签：" + (check.Reason ?? "未给出原因");
                row.AnchorStatusIsError = true;
                return;
            }

            row.ApplyPickedAnchor(pick.Item);
            row.IsAnchorPanelOpen = false;
            row.AnchorStatusText = $"已选{DouyinAnchorTypes.Label(pick.Item.Type)}「{pick.Item.Title}」";
            row.AnchorStatusIsError = false;
            if (SelectedDraft is { } draft) RunPreflight(draft);
        }
        catch (OperationCanceledException)
        {
            row.AnchorStatusText = "确认超时 —— 可重试";
            row.AnchorStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.AnchorStatusText = "确认失败：" + ex.Message + " —— 稍后重试";
            row.AnchorStatusIsError = true;
            Log.Warning(ex, "[Publish] 抖音标签确认失败");
        }
        finally
        {
            row.IsAnchorBusy = false;
        }
    }

    /// <summary>清除已选标签。</summary>
    [RelayCommand]
    private void ClearAnchor(TargetRowViewModel? row)
    {
        if (row is null || !row.HasAnchor) return;
        row.ClearAnchor();
        row.AnchorStatusText = "已清除标签";
        row.AnchorStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>能力位只拉一次（失败不置位）；拿不到就 fail-open —— 不因门控接口挂了禁用功能。</summary>
    private async Task EnsureAnchorCapabilitiesAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsAnchorBusy || !row.IsDouyin || row.AnchorCapabilitiesLoaded) return;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null) return;   // 静默：真正搜索时会给出凭证提示

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };
            var service = new DouyinAnchorService(options);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var caps = await service.GetCapabilitiesAsync(creds.Cookie, cts.Token);
            row.SetAnchorCapabilities(caps);
            row.AnchorCapabilitiesLoaded = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 抖音标签能力位拉取失败（按全部可用处理）");
        }
    }

    /// <summary>
    /// 拉一批标签候选。**永不抛** —— 失败写状态行，不打断编辑
    /// （标签是可选曝光项，接口挂了不该拦发布）。类型在请求期间被切换 → 丢弃这批结果。
    /// </summary>
    private async Task FetchAnchorsAsync(TargetRowViewModel? row, int type, string keyword)
    {
        if (row is null || row.IsAnchorBusy || !row.IsDouyin) return;

        row.IsAnchorBusy = true;
        row.AnchorStatusText = $"正在搜索{DouyinAnchorTypes.Label(type)}…";
        row.AnchorStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.AnchorStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试（不选标签也不影响发布）";
                row.AnchorStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };
            var service = new DouyinAnchorService(options);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var items = await service.SearchAsync(creds.Cookie, type, keyword, cts.Token);

            // 期间切了类型 → 这批结果属于上一类，别塞进当前页签
            if (row.SelectedAnchorType != type) return;

            row.SetAnchorCandidates(items);
            row.AnchorStatusText = items.Count > 0
                ? $"{DouyinAnchorTypes.Label(type)} · {items.Count} 个"
                    + (type == DouyinAnchorTypes.Spu ? "（可继续输入关键词筛选）" : "")
                : type == DouyinAnchorTypes.Applet
                    ? "没有搜到小程序 —— 检查链接是否有效，或在抖音 App 里重新复制分享链接"
                    : $"没有搜到{DouyinAnchorTypes.Label(type)} —— 换个关键词再试";
            row.AnchorStatusIsError = false;
        }
        catch (OperationCanceledException)
        {
            row.AnchorStatusText = "请求超时 —— 可重试（不选标签也不影响发布）";
            row.AnchorStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.AnchorStatusText = "拉取失败：" + ex.Message + " —— 稍后重试（不选标签也不影响发布）";
            row.AnchorStatusIsError = true;
            Log.Warning(ex, "[Publish] 抖音标签搜索失败");
        }
        finally
        {
            row.IsAnchorBusy = false;
        }
    }

    // ═══ 小红书速览合集选择器（2026-11-10：接平台 collection/pc/list?type=1）═══
    // 速览合集只认账号自己的列表 —— 手填 24 位 ID 既记不住也验证不了。
    // 选中后只写 noteSketchCollectionBind.id（与音乐选择器同一套交互）。

    /// <summary>展开 / 收起速览合集选择器；首次展开自动拉列表。</summary>
    [RelayCommand]
    private async Task ToggleSketchPanelAsync(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsSketchPanelOpen = !row.IsSketchPanelOpen;
        if (row.IsSketchPanelOpen && !row.HasSketchCandidates && !row.IsSketchBusy)
            await FetchSketchCollectionsAsync(row);
    }

    /// <summary>重新拉一次速览合集列表（账号侧新建合集后不用关面板重开）。</summary>
    [RelayCommand]
    private async Task RefreshSketchCollectionsAsync(TargetRowViewModel? row) =>
        await FetchSketchCollectionsAsync(row);

    /// <summary>
    /// 候选列表底部的「加载更多」—— 用上一页最后一条的 cursor 追加下一页。
    /// 速览合集超过一页的账号不多，但页面同款接口确实带游标（cursor 变成空串 = 到底了）。
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreSketchCollectionsAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsSketchBusy || !row.SketchHasMore) return;
        await FetchSketchCollectionsAsync(row, loadMore: true);
    }

    /// <summary>选中一个速览合集 → 写 <c>noteSketchCollectionBind.id</c> 并收起面板。</summary>
    [RelayCommand]
    private void PickSketch(SketchPickViewModel? pick)
    {
        if (pick is null || pick.IsFull) return;
        var row = TargetRows.FirstOrDefault(r => r.SketchCandidates.Contains(pick));
        if (row is null) return;

        row.ApplyPickedSketch(pick.Item);
        row.IsSketchPanelOpen = false;
        row.SketchStatusText = $"已选「{pick.Name}」";
        row.SketchStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清除已选速览合集（回到不绑定）。</summary>
    [RelayCommand]
    private void ClearSketch(TargetRowViewModel? row)
    {
        if (row is null || !row.HasSketch) return;
        row.ClearSketch();
        row.SketchStatusText = "已清除速览合集 —— 本条不绑定短剧合集";
        row.SketchStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>
    /// 拉该账号的速览合集列表。**永不抛** —— 失败写状态行，不打断编辑
    /// （速览合集是可选项，账号没有该能力/接口挂了都不该拦发布）。
    /// 预算 120s：小红书签名宿主冷启动实测可达 60s+（同 XhsSignerService 的预热上限）。
    /// <paramref name="loadMore"/> = 用上一页游标追加（不清空已加载候选）。
    /// </summary>
    private async Task FetchSketchCollectionsAsync(TargetRowViewModel? row, bool loadMore = false)
    {
        if (row is null || row.IsSketchBusy || !row.IsXhs) return;
        if (loadMore && !row.SketchHasMore) return;

        row.IsSketchBusy = true;
        row.SketchStatusText = loadMore
            ? "正在加载更多速览合集…"
            : "正在获取速览合集…（首次要等平台签名准备，可能要几十秒）";
        row.SketchStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.SketchStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试（不选速览合集也不影响发布）";
                row.SketchStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var signerReady = await XhsSignerService.PrepareAsync(options.SessionId, cts.Token);

            var page = await new XhsCollectionService(options)
                .ListSketchCollectionsAsync(
                    creds.Cookie, loadMore ? row.SketchNextCursor : null, ct: cts.Token);

            if (loadMore)
            {
                var added = row.AppendSketchCandidates(page.Items, page.NextCursor);
                if (added > 0)
                {
                    row.SketchStatusText = $"已加载 {row.SketchCandidates.Count} 个速览合集"
                        + (row.SketchHasMore ? "（还有更多）" : "（已全部加载）");
                    row.SketchStatusIsError = false;
                }
                else if (row.SketchHasMore)
                {
                    // 平台说还有下一页，但这一页没带新条目（游标原地打转）——
                    // 给个了断，别让「加载更多」变成无限点击；用户可「刷新」重来
                    row.SketchHasMore = false;
                    row.SketchStatusText =
                        $"没有拿到新的速览合集（共 {row.SketchCandidates.Count} 个）—— 可点「刷新」重来";
                    row.SketchStatusIsError = true;
                }
                else
                {
                    row.SketchStatusText = $"没有更多速览合集了（共 {row.SketchCandidates.Count} 个）";
                    row.SketchStatusIsError = false;
                }
            }
            else
            {
                row.SetSketchCandidates(page.Items, page.NextCursor);
                row.SketchStatusText = page.Items.Count > 0
                    ? $"共 {row.SketchCandidates.Count} 个速览合集"
                      + (row.SketchHasMore ? "（还有更多）" : "")
                    : signerReady
                        ? "该账号没有速览合集 —— 可留空（发布页同款下拉也是空的）"
                        : "平台没返回速览合集，且签名宿主未就绪 —— 稍后重试";
                row.SketchStatusIsError = page.Items.Count == 0 && !signerReady;
            }
        }
        catch (OperationCanceledException)
        {
            row.SketchStatusText = loadMore
                ? "加载更多超时 —— 已加载的候选保留，可点「刷新」重来"
                : "请求超时 —— 可重试（不选速览合集也不影响发布）";
            row.SketchStatusIsError = true;
            if (loadMore) row.SketchHasMore = false;
        }
        catch (Exception ex)
        {
            row.SketchStatusText = loadMore
                ? "加载更多失败：" + ex.Message + " —— 已加载的候选保留，可点「刷新」重来"
                : "拉取失败：" + ex.Message + " —— 稍后重试（不选速览合集也不影响发布）";
            row.SketchStatusIsError = true;
            if (loadMore) row.SketchHasMore = false;
            Log.Warning(ex, "[Publish] 小红书速览合集拉取失败");
        }
        finally
        {
            row.IsSketchBusy = false;
        }
    }

    // ═══ 小红书「选择群聊」选择器（2026-11-15：接平台 group_list）═══
    // 账号在 App 端创建的群聊才会出现在这里（页面同款：先去 App 消息-发现群聊创建）。
    // 选中写 business_binds.groupBind（bundle 逐字四字段）；失败只写状态行，不拦发布。

    /// <summary>展开 / 收起群聊选择器；首次展开自动拉列表。</summary>
    [RelayCommand]
    private async Task ToggleXhsGroupPanelAsync(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsXhsGroupPanelOpen = !row.IsXhsGroupPanelOpen;
        if (row.IsXhsGroupPanelOpen && !row.HasXhsGroupCandidates && !row.IsXhsGroupBusy)
            await FetchXhsGroupsAsync(row);
    }

    /// <summary>重新拉一次群聊列表（刚在 App 里建群后不用关面板重开）。</summary>
    [RelayCommand]
    private async Task RefreshXhsGroupsAsync(TargetRowViewModel? row) =>
        await FetchXhsGroupsAsync(row);

    /// <summary>选中一个群聊 → 写快照并收起面板。</summary>
    [RelayCommand]
    private void PickXhsGroup(XhsGroupPickViewModel? pick)
    {
        if (pick is null || !pick.IsLinkable) return;
        var row = TargetRows.FirstOrDefault(r => r.XhsGroupCandidates.Contains(pick));
        if (row is null) return;

        row.ApplyXhsGroup(pick.Item);
        row.IsXhsGroupPanelOpen = false;
        row.XhsGroupStatusText = $"已选「{pick.Name}」";
        row.XhsGroupStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清除已选群聊（回到不关联）。</summary>
    [RelayCommand]
    private void ClearXhsGroup(TargetRowViewModel? row)
    {
        if (row is null || !row.HasXhsGroup) return;
        row.ClearXhsGroup();
        row.XhsGroupStatusText = "已清除群聊 —— 本条不关联群聊";
        row.XhsGroupStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>
    /// 拉该账号可绑定的群聊。**永不抛** —— 失败写状态行，不打断编辑
    /// （群聊是可选项，账号没有群 / 接口挂了都不该拦发布）。
    /// 预算 120s：小红书签名宿主冷启动实测可达 60s+（同速览合集选择器）。
    /// </summary>
    private async Task FetchXhsGroupsAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsXhsGroupBusy || !row.IsXhs) return;

        row.IsXhsGroupBusy = true;
        row.XhsGroupStatusText = "正在获取群聊…（首次要等平台签名准备，可能要几十秒）";
        row.XhsGroupStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.XhsGroupStatusText =
                    "账号凭证不可用 —— 到「平台账号」重新登录后再试（不关联群聊也不影响发布）";
                row.XhsGroupStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var signerReady = await XhsSignerService.PrepareAsync(options.SessionId, cts.Token);

            var page = await new XhsGroupService(options)
                .ListAsync(creds.Cookie, noteId: "", ct: cts.Token);

            row.SetXhsGroupCandidates(page.Items);
            row.XhsGroupStatusText = page.Items.Count > 0
                ? $"共 {page.Items.Count} 个群聊"
                : signerReady
                    ? "该账号还没有可绑定的群聊 —— 先在 App 消息页创建群聊（不关联也不影响发布）"
                    : "平台没返回群聊，且签名宿主未就绪 —— 稍后重试";
            row.XhsGroupStatusIsError = page.Items.Count == 0 && !signerReady;
        }
        catch (OperationCanceledException)
        {
            row.XhsGroupStatusText = "请求超时 —— 可重试（不关联群聊也不影响发布）";
            row.XhsGroupStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.XhsGroupStatusText =
                "拉取失败：" + ex.Message + " —— 稍后重试（不关联群聊也不影响发布）";
            row.XhsGroupStatusIsError = true;
            Log.Warning(ex, "[Publish] 小红书群聊列表拉取失败");
        }
        finally
        {
            row.IsXhsGroupBusy = false;
        }
    }

    // ═══ 小红书 PK 封面（2026-11-15）═══
    // 附加图逐张选（复用封面选图回调），发布时逐张上传；
    // 主封面自动用发布封面/首帧当 index=0（wire 见 XhsPublishService.BuildPkCoverRelationsJson）。

    /// <summary>追加一张 PK 封面附加图。</summary>
    [RelayCommand]
    private async Task PickXhsPkCoverAsync(TargetRowViewModel? row)
    {
        if (row is null || SelectedDraft is null || CoverPicker is null || !row.IsXhs) return;
        var path = await CoverPicker();
        if (string.IsNullOrWhiteSpace(path)) return;
        row.AddXhsPkCover(path);
        RunPreflight(SelectedDraft);
    }

    /// <summary>移除一张 PK 封面附加图。</summary>
    [RelayCommand]
    private void RemoveXhsPkCover(XhsPkCoverItemViewModel? item)
    {
        if (item is null) return;
        var row = TargetRows.FirstOrDefault(r => r.XhsPkCovers.Contains(item));
        if (row is null) return;
        row.RemoveXhsPkCover(item);
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清空全部 PK 封面附加图（开关保留）。</summary>
    [RelayCommand]
    private void ClearXhsPkCovers(TargetRowViewModel? row)
    {
        if (row is null || !row.HasXhsPkCovers) return;
        row.ClearXhsPkCovers();
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    // ═══ 小红书视频标记（2026-11-15：标记地点 / 标记朋友）═══
    // 朋友走 search/label?type=user；地点复用 POI 搜索。选中写 video_info.markers。

    /// <summary>展开 / 收起标记选择器（不自动搜 —— 需要关键词）。</summary>
    [RelayCommand]
    private void ToggleXhsMarkerPanel(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsXhsMarkerPanelOpen = !row.IsXhsMarkerPanelOpen;
    }

    /// <summary>搜索可标记的朋友 / 地点。</summary>
    [RelayCommand]
    private async Task SearchXhsMarkersAsync(TargetRowViewModel? row) =>
        await FetchXhsMarkersAsync(row);

    /// <summary>选中一个可标记对象 → 加入已选（默认 0–5 秒）。</summary>
    [RelayCommand]
    private void PickXhsMarker(XhsMarkerPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.XhsMarkerCandidates.Contains(pick));
        if (row is null) return;

        row.AddXhsMarker(new XhsVideoMarker
        {
            Id = pick.Id,
            Name = pick.Name,
            Type = pick.Type,
            Avatar = pick.Avatar,
            StartSeconds = 0,
            EndSeconds = 5,
        });
        row.XhsMarkerStatusText = $"已标记「{pick.Name}」";
        row.XhsMarkerStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>移除一个已选标记。</summary>
    [RelayCommand]
    private void RemoveXhsMarker(XhsMarkerRowViewModel? item)
    {
        if (item is null) return;
        var row = TargetRows.FirstOrDefault(r => r.XhsMarkers.Contains(item));
        if (row is null) return;
        row.RemoveXhsMarker(item);
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清空全部标记。</summary>
    [RelayCommand]
    private void ClearXhsMarkers(TargetRowViewModel? row)
    {
        if (row is null || !row.HasXhsMarkers) return;
        row.ClearXhsMarkers();
        row.XhsMarkerStatusText = "已清空标记";
        row.XhsMarkerStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>
    /// 搜索可标记对象。**永不抛** —— 失败写状态行，不打断编辑
    /// （标记是可选项，接口挂了不拦发布）。预算 120s（小红书签名宿主冷启动）。
    /// </summary>
    private async Task FetchXhsMarkersAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsXhsMarkerBusy || !row.IsXhs) return;

        var keyword = (row.XhsMarkerQuery ?? "").Trim();
        if (keyword.Length == 0)
        {
            row.XhsMarkerStatusText = "先输入关键词再搜索（空关键词平台不返回结果）";
            row.XhsMarkerStatusIsError = true;
            return;
        }

        row.IsXhsMarkerBusy = true;
        row.XhsMarkerStatusText = "正在搜索…（首次要等平台签名准备，可能要几十秒）";
        row.XhsMarkerStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.XhsMarkerStatusText =
                    "账号凭证不可用 —— 到「平台账号」重新登录后再试（不加标记也不影响发布）";
                row.XhsMarkerStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await XhsSignerService.PrepareAsync(options.SessionId, cts.Token);

            var candidates = new List<XhsMarkerPickViewModel>();
            if (row.XhsMarkerTypeIndex == 1)
            {
                var pois = await new XhsPoiService(options)
                    .SearchAsync(creds.Cookie, keyword, ct: cts.Token);
                foreach (var poi in pois)
                {
                    if (string.IsNullOrWhiteSpace(poi.PoiId)
                        || string.IsNullOrWhiteSpace(poi.Name)) continue;
                    candidates.Add(new XhsMarkerPickViewModel
                    {
                        Id = poi.PoiId,
                        Name = poi.Name,
                        Type = "location",
                        Subtitle = poi.Subname,
                    });
                }
            }
            else
            {
                var users = await new XhsLabelService(options)
                    .SearchFriendsAsync(creds.Cookie, keyword, cts.Token);
                foreach (var user in users)
                {
                    candidates.Add(new XhsMarkerPickViewModel
                    {
                        Id = user.Id,
                        Name = user.Name,
                        Type = "user",
                        Subtitle = user.Desc,
                        Avatar = user.Avatar,
                    });
                }
            }

            row.SetXhsMarkerCandidates(candidates);
            row.XhsMarkerStatusText = candidates.Count > 0
                ? $"找到 {candidates.Count} 个可标记对象"
                : "没有找到相关结果 —— 换个关键词试试";
            row.XhsMarkerStatusIsError = candidates.Count == 0;
        }
        catch (OperationCanceledException)
        {
            row.XhsMarkerStatusText = "请求超时 —— 可重试（不加标记也不影响发布）";
            row.XhsMarkerStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.XhsMarkerStatusText =
                "搜索失败：" + ex.Message + " —— 稍后重试（不加标记也不影响发布）";
            row.XhsMarkerStatusIsError = true;
            Log.Warning(ex, "[Publish] 小红书视频标记搜索失败");
        }
        finally
        {
            row.IsXhsMarkerBusy = false;
        }
    }

    // ═══ 小红书添加路线（2026-11-15）═══
    // 流程：搜 POI → 点选换坐标 → 保存路线（save/travel，空 polygon）→ 快照 travelId。
    // 发布只带快照（hash_tag 的 map_traver + biz_relations 的 POI_MAP_TRAVEL）。
    // 失败只写状态行；路线是可选项，接口挂了不拦发布。

    /// <summary>展开 / 收起路线编辑器（已建路线时由 UI 隐藏编辑器）。</summary>
    [RelayCommand]
    private void ToggleXhsRoutePanel(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsXhsRoutePanelOpen = !row.IsXhsRoutePanelOpen;
    }

    /// <summary>搜索可加入路线的地点。</summary>
    [RelayCommand]
    private async Task SearchXhsRoutePoisAsync(TargetRowViewModel? row) =>
        await FetchXhsRoutePoisAsync(row);

    /// <summary>选中一个地点候选 → 换坐标 → 加入草稿。</summary>
    [RelayCommand]
    private async Task AddXhsRoutePoiAsync(XhsRoutePickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.XhsRouteCandidates.Contains(pick));
        if (row is null || row.IsXhsRouteBusy || !row.IsXhs) return;

        row.IsXhsRouteBusy = true;
        row.XhsRouteStatusText = "正在定位地点…";
        row.XhsRouteStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.XhsRouteStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试";
                row.XhsRouteStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await XhsSignerService.PrepareAsync(options.SessionId, cts.Token);

            var point = await new XhsRouteService(options).ResolvePointAsync(
                creds.Cookie, pick.Item.PoiId, pick.Item.Name, cts.Token);
            row.AddXhsRoutePoint(point);
            row.XhsRouteStatusText =
                $"已添加「{point.Name}」 · 共 {row.XhsRoutePoints.Count} 个地点（至少 2 个才能保存）";
        }
        catch (OperationCanceledException)
        {
            row.XhsRouteStatusText = "定位超时 —— 可重试（不加路线也不影响发布）";
            row.XhsRouteStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.XhsRouteStatusText = "定位失败：" + ex.Message + " —— 稍后重试";
            row.XhsRouteStatusIsError = true;
            Log.Warning(ex, "[Publish] 小红书路线地点定位失败");
        }
        finally
        {
            row.IsXhsRouteBusy = false;
        }
    }

    /// <summary>从草稿里移除一个地点。</summary>
    [RelayCommand]
    private void RemoveXhsRoutePoi(XhsRoutePointViewModel? item)
    {
        if (item is null) return;
        var row = TargetRows.FirstOrDefault(r => r.XhsRoutePoints.Contains(item));
        if (row is null) return;
        row.RemoveXhsRoutePoint(item);
    }

    /// <summary>保存路线（平台 save/travel）→ 写快照，发布体两处落点由快照驱动。</summary>
    [RelayCommand]
    private async Task SaveXhsRouteAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsXhsRouteBusy || !row.IsXhs) return;

        var points = row.XhsRoutePoints.Select(p => p.Point).ToList();
        if (points.Count < 2)
        {
            row.XhsRouteStatusText = "路线至少需要 2 个地点（页面同款校验）";
            row.XhsRouteStatusIsError = true;
            return;
        }

        var name = (row.XhsRouteName ?? "").Trim();
        if (name.Length == 0)
        {
            row.XhsRouteStatusText = "请填写路线名（页面同款必填）";
            row.XhsRouteStatusIsError = true;
            return;
        }

        row.IsXhsRouteBusy = true;
        row.XhsRouteStatusText = "正在保存路线…（首次要等平台签名准备，可能要几十秒）";
        row.XhsRouteStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.XhsRouteStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试";
                row.XhsRouteStatusIsError = true;
                return;
            }

            var userId = CredentialFields.CookieValue(
                creds.Cookie, "x-user-id-creator.xiaohongshu.com") ?? creds.PlatformUid;
            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await XhsSignerService.PrepareAsync(options.SessionId, cts.Token);

            var saved = await new XhsRouteService(options).SaveRouteAsync(
                creds.Cookie, userId ?? "", name, points, cts.Token);

            row.ApplyXhsRoute(new XhsRouteRef(
                saved.TravelId, saved.RouteName, saved.TravelType, saved.PlanAmount));
            row.IsXhsRoutePanelOpen = false;
            row.XhsRouteStatusText =
                $"路线已保存：「{saved.RouteName}」 · {points.Count} 个地点";
            row.XhsRouteStatusIsError = false;
            if (SelectedDraft is { } draft) RunPreflight(draft);
        }
        catch (OperationCanceledException)
        {
            row.XhsRouteStatusText = "保存超时 —— 可重试（不加路线也不影响发布）";
            row.XhsRouteStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.XhsRouteStatusText = "保存失败：" + ex.Message + " —— 稍后重试";
            row.XhsRouteStatusIsError = true;
            Log.Warning(ex, "[Publish] 小红书路线保存失败");
        }
        finally
        {
            row.IsXhsRouteBusy = false;
        }
    }

    /// <summary>清除已建路线（回到不关联；草稿一并清）。</summary>
    [RelayCommand]
    private void ClearXhsRoute(TargetRowViewModel? row)
    {
        if (row is null || !row.HasXhsRoute) return;
        row.ClearXhsRoute();
        row.XhsRouteStatusText = "已清除路线 —— 本条不关联路线";
        row.XhsRouteStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>
    /// 搜索地点（query/poi/by/keyword）。**永不抛** —— 失败写状态行（路线可选，
    /// 不拦发布）。预算 120s（小红书签名宿主冷启动）。
    /// </summary>
    private async Task FetchXhsRoutePoisAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsXhsRouteBusy || !row.IsXhs) return;

        var keyword = (row.XhsRouteQuery ?? "").Trim();
        if (keyword.Length == 0)
        {
            row.XhsRouteStatusText = "先输入地点关键词再搜索";
            row.XhsRouteStatusIsError = true;
            return;
        }

        row.IsXhsRouteBusy = true;
        row.XhsRouteStatusText = "正在搜索地点…（首次要等平台签名准备，可能要几十秒）";
        row.XhsRouteStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.XhsRouteStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试";
                row.XhsRouteStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await XhsSignerService.PrepareAsync(options.SessionId, cts.Token);

            var items = await new XhsRouteService(options)
                .SearchPoiAsync(creds.Cookie, keyword, cts.Token);
            row.SetXhsRouteCandidates(items);
            row.XhsRouteStatusText = items.Count > 0
                ? $"找到 {items.Count} 个地点"
                : "没有找到相关地点 —— 换个关键词试试";
            row.XhsRouteStatusIsError = items.Count == 0;
        }
        catch (OperationCanceledException)
        {
            row.XhsRouteStatusText = "搜索超时 —— 可重试（不加路线也不影响发布）";
            row.XhsRouteStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.XhsRouteStatusText = "搜索失败：" + ex.Message + " —— 稍后重试";
            row.XhsRouteStatusIsError = true;
            Log.Warning(ex, "[Publish] 小红书路线地点搜索失败");
        }
        finally
        {
            row.IsXhsRouteBusy = false;
        }
    }

    // ═══ 小红书引用笔记选择器（A5，2026-11-11：接平台 note/user/posted）═══
    // 平台页面同一时间只允许引用一条笔记。列表只在打开面板时拉
    // （签名宿主冷启动可达 60s+，不拖慢进页）；失败只写状态行，不打断编辑。

    /// <summary>展开 / 收起引用笔记选择器；首次展开自动拉列表。</summary>
    [RelayCommand]
    private async Task ToggleXhsNotePanelAsync(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsXhsNotePanelOpen = !row.IsXhsNotePanelOpen;
        if (row.IsXhsNotePanelOpen && !row.HasXhsNoteCandidates && !row.IsXhsNoteBusy)
            await FetchXhsNotesAsync(row);
    }

    /// <summary>重新拉一次已发布笔记列表（刚在平台发过笔记后不用关面板重开）。</summary>
    [RelayCommand]
    private async Task RefreshXhsNotesAsync(TargetRowViewModel? row) =>
        await FetchXhsNotesAsync(row);

    /// <summary>选中一条引用笔记 → 写快照并收起面板。</summary>
    [RelayCommand]
    private void PickXhsNote(XhsNotePickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.XhsNoteCandidates.Contains(pick));
        if (row is null) return;

        row.ApplyPickedNote(pick.Item);
        row.IsXhsNotePanelOpen = false;
        row.XhsNoteStatusText = $"已引用「{pick.Name}」";
        row.XhsNoteStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清除已选引用笔记（回到不引用）。</summary>
    [RelayCommand]
    private void ClearXhsNote(TargetRowViewModel? row)
    {
        if (row is null || !row.HasXhsNote) return;
        row.ClearXhsNote();
        row.XhsNoteStatusText = "已清除引用笔记 —— 本条不引用其它笔记";
        row.XhsNoteStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>
    /// 拉该账号的已发布笔记列表。**永不抛** —— 失败写状态行，不打断编辑
    /// （引用笔记是可选项，账号没有笔记 / 接口挂了都不该拦发布）。
    /// 预算 120s：小红书签名宿主冷启动实测可达 60s+（同速览合集选择器）。
    /// </summary>
    private async Task FetchXhsNotesAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsXhsNoteBusy || !row.IsXhs) return;

        row.IsXhsNoteBusy = true;
        row.XhsNoteStatusText = "正在获取已发布笔记…（首次要等平台签名准备，可能要几十秒）";
        row.XhsNoteStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.XhsNoteStatusText =
                    "账号凭证不可用 —— 到「平台账号」重新登录后再试（不引用也不影响发布）";
                row.XhsNoteStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var signerReady = await XhsSignerService.PrepareAsync(options.SessionId, cts.Token);

            var notes = await new XhsNoteService(options)
                .ListPostedAsync(creds.Cookie, tab: 1, page: 0, ct: cts.Token);

            row.SetXhsNoteCandidates(notes);
            row.XhsNoteStatusText = notes.Count > 0
                ? $"找到 {notes.Count} 条已发布笔记（只显示最近一批）"
                : signerReady
                    ? "该账号还没有已发布笔记 —— 可留空（发布页同款面板也是空的）"
                    : "平台没返回笔记，且签名宿主未就绪 —— 稍后重试";
            row.XhsNoteStatusIsError = notes.Count == 0 && !signerReady;
        }
        catch (OperationCanceledException)
        {
            row.XhsNoteStatusText = "请求超时 —— 可重试（不引用笔记也不影响发布）";
            row.XhsNoteStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.XhsNoteStatusText =
                "拉取失败：" + ex.Message + " —— 稍后重试（不引用笔记也不影响发布）";
            row.XhsNoteStatusIsError = true;
            Log.Warning(ex, "[Publish] 小红书引用笔记列表拉取失败");
        }
        finally
        {
            row.IsXhsNoteBusy = false;
        }
    }

    // ═══ 小红书 @用户选择器（2026-11-10：接平台 search/user_info）═══
    // 平台正文里的 @ 是结构化字段（common.ats）—— 手填 JSON 难写也验证不了；
    // 按昵称搜账号（带小红书号 / 简介片段）后点选。可多选，点 × 移除。
    // 选中只写 XhsExtras.Ats；正文缺「@昵称」由 XhsCaption 补（预检与组装同一份）。

    /// <summary>
    /// 展开 / 收起 @用户选择器。**不自动搜索** —— 空关键词虽然有效（页面同款返回推荐用户），
    /// 但一展开就打一次签名接口太贵；用户输入后点「搜索」。
    /// </summary>
    [RelayCommand]
    private void ToggleXhsMentionPanel(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsXhsMentionPanelOpen = !row.IsXhsMentionPanelOpen;
    }

    /// <summary>按昵称搜索用户（空词 = 页面同款，平台回推荐用户）。</summary>
    [RelayCommand]
    private async Task SearchXhsMentionsAsync(TargetRowViewModel? row) =>
        await FetchXhsMentionsAsync(row);

    /// <summary>加入一个 @用户（面板保持展开 —— 多选是常态）。</summary>
    [RelayCommand]
    private void PickXhsMention(XhsMentionPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.XhsMentionCandidates.Contains(pick));
        if (row is null) return;

        if (!row.AddXhsMention(pick.User))
        {
            row.XhsMentionStatusText = $"「{pick.Nickname}」已在 @ 列表里";
            row.XhsMentionStatusIsError = false;
            return;
        }

        row.XhsMentionStatusText = $"已添加 @{pick.Nickname}（正文末尾会补 @昵称）";
        row.XhsMentionStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>移除一个已选 @用户。</summary>
    [RelayCommand]
    private void RemoveXhsMention(XhsMentionPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.XhsMentions.Contains(pick));
        if (row is null) return;

        row.RemoveXhsMention(pick);
        row.XhsMentionStatusText = $"已移除 @{pick.Nickname}";
        row.XhsMentionStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清空已选 @用户（发布体不下发 common.ats）。</summary>
    [RelayCommand]
    private void ClearXhsMentions(TargetRowViewModel? row)
    {
        if (row is null || !row.HasXhsMentions) return;
        row.ClearXhsMentions();
        row.XhsMentionStatusText = "已清空 @用户 —— 本条不再提及任何人";
        row.XhsMentionStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>
    /// 搜索 @用户。**永不抛** —— 失败写状态行，不打断编辑
    /// （@用户是可选项，接口挂了不该拦发布）。
    /// 预算 120s：小红书签名宿主冷启动实测可达 60s+（同速览合集 / 话题候选）。
    /// </summary>
    private async Task FetchXhsMentionsAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsXhsMentionBusy || !row.IsXhs) return;

        row.IsXhsMentionBusy = true;
        row.XhsMentionStatusText = "正在搜索用户…（首次要等平台签名准备，可能要几十秒）";
        row.XhsMentionStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.XhsMentionStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试（不 @ 用户也不影响发布）";
                row.XhsMentionStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var signerReady = await XhsSignerService.PrepareAsync(options.SessionId, cts.Token);

            var users = await new XhsMentionService(options)
                .SearchAsync(creds.Cookie, row.XhsMentionQuery.Trim(), cts.Token);

            row.SetXhsMentionCandidates(users);
            row.XhsMentionStatusText = users.Count > 0
                ? $"共 {users.Count} 个用户 —— 点「选」加入 @ 列表"
                : signerReady
                    ? "没有搜到用户 —— 换个昵称再试"
                    : "平台没返回用户，且签名宿主未就绪 —— 稍后重试";
            row.XhsMentionStatusIsError = users.Count == 0 && !signerReady;
        }
        catch (OperationCanceledException)
        {
            row.XhsMentionStatusText = "搜索超时 —— 可重试（不 @ 用户也不影响发布）";
            row.XhsMentionStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.XhsMentionStatusText = "搜索失败：" + ex.Message + " —— 稍后重试（不 @ 用户也不影响发布）";
            row.XhsMentionStatusIsError = true;
            Log.Warning(ex, "[Publish] 小红书 @用户搜索失败");
        }
        finally
        {
            row.IsXhsMentionBusy = false;
        }
    }

    // ═══ 抖音 @好友选择器（2026-11-11 补抓：discover/search）═══
    // 发布体三件套：common.mentions（uid 数组）+ 正文「@昵称」+ text_extra type=0 条目。
    // 搜索走本地 msToken/a_bogus（同标签选择器），不需要 WebView 签名宿主。
    // **永不抛**：@好友是可选项，接口挂了只写状态行，不拦发布。

    /// <summary>展开 / 收起 @好友选择器（不自动搜索 —— 一展开就打一次签名接口太贵）。</summary>
    [RelayCommand]
    private void ToggleDouyinMentionPanel(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsDouyinMentionPanelOpen = !row.IsDouyinMentionPanelOpen;
    }

    [RelayCommand]
    private async Task SearchDouyinMentionsAsync(TargetRowViewModel? row) =>
        await FetchDouyinMentionsAsync(row);

    /// <summary>加入一个 @好友（面板保持展开 —— 多选是常态）。</summary>
    [RelayCommand]
    private void PickDouyinMention(DouyinMentionPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.DouyinMentionCandidates.Contains(pick));
        if (row is null) return;

        if (!row.AddDouyinMention(pick.User))
        {
            row.DouyinMentionStatusText = $"「{pick.Nickname}」已在 @ 列表里";
            row.DouyinMentionStatusIsError = false;
            return;
        }

        row.DouyinMentionStatusText = $"已添加 @{pick.Nickname}（正文末尾会补 @昵称）";
        row.DouyinMentionStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    [RelayCommand]
    private void RemoveDouyinMention(DouyinMentionPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.DouyinMentions.Contains(pick));
        if (row is null) return;

        row.RemoveDouyinMention(pick);
        row.DouyinMentionStatusText = $"已移除 @{pick.Nickname}";
        row.DouyinMentionStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清空已选 @好友（发布体不再下发 common.mentions）。</summary>
    [RelayCommand]
    private void ClearDouyinMentions(TargetRowViewModel? row)
    {
        if (row is null || !row.HasDouyinMentions) return;
        row.ClearDouyinMentions();
        row.DouyinMentionStatusText = "已清空 @好友 —— 本条不再提及任何人";
        row.DouyinMentionStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>按昵称 / 抖音号搜索 @好友候选（空词原样发，平台可能回推荐）。永不抛。</summary>
    private async Task FetchDouyinMentionsAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsDouyinMentionBusy || !row.IsDouyin) return;

        row.IsDouyinMentionBusy = true;
        row.DouyinMentionStatusText = "正在搜索用户…";
        row.DouyinMentionStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.DouyinMentionStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试（不 @ 好友也不影响发布）";
                row.DouyinMentionStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var users = await new DouyinMentionService(options)
                .SearchAsync(creds.Cookie, row.DouyinMentionQuery.Trim(), ct: cts.Token);

            row.SetDouyinMentionCandidates(users);
            row.DouyinMentionStatusText = users.Count > 0
                ? $"共 {users.Count} 个用户 —— 点「选」加入 @ 列表"
                : "没有搜到用户 —— 换个昵称或抖音号再试";
        }
        catch (OperationCanceledException)
        {
            row.DouyinMentionStatusText = "搜索超时 —— 可重试（不 @ 好友也不影响发布）";
            row.DouyinMentionStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.DouyinMentionStatusText = "搜索失败：" + ex.Message + " —— 稍后重试（不 @ 好友也不影响发布）";
            row.DouyinMentionStatusIsError = true;
            Log.Warning(ex, "[Publish] 抖音 @好友搜索失败");
        }
        finally
        {
            row.IsDouyinMentionBusy = false;
        }
    }

    // ═══ 视频号 @好友（2026-11-11 B2：helper_search_finder_account）═══
    // 提交体只认昵称：objectDesc.mentionedUser=[{nickname}] + 正文末尾「@昵称 」。
    // 搜索需要视频号 Cookie + deviceId（extra_data 里），缺 deviceId 时状态行给刷新凭证的指引。

    /// <summary>展开 / 收起 @好友面板。</summary>
    [RelayCommand]
    private void ToggleWechatMentionPanel(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsWechatMentionPanelOpen = !row.IsWechatMentionPanelOpen;
    }

    /// <summary>按昵称搜索 @好友候选（空词原样发，由平台决定返回什么）。永不抛。</summary>
    [RelayCommand]
    private async Task SearchWechatMentionsAsync(TargetRowViewModel? row) =>
        await FetchWechatMentionsAsync(row);

    /// <summary>加入一个 @好友（面板保持展开 —— 多选是常态）。</summary>
    [RelayCommand]
    private void PickWechatMention(WechatMentionPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.WechatMentionCandidates.Contains(pick));
        if (row is null) return;

        if (!row.AddWechatMention(pick.User))
        {
            row.WechatMentionStatusText = $"「{pick.Nickname}」已在 @ 列表里";
            row.WechatMentionStatusIsError = false;
            return;
        }

        row.WechatMentionStatusText = $"已添加 @{pick.Nickname}（正文末尾会补「@昵称 」）";
        row.WechatMentionStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>移除一个已选 @好友。</summary>
    [RelayCommand]
    private void RemoveWechatMention(WechatMentionPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.WechatMentions.Contains(pick));
        if (row is null) return;

        row.RemoveWechatMention(pick);
        row.WechatMentionStatusText = $"已移除 @{pick.Nickname}";
        row.WechatMentionStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清空已选 @好友（发布体不再下发 mentionedUser）。</summary>
    [RelayCommand]
    private void ClearWechatMentions(TargetRowViewModel? row)
    {
        if (row is null || !row.HasWechatMentions) return;
        row.ClearWechatMentions();
        row.WechatMentionStatusText = "已清空 @好友 —— 本条不再提及任何人";
        row.WechatMentionStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>搜索 @好友候选。接口失败 / 缺 deviceId 只写状态行，不拦发布。</summary>
    private async Task FetchWechatMentionsAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsWechatMentionBusy || !row.IsWechatVideo) return;

        row.IsWechatMentionBusy = true;
        row.WechatMentionStatusText = "正在搜索视频号…";
        row.WechatMentionStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.WechatMentionStatusText =
                    "账号凭证不可用 —— 到「平台账号」重新登录后再试（不 @ 好友也不影响发布）";
                row.WechatMentionStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };
            var deviceId = CredentialFields.Extra(creds.ExtraDataJson)
                .GetValueOrDefault("deviceId")?.ToString() ?? "";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var users = await new WechatVideoMentionService(options)
                .SearchAsync(creds.Cookie, deviceId, row.WechatMentionQuery.Trim(), ct: cts.Token);

            row.SetWechatMentionCandidates(users);
            row.WechatMentionStatusText = users.Count > 0
                ? $"共 {users.Count} 个视频号 —— 点「选」加入 @ 列表"
                : "没有搜到视频号 —— 换个昵称再试";
        }
        catch (OperationCanceledException)
        {
            row.WechatMentionStatusText = "搜索超时 —— 可重试（不 @ 好友也不影响发布）";
            row.WechatMentionStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.WechatMentionStatusText =
                "搜索失败：" + ex.Message + " —— 稍后重试（不 @ 好友也不影响发布）";
            row.WechatMentionStatusIsError = true;
            Log.Warning(ex, "[Publish] 视频号 @好友搜索失败");
        }
        finally
        {
            row.IsWechatMentionBusy = false;
        }
    }

    // ═══ 快手 @好友（B3 抓包 2026-11-11：at/search + at/choose）═══
    // 落点不是独立字段：caption 里的纯文本 token `@昵称(O<userId>)`（执行端 KuaishouMentionText 追加）。
    // 搜索与热点同一条签名链（sig3 宿主），预算同样给 150s（冷启动要开页面等签名模块）。

    /// <summary>展开 / 收起 @好友面板。</summary>
    [RelayCommand]
    private void ToggleKsMentionPanel(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsKsMentionPanelOpen = !row.IsKsMentionPanelOpen;
    }

    /// <summary>按昵称搜索 @好友候选。空词原样发，由平台决定返回什么。</summary>
    [RelayCommand]
    private async Task SearchKsMentionsAsync(TargetRowViewModel? row) =>
        await FetchKsMentionsAsync(row);

    /// <summary>加入一个 @好友（面板保持展开 —— 多选是常态）。</summary>
    [RelayCommand]
    private void PickKsMention(KsMentionPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.KsMentionCandidates.Contains(pick));
        if (row is null) return;

        if (!row.AddKsMention(pick.User))
        {
            row.KsMentionStatusText = $"「{pick.UserName}」已在 @ 列表里";
            row.KsMentionStatusIsError = false;
            return;
        }

        row.KsMentionStatusText =
            $"已添加 @{pick.UserName}（发布时正文里会变成可点击的 @ 标签）";
        row.KsMentionStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>移除一个已选 @好友。</summary>
    [RelayCommand]
    private void RemoveKsMention(KsMentionPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.KsMentions.Contains(pick));
        if (row is null) return;

        row.RemoveKsMention(pick);
        row.KsMentionStatusText = $"已移除 @{pick.UserName}";
        row.KsMentionStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清空已选 @好友（caption 不再带 @ HTML 片段）。</summary>
    [RelayCommand]
    private void ClearKsMentions(TargetRowViewModel? row)
    {
        if (row is null || !row.HasKsMentions) return;
        row.ClearKsMentions();
        row.KsMentionStatusText = "已清空 @好友 —— 本条不再提及任何人";
        row.KsMentionStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>
    /// 搜索 @好友候选。**永不抛** —— 失败 / 缺凭证只写状态行，不拦发布
    /// （@好友是可选曝光项）。
    /// </summary>
    private async Task FetchKsMentionsAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsKsMentionBusy || !row.IsKuaishou) return;

        row.IsKsMentionBusy = true;
        row.KsMentionStatusText = "正在搜索快手用户…（首次要等平台签名准备，可能要几十秒）";
        row.KsMentionStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.KsMentionStatusText =
                    "账号凭证不可用 —— 到「平台账号」重新登录后再试（不 @ 好友也不影响发布）";
                row.KsMentionStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var signerReady = await KuaishouSignerService.PrepareAsync(options.SessionId, cts.Token);

            var page = await new KuaishouMentionService(options)
                .SearchAsync(creds.Cookie, row.KsMentionQuery.Trim(), ct: cts.Token);

            row.SetKsMentionCandidates(page.Items);
            row.KsMentionStatusText = page.Items.Count > 0
                ? $"共 {page.Items.Count} 个用户 —— 点「选」加入 @ 列表"
                  + (page.HasMore ? "（还有更多，换个更准的关键词）" : "")
                : "没有搜到用户 —— 换个昵称再试";
            row.KsMentionStatusIsError = page.Items.Count == 0 && !signerReady;
        }
        catch (OperationCanceledException)
        {
            row.KsMentionStatusText = "搜索超时 —— 可重试（不 @ 好友也不影响发布）";
            row.KsMentionStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.KsMentionStatusText =
                "搜索失败：" + ex.Message + " —— 稍后重试（不 @ 好友也不影响发布）";
            row.KsMentionStatusIsError = true;
            Log.Warning(ex, "[Publish] 快手 @好友搜索失败");
        }
        finally
        {
            row.IsKsMentionBusy = false;
        }
    }

    // ═══ B站分区选择器（2026-11-10：human/type2/list）═══
    // 投稿页的「分区」下拉 = human_type2（30 个领域）。旧版 tid 保持默认 / params 覆盖 ——
    // archive/pre 的 typelist 对当前账号只回 20 个父分区（无子分区），tid ↔ human_type2
    // 映射未知（抓包只有 1015 → 21 一个样本），不在 UI 里猜。

    /// <summary>展开 / 收起分区面板；首次展开自动拉列表。</summary>
    [RelayCommand]
    private async Task ToggleBiliPartitionPanelAsync(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsBiliPartitionPanelOpen = !row.IsBiliPartitionPanelOpen;
        if (row.IsBiliPartitionPanelOpen && !row.HasBiliPartitionCandidates && !row.IsBiliPartitionBusy)
            await FetchBiliPartitionsAsync(row);
    }

    /// <summary>重新拉 B站分区列表。</summary>
    [RelayCommand]
    private async Task RefreshBiliPartitionsAsync(TargetRowViewModel? row) =>
        await FetchBiliPartitionsAsync(row);

    /// <summary>选中一个分区 → 写 <c>human_type2</c> 并收起面板。</summary>
    [RelayCommand]
    private void PickBiliPartition(BiliTypePickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.BiliPartitionCandidates.Contains(pick));
        if (row is null) return;

        row.ApplyPickedBiliPartition(pick.Type);
        row.BiliPartitionStatusText = $"已选分区「{pick.Name}」";
        row.BiliPartitionStatusIsError = false;
        row.IsBiliPartitionPanelOpen = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>拉 B站分区列表（human_type2）。**永不抛** —— 失败写状态行，不打断编辑。</summary>
    private async Task FetchBiliPartitionsAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsBiliPartitionBusy || !row.IsBilibili) return;

        row.IsBiliPartitionBusy = true;
        row.BiliPartitionStatusText = "正在获取分区列表…";
        row.BiliPartitionStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.BiliPartitionStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试（不改分区也不影响发布）";
                row.BiliPartitionStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var types = await new BilibiliPartitionService(options)
                .GetHumanTypesAsync(creds.Cookie, cts.Token);

            row.SetBiliPartitionCandidates(types);
            row.BiliPartitionStatusText = types.Count > 0
                ? $"共 {types.Count} 个分区"
                : "平台没有返回分区列表";
            row.BiliPartitionStatusIsError = types.Count == 0;
        }
        catch (OperationCanceledException)
        {
            row.BiliPartitionStatusText = "请求超时 —— 可重试（不改分区也不影响发布）";
            row.BiliPartitionStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.BiliPartitionStatusText = "拉取失败：" + ex.Message + " —— 稍后重试（不改分区也不影响发布）";
            row.BiliPartitionStatusIsError = true;
            Log.Warning(ex, "[Publish] B站分区列表拉取失败");
        }
        finally
        {
            row.IsBiliPartitionBusy = false;
        }
    }

    // ═══ 快手「专属领域 / 二级领域」选择器（2026-11-10：upload/domain/list）═══
    // 平台领域是两级列表（专属领域 → 二级领域），页面加载就拉这份数据；
    // 选中仍只写 KuaishouExtras.Domain / SecondDomain（wire 不变）。

    /// <summary>展开 / 收起领域面板；首次展开自动拉列表。</summary>
    [RelayCommand]
    private async Task ToggleKsDomainPanelAsync(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsKsDomainPanelOpen = !row.IsKsDomainPanelOpen;
        if (row.IsKsDomainPanelOpen && !row.HasKsDomainCandidates && !row.IsKsDomainBusy)
            await FetchKsDomainsAsync(row);
    }

    /// <summary>重新拉快手领域列表。</summary>
    [RelayCommand]
    private async Task RefreshKsDomainsAsync(TargetRowViewModel? row) =>
        await FetchKsDomainsAsync(row);

    /// <summary>选中一个专属领域 → 写 <c>domain</c> 并摊开二级领域（面板不收起，方便接着选二级）。</summary>
    [RelayCommand]
    private void PickKsDomain(KsDomainPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.KsDomainCandidates.Contains(pick));
        if (row is null) return;

        row.ApplyKsFirstDomain(pick.Domain);
        row.KsDomainStatusText = pick.HasSecond
            ? $"已选专属领域「{pick.Name}」—— 可直接发布，或再选一个二级领域"
            : $"已选专属领域「{pick.Name}」（该领域没有二级领域）";
        row.KsDomainStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>选中一个二级领域 → 写 <c>secondDomain</c> 并收起面板。</summary>
    [RelayCommand]
    private void PickKsSecondDomain(KsSecondDomainPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.KsSecondDomainCandidates.Contains(pick));
        if (row is null) return;

        row.ApplyKsSecondDomain(pick.Name);
        row.KsDomainStatusText = $"已选「{row.KsDomainDisplay} / {pick.Name}」";
        row.KsDomainStatusIsError = false;
        row.IsKsDomainPanelOpen = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清除两级领域（回到不下发）。</summary>
    [RelayCommand]
    private void ClearKsDomains(TargetRowViewModel? row)
    {
        if (row is null || !row.HasKsDomainOrSecond) return;
        row.ClearKsDomains();
        row.KsDomainStatusText = "已清除领域 —— 发布时不下发 domain / secondDomain";
        row.KsDomainStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>
    /// 拉快手领域列表。**永不抛** —— 失败写状态行，不打断编辑
    /// （领域是可选项，账号没权限/接口挂了都不该拦发布）。
    /// 预算 150s：快手签名宿主冷启动要开页面 + 等签名模块（同 KuaishouSignerService 预热上限）。
    /// </summary>
    private async Task FetchKsDomainsAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsKsDomainBusy || !row.IsKuaishou) return;

        row.IsKsDomainBusy = true;
        row.KsDomainStatusText = "正在获取领域列表…（首次要等平台签名准备，可能要几十秒）";
        row.KsDomainStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.KsDomainStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试（不选领域也不影响发布）";
                row.KsDomainStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var signerReady = await KuaishouSignerService.PrepareAsync(options.SessionId, cts.Token);

            var domains = await new KuaishouDomainService(options)
                .GetDomainsAsync(creds.Cookie, cts.Token);

            row.SetKsDomainCandidates(domains);
            row.KsDomainStatusText = domains.Count > 0
                ? $"共 {domains.Count} 个专属领域"
                : signerReady
                    ? "平台没有返回领域列表 —— 可留空（发布页同款下拉也是空的）"
                    : "平台没返回领域列表，且签名宿主未就绪 —— 稍后重试";
            row.KsDomainStatusIsError = domains.Count == 0 && !signerReady;
        }
        catch (OperationCanceledException)
        {
            row.KsDomainStatusText = "请求超时 —— 可重试（不选领域也不影响发布）";
            row.KsDomainStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.KsDomainStatusText = "拉取失败：" + ex.Message + " —— 稍后重试（不选领域也不影响发布）";
            row.KsDomainStatusIsError = true;
            Log.Warning(ex, "[Publish] 快手领域列表拉取失败");
        }
        finally
        {
            row.IsKsDomainBusy = false;
        }
    }

    // ═══ 快手「关联热点」选择器（B3 抓包 2026-11-11：hotspot/list）═══
    // 热点不是自由文本 —— 平台只认自己的 hotspotId（+ hotspotConnectType:1），
    // 所以做成「搜索 / 热门 → 点选」；不选 = 不参与热点（两个键都不下发）。

    /// <summary>展开 / 收起热点面板；首次展开自动拉推荐热榜（点开即有内容）。</summary>
    [RelayCommand]
    private async Task ToggleKsHotspotPanelAsync(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsKsHotspotPanelOpen = !row.IsKsHotspotPanelOpen;
        if (row.IsKsHotspotPanelOpen && !row.HasKsHotspotCandidates && !row.IsKsHotspotBusy)
            await FetchKsHotspotsAsync(row, trending: true, loadMore: false);
    }

    /// <summary>按关键词搜索热点（空关键词不请求，提示改用「热门」）。</summary>
    [RelayCommand]
    private async Task SearchKsHotspotsAsync(TargetRowViewModel? row)
    {
        if (row is null) return;
        if (string.IsNullOrWhiteSpace(row.KsHotspotQuery))
        {
            row.KsHotspotStatusText = "先输入关键词，或点「热门」直接看推荐";
            row.KsHotspotStatusIsError = false;
            return;
        }
        await FetchKsHotspotsAsync(row, trending: false, loadMore: false);
    }

    /// <summary>拉推荐热榜（不传关键词）。</summary>
    [RelayCommand]
    private async Task FetchTrendingKsHotspotsAsync(TargetRowViewModel? row) =>
        await FetchKsHotspotsAsync(row, trending: true, loadMore: false);

    /// <summary>
    /// 加载更多 —— 用上一次列表请求的关键词与游标。
    /// 不读正在编辑的输入框（用户可能已经改了词，但列表里还是上一次的结果）。
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreKsHotspotsAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsKsHotspotBusy || !row.KsHotspotHasMore) return;
        await FetchKsHotspotsAsync(row, trending: row.KsHotspotLastQuery.Length == 0, loadMore: true);
    }

    /// <summary>选中一条热点 → 写 <c>hotspotId</c>（+ 文案快照）并收起面板。</summary>
    [RelayCommand]
    private void PickKsHotspot(KsHotspotPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.KsHotspotCandidates.Contains(pick));
        if (row is null) return;

        row.ApplyKsHotspot(pick.Item);
        row.IsKsHotspotPanelOpen = false;
        row.KsHotspotStatusText = $"已选热点「{pick.Content}」";
        row.KsHotspotStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清除已选热点（回到不参与热点）。</summary>
    [RelayCommand]
    private void ClearKsHotspot(TargetRowViewModel? row)
    {
        if (row is null || !row.HasKsHotspot) return;
        row.ClearKsHotspot();
        row.KsHotspotStatusText = "已清除热点 —— 发布时不带 hotspotId";
        row.KsHotspotStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    // ═══ 快手「作者服务 · 关联小程序」（2026-11-15 真页面抓包：relation/list → associateTasks）═══
    // 平台要的是 relation/list 解析出的整个 associateTaskId 对象，所以做成
    // 「粘小程序链接 → 解析 → 点选」；不选 = submit 不带 associateTasks 键。

    /// <summary>展开 / 收起小程序面板。</summary>
    [RelayCommand]
    private void ToggleKsAssociatePanel(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsKsAssociatePanelOpen = !row.IsKsAssociatePanelOpen;
    }

    /// <summary>解析小程序链接（空链接不请求，直接提示）。</summary>
    [RelayCommand]
    private async Task SearchKsAssociateAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsKsAssociateBusy || !row.IsKuaishou) return;

        var link = row.KsAssociateLink.Trim();
        if (link.Length == 0)
        {
            row.KsAssociateStatusText = "先粘贴小程序页面链接（快手 APP → 小程序 → 右上角「复制页面链接」）";
            row.KsAssociateStatusIsError = false;
            return;
        }

        row.IsKsAssociateBusy = true;
        row.KsAssociateStatusText = "正在解析小程序链接…（首次要等平台签名准备，可能要几十秒）";
        row.KsAssociateStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.KsAssociateStatusText =
                    "账号凭证不可用 —— 到「平台账号」重新登录后再试（不关联也不影响发布）";
                row.KsAssociateStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var signerReady = await KuaishouSignerService.PrepareAsync(options.SessionId, cts.Token);

            var page = await new KuaishouRelationService(options)
                .SearchAsync(creds.Cookie, link, ct: cts.Token);

            row.SetKsAssociateCandidates(page.Items);
            row.KsAssociateStatusText = page.Items.Count > 0
                ? $"找到 {page.Items.Count} 个可关联小程序 —— 点「选」确认"
                : "没有解析到小程序 —— 确认链接是从快手 APP 的小程序页面复制的";
            row.KsAssociateStatusIsError = page.Items.Count == 0 && !signerReady;
        }
        catch (OperationCanceledException)
        {
            row.KsAssociateStatusText = "请求超时 —— 可重试（不关联也不影响发布）";
            row.KsAssociateStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.KsAssociateStatusText = "解析失败：" + ex.Message + " —— 稍后重试（不关联也不影响发布）";
            row.KsAssociateStatusIsError = true;
            Log.Warning(ex, "[Publish] 快手小程序链接解析失败");
        }
        finally
        {
            row.IsKsAssociateBusy = false;
        }
    }

    /// <summary>选中一条解析结果 → 写 <c>associateTasks</c> 元素并收起面板。</summary>
    [RelayCommand]
    private void PickKsAssociate(KsRelationPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.KsAssociateCandidates.Contains(pick));
        if (row is null) return;

        row.ApplyKsAssociate(pick.Item, row.KsAssociateLink.Trim());
        row.IsKsAssociatePanelOpen = false;
        row.KsAssociateStatusText = $"已关联小程序「{pick.Title}」";
        row.KsAssociateStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清除已关联小程序（发布时不带 <c>associateTasks</c>）。</summary>
    [RelayCommand]
    private void ClearKsAssociate(TargetRowViewModel? row)
    {
        if (row is null || !row.HasKsAssociate) return;
        row.ClearKsAssociate();
        row.KsAssociateStatusText = "已清除 — 发布时不带 associateTasks";
        row.KsAssociateStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    // ═══ 快手「共同创作」（2026-11-15 发布页 bundle 定案：coPublishUser + 角色枚举）═══
    // 选人搜索复用 at/search（bundle 的 FETCH_SEARCH_USER 与 @好友同端点）。

    /// <summary>展开 / 收起共创面板。</summary>
    [RelayCommand]
    private void ToggleKsCoPublishPanel(TargetRowViewModel? row)
    {
        if (row is null) return;
        row.IsKsCoPublishPanelOpen = !row.IsKsCoPublishPanelOpen;
    }

    /// <summary>搜索共创候选人（复用 at/search；关键词可空 —— 空词即平台默认列表）。</summary>
    [RelayCommand]
    private async Task SearchKsCoPublishAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsKsCoPublishBusy || !row.IsKuaishou) return;

        row.IsKsCoPublishBusy = true;
        row.KsCoPublishStatusText = "正在搜索快手用户…（首次要等平台签名准备，可能要几十秒）";
        row.KsCoPublishStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.KsCoPublishStatusText =
                    "账号凭证不可用 —— 到「平台账号」重新登录后再试（不共创也不影响发布）";
                row.KsCoPublishStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var signerReady = await KuaishouSignerService.PrepareAsync(options.SessionId, cts.Token);

            var page = await new KuaishouMentionService(options)
                .SearchAsync(creds.Cookie, row.KsCoPublishQuery.Trim(), ct: cts.Token);

            row.SetKsCoPublishCandidates(page.Items);
            row.KsCoPublishStatusText = page.Items.Count > 0
                ? $"共 {page.Items.Count} 个用户 —— 点「选」加入共创"
                : "没有搜到用户 —— 换个昵称再试";
            row.KsCoPublishStatusIsError = page.Items.Count == 0 && !signerReady;
        }
        catch (OperationCanceledException)
        {
            row.KsCoPublishStatusText = "请求超时 —— 可重试（不共创也不影响发布）";
            row.KsCoPublishStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.KsCoPublishStatusText = "搜索失败：" + ex.Message + " —— 稍后重试（不共创也不影响发布）";
            row.KsCoPublishStatusIsError = true;
            Log.Warning(ex, "[Publish] 快手共创用户搜索失败");
        }
        finally
        {
            row.IsKsCoPublishBusy = false;
        }
    }

    /// <summary>加入一名共创（面板保持展开；同一用户已在列表里就提示）。</summary>
    [RelayCommand]
    private void PickKsCoPublish(KsMentionPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.KsCoPublishCandidates.Contains(pick));
        if (row is null) return;

        if (!row.AddKsCoPublish(pick.User))
        {
            row.KsCoPublishStatusText = $"「{pick.UserName}」已在共创列表里";
            row.KsCoPublishStatusIsError = false;
            return;
        }

        row.KsCoPublishStatusText =
            $"已加入「{pick.UserName}」—— 可在右侧下拉里改角色（默认 / 作者 / 共创 / 出镜）";
        row.KsCoPublishStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>移除一名已选共创。</summary>
    [RelayCommand]
    private void RemoveKsCoPublish(KsCoPublishPickViewModel? pick)
    {
        if (pick is null) return;
        var row = TargetRows.FirstOrDefault(r => r.KsCoPublishUsers.Contains(pick));
        if (row is null) return;

        row.RemoveKsCoPublish(pick);
        row.KsCoPublishStatusText = $"已移除「{pick.UserName}」";
        row.KsCoPublishStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>清空共创（发布时 <c>coPublishUser</c> 为空数组）。</summary>
    [RelayCommand]
    private void ClearKsCoPublish(TargetRowViewModel? row)
    {
        if (row is null || !row.HasKsCoPublishUsers) return;
        row.ClearKsCoPublish();
        row.KsCoPublishStatusText = "已清空共创 —— 发布时 coPublishUser 为空数组";
        row.KsCoPublishStatusIsError = false;
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>
    /// 拉一批快手热点候选（搜索 / 热榜 / 翻页）。**永不抛** —— 失败写状态行，不打断编辑
    /// （热点是可选曝光项，接口挂了不该拦发布）。
    /// 预算 150s：快手签名宿主冷启动要开页面 + 等签名模块（同领域列表 / KuaishouSignerService 预热上限）。
    /// </summary>
    private async Task FetchKsHotspotsAsync(
        TargetRowViewModel? row, bool trending, bool loadMore)
    {
        if (row is null || row.IsKsHotspotBusy || !row.IsKuaishou) return;

        var keyword = loadMore ? row.KsHotspotLastQuery : row.KsHotspotQuery.Trim();
        var cursor = loadMore ? row.KsHotspotNextCursor : KuaishouHotspotService.FirstCursor;

        row.IsKsHotspotBusy = true;
        row.KsHotspotStatusText = loadMore
            ? "正在加载更多热点…"
            : trending
                ? "正在获取热门热点…（首次要等平台签名准备，可能要几十秒）"
                : "正在搜索热点…";
        row.KsHotspotStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.KsHotspotStatusText =
                    "账号凭证不可用 —— 到「平台账号」重新登录后再试（不选热点也不影响发布）";
                row.KsHotspotStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var signerReady = await KuaishouSignerService.PrepareAsync(options.SessionId, cts.Token);

            // caption 与发布体同一份（页面也拿当前作品文案做相关性推荐）
            var caption = SelectedDraft is { } draft
                ? PublishPayloadBuilder.KuaishouCaption(draft, row.Params)
                : "";

            var page = await new KuaishouHotspotService(options)
                .QueryAsync(creds.Cookie, keyword, caption, cursor, cts.Token);

            row.SetKsHotspotCandidates(page.Items, page.NextCursor, append: loadMore);
            row.KsHotspotLastQuery = keyword;
            row.KsHotspotStatusText = page.Items.Count > 0
                ? loadMore
                    ? $"又加载 {page.Items.Count} 个热点（共 {row.KsHotspotCandidates.Count} 个）"
                    : trending
                        ? $"热门热点 {row.KsHotspotCandidates.Count} 个"
                        : $"找到 {page.Items.Count} 个热点"
                : loadMore
                    ? "没有更多热点了"
                    : trending
                        ? "平台这次没有返回热门热点 —— 可换个关键词搜"
                        : "没有搜到热点 —— 换个关键词再试";
            row.KsHotspotStatusIsError = page.Items.Count == 0 && !signerReady;
        }
        catch (OperationCanceledException)
        {
            row.KsHotspotStatusText = "请求超时 —— 可重试（不选热点也不影响发布）";
            row.KsHotspotStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.KsHotspotStatusText = "拉取失败：" + ex.Message + " —— 稍后重试（不选热点也不影响发布）";
            row.KsHotspotStatusIsError = true;
            Log.Warning(ex, "[Publish] 快手热点列表拉取失败");
        }
        finally
        {
            row.IsKsHotspotBusy = false;
        }
    }

    // ═══ 头条发布权限（GetPublishAuth，2026-11-10）═══

    /// <summary>
    /// 检查头条账号的发布权限（赞赏 / 原创 / 禁发）—— 发布页初始化时页面自己拉的
    /// <c>xigua/api/upload/GetPublishAuth</c>（抓包 <c>docs/toutiao/20261105</c>）。
    /// **永不抛**：失败写状态行，不打断编辑。
    ///
    /// 存在的意义：「赞赏」开关在 <c>HasPraisePermission=false</c> 的账号上会被平台静默忽略，
    /// 用户看不到任何反馈；查一次把结论摆出来（与原创 <c>ClaimOrigin</c> 同一条通道，
    /// 2026-11-10 真账号回读两者都是 false）。
    /// </summary>
    [RelayCommand]
    private async Task CheckToutiaoAuthAsync(TargetRowViewModel? row)
    {
        if (row is null || row.IsToutiaoAuthBusy || !row.IsToutiao) return;

        row.IsToutiaoAuthBusy = true;
        row.ToutiaoAuthStatusText = "正在查询账号发布权限…";
        row.ToutiaoAuthStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.ToutiaoAuthStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试（不影响发布）";
                row.ToutiaoAuthStatusIsError = true;
                return;
            }

            var extra = CredentialFields.Extra(creds.ExtraDataJson);
            var csrfToken = extra.GetValueOrDefault("csrfToken")?.ToString();
            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var raw = await new ToutiaoVideoService(options)
                .GetPublishAuthRawAsync(creds.Cookie, csrfToken, cts.Token);

            var auth = ToutiaoPublishAuthParser.Parse(raw);
            if (auth is null)
            {
                row.ToutiaoAuthStatusText = "没能从平台响应里读出发布权限（形状可能变了）—— 可重试";
                row.ToutiaoAuthStatusIsError = true;
                return;
            }

            row.ToutiaoPraiseAllowed = auth.PraiseAllowed;
            var parts = new List<string>();
            if (auth.PublishDisabled == true) parts.Add("⚠ 平台已限制该账号发布");
            parts.Add(auth.PraiseAllowed switch
            {
                true => "赞赏：可用",
                false => "赞赏：无权限（开启会被平台静默忽略）",
                null => "赞赏：权限未知",
            });
            parts.Add(auth.ClaimOriginAllowed switch
            {
                true => "原创：可用",
                false => auth.NoClaimOriginReason is { Length: > 0 } reason
                    ? $"原创：不可用（{reason}）"
                    : "原创：无权限",
                null => "原创：权限未知",
            });
            // 视频生成图文（A2）：入口由 TransferToArticleButtonShow 控制；
            // 只有用户真的选了 1/2 且权限不可用时才标红（没选就不打扰）。
            parts.Add(auth.TransferToArticleAllowed switch
            {
                true => "视频生成图文：可用",
                false => "视频生成图文：不可用（平台不显示该入口）",
                null => "视频生成图文：权限未知",
            });
            row.ToutiaoAuthStatusText = string.Join(" · ", parts);
            row.ToutiaoAuthStatusIsError = auth.PublishDisabled == true
                || auth.PraiseAllowed == false
                || (auth.TransferToArticleAllowed == false
                    && row.Params.Toutiao?.VideoTransArticleFlag is 1 or 2);
        }
        catch (OperationCanceledException)
        {
            row.ToutiaoAuthStatusText = "查询超时 —— 可重试（不影响发布）";
            row.ToutiaoAuthStatusIsError = true;
        }
        catch (Exception ex)
        {
            row.ToutiaoAuthStatusText = "查询失败：" + ex.Message + " —— 稍后重试（不影响发布）";
            row.ToutiaoAuthStatusIsError = true;
            Log.Warning(ex, "[Publish] 头条发布权限查询失败");
        }
        finally
        {
            row.IsToutiaoAuthBusy = false;
        }
    }

    /// <summary>关键词搜索平台话题（抖音 / 快手 / 小红书）</summary>
    [RelayCommand]
    private async Task SearchTopicSuggestionsAsync(TargetRowViewModel? row) =>
        await FetchTopicSuggestionsAsync(row, recommend: false);

    /// <summary>获取平台推荐话题（B站按标题 + 分区；快手 / 头条要等上传完成后的上下文）</summary>
    [RelayCommand]
    private async Task RecommendTopicSuggestionsAsync(TargetRowViewModel? row) =>
        await FetchTopicSuggestionsAsync(row, recommend: true);

    /// <summary>把候选加进该平台的话题列表</summary>
    [RelayCommand]
    private void PickTopicSuggestion(TopicPickViewModel? pick)
    {
        if (pick is null || SelectedDraft is null) return;
        var row = TargetRows.FirstOrDefault(r => r.TopicSuggestions.Contains(pick));
        if (row is null) return;

        var source = pick.FromRecommend ? TopicRefSources.Recommend : TopicRefSources.Search;
        ApplyTopicToRow(row, TopicSelection.ToRef(pick.Suggestion, source));
        row.TopicStatusText = pick.Suggestion.HasPlatformId
            ? $"已添加「{pick.Suggestion.Name}」（带平台话题 ID）"
            : $"已添加「{pick.Suggestion.Name}」（没有话题 ID，发布时按纯文本处理）";
        row.TopicStatusIsError = false;
    }

    /// <summary>
    /// 话题写进目标：TopicRefs（结构化）+ Topics（旧字段桥接）+ 芯片 / 摘要 / 预检刷新。
    /// 不碰 draft.Topics —— 见本节顶部注释。
    /// </summary>
    private void ApplyTopicToRow(TargetRowViewModel row, TopicRef topic)
    {
        var name = TopicSelection.NormalizeName(topic.Name);
        if (name.Length == 0) return;
        var normalized = name == topic.Name ? topic : topic with { Name = name };

        row.Params.UpsertTopic(normalized);
        row.Params.Topics.RemoveAll(t => t.Name == normalized.Name);
        row.Params.Topics.Add(new ResolvedTopic(normalized.Name, normalized.Id, normalized.Name));

        row.RefreshTopicChips();
        row.RefreshSummary();
        NotifyKuaishouTopicLimit();
        if (SelectedDraft is { } draft) RunPreflight(draft);
    }

    /// <summary>
    /// 拉一批话题候选（搜索 / 推荐）。**永不抛** —— 失败把原因写进面板状态行，
    /// 不打断编辑（话题是辅助输入，接口挂了也不该拦发布）。
    /// </summary>
    private async Task FetchTopicSuggestionsAsync(TargetRowViewModel? row, bool recommend)
    {
        var draft = SelectedDraft;
        if (draft is null || row is null || row.IsTopicBusy) return;
        if (recommend ? !row.TopicSupportsRecommend : !row.TopicSupportsSearch) return;

        var keyword = TopicSelection.NormalizeName(row.TopicQuery);
        if (!recommend && keyword.Length == 0)
        {
            row.TopicStatusText = "先输入关键词，或点「获取平台推荐」";
            row.TopicStatusIsError = false;
            return;
        }

        // 缓存键：账号 + 平台 + 搜索/推荐 + 关键词/标题（2.6.7）。
        // 命中就**立刻出结果、连凭证接口都不调** —— 平台接口挂了/离线时照样能挑上一次的话题。
        // 同一个键再点一次 = 强制刷新（用户要最新推荐时不必等 10 分钟）。
        var cacheKey = TopicApiCache.TopicKey(
            row.Target.SessionId, row.Target.PlatformKey, recommend, keyword,
            recommend ? $"{draft.Title}\n{draft.Description}" : "");
        if (!string.Equals(row.CachedTopicKey, cacheKey, StringComparison.Ordinal)
            && TopicApiCache.Shared.TryGetTopics(cacheKey, out var cached))
        {
            row.SetTopicSuggestions(cached.Items, recommend);
            row.CachedTopicKey = cacheKey;
            row.TopicStatusText = cached.Items.Count > 0
                ? $"已显示 {cached.Items.Count} 个话题（10 分钟内缓存 · 再点一次强制刷新）"
                : "上次没有搜到话题（10 分钟内缓存 · 再点一次强制刷新，或直接手动输入）";
            row.TopicStatusIsError = false;
            return;
        }
        // 本次回网络：清掉「来自缓存」的记账
        row.CachedTopicKey = null;

        row.IsTopicBusy = true;
        row.TopicStatusText = recommend ? "正在获取平台推荐…" : "正在搜索平台话题…";
        row.TopicStatusIsError = false;

        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(row.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.TopicStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试；也可直接手动输入话题（不影响发布）";
                row.TopicStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = row.Target.SessionId,
            };

            // 小红书签名宿主先热、再起请求预算（冷启动几十秒；顺序反了会「永远搜不到」，
            // 与 PublishResolver.WarmPlatformAsync 同口径）
            if (row.Target.PlatformKey == PlatformNames.Xiaohongshu)
                await XhsSignerService.PrepareAsync(row.Target.SessionId, CancellationToken.None);

            var service = TopicServiceCatalog.CreateTopicService(row.Target.PlatformKey, options);
            var query = new TopicQuery
            {
                Keyword = keyword,
                Title = draft.Title,
                Description = draft.Description,
                Count = 20,
            };
            var credentials = new PlatformCredentials(creds.Cookie, creds.ExtraDataJson);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = recommend
                ? await service.RecommendAsync(credentials, query, cts.Token)
                : await service.SearchAsync(credentials, query, cts.Token);

            if (!result.Ok)
            {
                // 失败不丢上一次的候选 —— 用户还能从旧列表里挑。
                // 失败**不写缓存**：再点一次就是真重试（2.6.7）。
                row.TopicStatusText = (result.Error ?? "拉取失败，稍后重试")
                    + " —— 可直接手动输入话题，发布不受影响";
                row.TopicStatusIsError = true;
                return;
            }

            // 只缓存成功（失败缓存 = 重试也救不回来）
            TopicApiCache.Shared.SetTopics(cacheKey, result);
            row.SetTopicSuggestions(result.Items, recommend);
            row.TopicStatusText = result.Items.Count > 0
                ? recommend ? $"平台推荐 {result.Items.Count} 个话题" : $"找到 {result.Items.Count} 个话题"
                : recommend
                    ? "平台这次没有推荐话题 —— 可以直接手动输入"
                    : "没有搜到话题 —— 可以直接手动输入";
            row.TopicStatusIsError = false;
        }
        catch (OperationCanceledException)
        {
            row.TopicStatusText = "请求超时 —— 可直接手动输入话题（不影响发布），或点按钮重试";
            row.TopicStatusIsError = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 拉取话题失败 {Platform}", row.Target.PlatformKey);
            row.TopicStatusText = "拉取失败：" + ex.Message + " —— 可直接手动输入话题（不影响发布）";
            row.TopicStatusIsError = true;
        }
        finally
        {
            row.IsTopicBusy = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  左栏：全局「官方活动」区（2.6.9）
    //
    //  2026-11-06 用户要求「活动合起来」：每平台一行，不再散落在右侧平台卡里。
    //  活动值仍逐账号写 PlatformParams.Activities（平台侧对象，各平台独立）；
    //  一行内的选择 / 清除对该平台**所有账号**一起生效。
    //
    //  与话题块同一套分工：
    //    · 结构化快照写 Params.Activities（2.6.5 起 builder 读它）；
    //    · 桥接 Params.ActivityIds —— 2.6.5 之前抖音/快手 builder 读旧字段，
    //      不桥接的话「界面上选了活动、发布时静默不带」（最难查的一类 bug）；
    //    · 选中活动经 UpsertActivity 自动把活动关联话题并进 Params.TopicRefs，
    //      同一批话题再桥接进 Params.Topics（旧 ResolvedTopic）。
    //
    //  单选语义：点一张卡 = 替换该平台所有账号的活动；清理由芯片上的 × 负责。
    //  已结束的活动灰显、点了只给提示，绝不选上（过期活动发布会白跑）。
    // ═══════════════════════════════════════════════════════════════

    /// <summary>展开 / 收起某个平台的「官方活动」面板</summary>
    [RelayCommand]
    private void ToggleActivityPanel(PlatformActivityRowViewModel? row)
    {
        if (row is null) return;
        row.IsActivityPanelOpen = !row.IsActivityPanelOpen;
        if (row.IsActivityPanelOpen) row.RefreshActivityPickStates();
    }

    /// <summary>选择一个官方活动（单选；已结束的不让选；作用到该平台所有账号）</summary>
    [RelayCommand]
    private void PickActivity(ActivityCardViewModel? card)
    {
        if (card is null || SelectedDraft is not { } draft) return;
        var row = ActivityRows.FirstOrDefault(r => r.ActivityOptions.Contains(card));
        if (row is null) return;

        if (card.IsEnded)
        {
            row.ActivityStatusText = "该活动已结束，不能选择";
            row.ActivityStatusIsError = true;
            return;
        }

        var activity = TopicSelection.ToActivityRef(card.Activity);
        foreach (var target in row.Targets)
            ApplyActivityToTarget(target, activity);

        row.RefreshActivityChips();
        NotifyKuaishouTopicLimit();
        RunPreflight(draft);

        row.ActivityStatusText = activity.Topics.Count > 0
            ? $"已选「{activity.Name}」，自动带入 {activity.Topics.Count} 个话题"
            : $"已选「{activity.Name}」";
        row.ActivityStatusIsError = false;
    }

    /// <summary>把一个活动写进单个账号（替换其活动列表、桥接旧字段、带入关联话题）</summary>
    private static void ApplyActivityToTarget(TargetRowViewModel row, ActivityRef activity)
    {
        // 单选：替换该账号已选的活动（params.txt 可能导入过多个，用户手动点选即收敛成一个）。
        // 换选时先把旧活动自动带入的话题摘掉（新活动也带同名话题的保留）——
        // 否则 A 的话题会一直粘着，用户以为只选了 B。
        var keepTopics = new HashSet<string>(
            activity.Topics.Select(t => t.Name), StringComparer.Ordinal);
        RemoveActivityTopics(row, row.Params.Activities.SelectMany(a => a.Topics), keepTopics);

        row.Params.Activities.Clear();
        row.Params.UpsertActivity(activity);   // 关联话题自动进 TopicRefs
        row.Params.ActivityIds = [activity.Id]; // 旧字段桥接（2.6.5 之前的 builder 读它）
        foreach (var topic in activity.Topics)
            BridgeTopicToLegacy(row, topic);

        row.RefreshActivityChips();
        row.RefreshTopicChips();
        row.RefreshSummary();
    }

    /// <summary>
    /// 清除一个已选活动（作用到该平台所有账号）。活动自动带入的话题一并移除 ——
    /// B站的活动就挂在话题的 <c>mission_id</c> 上，只删活动不清话题 = 活动其实还在参加；
    /// 之后被用户手动改过来源（manual/search）的话题、或被其它已选活动引用的话题保留。
    /// </summary>
    [RelayCommand]
    private void ClearActivity(ActivityChipViewModel? chip)
    {
        if (chip is null || SelectedDraft is not { } draft) return;
        var row = ActivityRows.FirstOrDefault(r => r.ActivityChips.Contains(chip));
        if (row is null) return;

        var activity = chip.Activity;
        var removed = 0;
        foreach (var target in row.Targets)
            removed += RemoveActivityFromTarget(target, activity);

        row.RefreshActivityChips();
        NotifyKuaishouTopicLimit();
        RunPreflight(draft);

        row.ActivityStatusText = removed > 0
            ? $"已取消「{activity.Name}」，自动带入的 {removed} 个话题已一并移除"
            : $"已取消「{activity.Name}」";
        row.ActivityStatusIsError = false;
    }

    /// <summary>从单个账号摘掉一个活动；返回顺带移除的自动话题数</summary>
    private static int RemoveActivityFromTarget(TargetRowViewModel row, ActivityRef activity)
    {
        row.Params.ActivityIds.RemoveAll(id => id == activity.Id);
        // 仍被其它已选活动引用的同名话题要保留（多个活动共用赛道话题）
        var keepTopics = new HashSet<string>(
            row.Params.Activities
                .Where(a => a.Id != activity.Id)
                .SelectMany(a => a.Topics)
                .Select(t => t.Name),
            StringComparer.Ordinal);
        var removed = RemoveActivityTopics(row, activity.Topics, keepTopics);
        row.Params.Activities.RemoveAll(a => a.Id == activity.Id);

        row.RefreshActivityChips();
        row.RefreshTopicChips();
        row.RefreshSummary();
        return removed;
    }

    /// <summary>拉取平台官方活动列表（搜索 / 推荐同一入口）；失败不拦发布</summary>
    [RelayCommand]
    private async Task FetchActivitiesAsync(PlatformActivityRowViewModel? row)
    {
        var draft = SelectedDraft;
        if (draft is null || row is null || row.IsActivityBusy) return;

        // 合并行没有自己的会话：用该平台第一个可用账号的凭证拉列表
        // （活动列表是平台级的，同平台各账号看到的是同一批）。
        var target = row.Targets.FirstOrDefault(t => t.Target.SessionId != Guid.Empty && t.IsLoggedIn)
            ?? row.Targets.FirstOrDefault(t => t.Target.SessionId != Guid.Empty)
            ?? row.Targets.FirstOrDefault();
        if (target is null) return;

        // 与话题块同一套缓存口径（2.6.7）：账号 + 平台 + 标题（B站按标题聚合活动）。
        // 命中不取凭证、不打平台接口；同键再点一次 = 强制刷新。
        var cacheKey = TopicApiCache.ActivityKey(
            target.Target.SessionId, row.PlatformKey, draft.Title);
        if (!string.Equals(row.CachedActivityKey, cacheKey, StringComparison.Ordinal)
            && TopicApiCache.Shared.TryGetActivities(cacheKey, out var cached))
        {
            row.SetActivityOptions(cached.Items);
            row.CachedActivityKey = cacheKey;
            row.ActivityStatusText = cached.Items.Count > 0
                ? $"已显示 {cached.Items.Count} 个活动（10 分钟内缓存 · 再点一次强制刷新）"
                : "上次没有拉到活动（10 分钟内缓存 · 再点一次强制刷新；活动不影响发布）";
            row.ActivityStatusIsError = false;
            // 封面按 URL 缓存（ActivityCoverLoader），命中缓存重建卡片后补图不花网络
            _ = row.LoadActivityCoversAsync();
            return;
        }
        row.CachedActivityKey = null;

        row.IsActivityBusy = true;
        row.ActivityStatusText = "正在拉取官方活动…";
        row.ActivityStatusIsError = false;

        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(target.Target.SessionId);
            if (creds?.Cookie is null)
            {
                row.ActivityStatusText = "账号凭证不可用 —— 到「平台账号」重新登录后再试；活动不影响发布";
                row.ActivityStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
                SessionId = target.Target.SessionId,
            };

            // 小红书签名宿主先热、再起请求预算（与话题块同口径，顺序反了会「永远拉不到」）
            if (row.PlatformKey == PlatformNames.Xiaohongshu)
                await XhsSignerService.PrepareAsync(target.Target.SessionId, CancellationToken.None);

            var service = TopicServiceCatalog.CreateActivityService(row.PlatformKey, options);
            var query = new ActivityQuery { Title = draft.Title, Count = 50 };
            var credentials = new PlatformCredentials(creds.Cookie, creds.ExtraDataJson);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = await service.ListAsync(credentials, query, cts.Token);

            if (!result.Ok)
            {
                // 失败不丢上一次的候选 —— 用户还能从旧列表里挑；失败不写缓存，再点 = 真重试
                row.ActivityStatusText = (result.Error ?? "拉取失败，稍后重试")
                    + " —— 活动不影响发布，可稍后重试";
                row.ActivityStatusIsError = true;
                return;
            }

            TopicApiCache.Shared.SetActivities(cacheKey, result);
            row.SetActivityOptions(result.Items);
            row.ActivityStatusText = result.Items.Count > 0
                ? $"找到 {result.Items.Count} 个活动（已结束的灰显，不可选）"
                : "这次没有拉到活动 —— 活动不影响发布，可稍后重试";
            row.ActivityStatusIsError = false;

            // 封面后台加载（失败只显示首字占位）；不 await，面板先出来
            _ = row.LoadActivityCoversAsync();
        }
        catch (OperationCanceledException)
        {
            row.ActivityStatusText = "请求超时 —— 活动不影响发布，可稍后重试";
            row.ActivityStatusIsError = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 拉取官方活动失败 {Platform}", row.PlatformKey);
            row.ActivityStatusText = "拉取失败：" + ex.Message + " —— 活动不影响发布，可稍后重试";
            row.ActivityStatusIsError = true;
        }
        finally
        {
            row.IsActivityBusy = false;
        }
    }

    /// <summary>
    /// 摘掉活动自动带入的话题（活动被清除 / 换成另一个活动时调），返回实际摘掉几个。
    ///
    /// <paramref name="activityTopics"/> = 要摘的话题集合（清一个活动就传它的；
    /// 换选时传所有旧活动的并集 —— 旧活动全部要被替换）。
    /// 两条保留规则：
    ///   · <paramref name="keepNames"/> 里的名字 —— 新活动也带同名话题，别刚加上又删、
    ///     或仍被其它已选活动引用（多个活动共用赛道话题）；
    ///   · 用户后来手动改过来源（manual/search/recommend）的 —— 尊重用户选择。
    /// B站的活动就挂在话题的 <c>mission_id</c> 上，所以清活动必须连话题一起摘，
    /// 否则界面显示「已取消活动」、实际还在参加。
    /// </summary>
    private static int RemoveActivityTopics(
        TargetRowViewModel row, IEnumerable<TopicRef> activityTopics, ISet<string>? keepNames)
    {
        var removed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topic in activityTopics)
        {
            if (!seen.Add(topic.Name)) continue;
            if (keepNames?.Contains(topic.Name) == true) continue;
            if (!row.Params.TopicRefs.Any(t => t.Name == topic.Name
                    && t.Source == TopicRefSources.Activity))
                continue;
            row.Params.TopicRefs.RemoveAll(t => t.Name == topic.Name);
            row.Params.Topics.RemoveAll(t => t.Name == topic.Name);
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// 话题桥接进旧字段（2.6.5 之前 builder 读 <c>PlatformParams.Topics</c>）。
    /// 与 <see cref="ApplyTopicToRow"/> 同一口径：同名先删再加，避免旧 ID 残留。
    /// </summary>
    private static void BridgeTopicToLegacy(TargetRowViewModel row, TopicRef topic)
    {
        row.Params.Topics.RemoveAll(t => t.Name == topic.Name);
        row.Params.Topics.Add(new ResolvedTopic(topic.Name, topic.Id, topic.Name));
    }

    /// <summary>品牌色兜底表（真实色值来自 /api/config/platforms，这里给首屏兜底）</summary>
    private string ChannelBrandHex(string platformKey) =>
        GlobalTargets.FirstOrDefault(t => t.PlatformKey == platformKey)?.BrandHex
        ?? platformKey switch
        {
            PlatformNames.Douyin => "#FF0049",
            PlatformNames.Kuaishou => "#FF4906",
            PlatformNames.Xiaohongshu => "#FE2C55",
            PlatformNames.WechatVideo => "#07C160",
            _ => "#6C5CE7",
        };

    // ═══════════════════════════════════════════════════════════════
    //  内容声明（全局单选 + 二级随选中项出现）
    // ═══════════════════════════════════════════════════════════════

    public ObservableCollection<DeclarationOptionViewModel> DeclarationOptions { get; }

    // 标签取自 DeclarationMapping（唯一来源）—— params.txt 的取值词表用的也是它，
    // 两边共用一份文案，不会出现「编辑器叫『内容为转载』、params 里必须写别的」。
    // 顺序 = 枚举声明顺序（None → SelfShot），与原手写列表一致。
    private static ObservableCollection<DeclarationOptionViewModel> BuildDeclarationOptions() =>
        [.. Enum.GetValues<DeclarationKind>()
            .Select(k => new DeclarationOptionViewModel
            {
                Kind = k,
                Label = DeclarationMapping.DeclarationLabel(k),
            })];

    /// <summary>单选：点一个自动取消其余（设计文档 §4.3 硬规则一 —— 语义互斥）</summary>
    [RelayCommand]
    private void SelectDeclaration(DeclarationKind kind)
    {
        var draft = SelectedDraft;
        if (draft is null) return;

        draft.Declaration.Kind = kind;
        SyncDeclarationSelection();
        RefreshDeclarationSecondary();
        // 头条卡的「跟随全局」预览与「自行拍摄」二级要跟着全局语义变（A1）
        foreach (var row in TargetRows) row.SetGlobalDeclaration(kind);
        NotifyEditorComputed();   // 「转载/自行拍摄」的二级字段由 Kind 算出来，得跟着播报
        RunPreflight(draft);
        RefreshSummaryCounts();
    }

    private void SyncDeclarationSelection()
    {
        var kind = SelectedDraft?.Declaration.Kind ?? DeclarationKind.None;
        foreach (var o in DeclarationOptions)
        {
            if (o.Kind != kind || !o.IsSelected) o.IsSelected = o.Kind == kind;
            // 平台能力提示：所有勾选目标都不支持时才提示（部分支持就不该说「不支持」）
            var targets = SelectedDraft?.EnabledTargets.Select(t => t.PlatformKey).ToList() ?? [];
            o.UnsupportedHint = targets.Count > 0 && targets.All(p => !DeclarationMapping.Supports(p, o.Kind))
                ? DeclarationMapping.UnsupportedReason(targets[0], o.Kind)
                : null;
        }
        OnPropertyChanged(nameof(DeclarationMatrixText));
    }

    /// <summary>能力矩阵说明行（让「为什么这个平台没有这一项」在 UI 上说得出口）</summary>
    public string DeclarationMatrixText
    {
        get
        {
            var targets = SelectedDraft?.EnabledTargets.Select(t => t.PlatformKey).Distinct().ToList() ?? [];
            if (targets.Count == 0) return "";
            var unsupported = DeclarationOptions
                .Where(o => o.Kind != DeclarationKind.None && o.UnsupportedHint is not null)
                .Select(o => o.Label)
                .ToList();
            return unsupported.Count == 0
                ? "所选平台都支持全部声明"
                : $"以下声明在你选中的平台里没有对应项：{string.Join("、", unsupported)}";
        }
    }

    // ═══ 二级区（只有「转载」「自行拍摄」有）═══

    /// <summary>当前语义有没有二级要填（没有就整块不出现，只给一句说明）</summary>
    [ObservableProperty] private bool _showDeclarationSecondary;
    [ObservableProperty] private string _declarationSecondaryHint = "";

    partial void OnShowDeclarationSecondaryChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSecondaryHintOnly));
    }

    /// <summary>没有二级可填时显示提示行（**绝不能把几套二级同时摊开**，§4.3 硬规则二）</summary>
    public bool ShowSecondaryHintOnly => !ShowDeclarationSecondary;

    private void RefreshDeclarationSecondary()
    {
        var kind = SelectedDraft?.Declaration.Kind ?? DeclarationKind.None;
        ShowDeclarationSecondary = ContentDeclaration.HasSecondary(kind);

        DeclarationSecondaryHint = kind switch
        {
            DeclarationKind.Repost => "转载要在各平台注明来源 —— 每个平台填的东西不同，下面按平台分组",
            DeclarationKind.SelfShot => "「自行拍摄」需要补充拍摄信息 —— 各平台要求不同",
            _ => "该声明无需补充信息 —— 二级字段只在选了「转载」或「自行拍摄」时才出现",
        };
    }

    // ═══ 二级字段的可编辑性（按平台 + 按语义）═══

    /// <summary>抖音转载二级：来源（抖音作品 / 站外）+ 选站内作品</summary>
    public bool ShowDouyinRepostSecondary =>
        ShowDeclarationSecondary && SelectedDraft?.Declaration.Kind == DeclarationKind.Repost
        && SelectedDraft.EnabledTargets.Any(t => t.PlatformKey == PlatformNames.Douyin);

    /// <summary>快手转载二级：来源平台 + 地区 + 时间</summary>
    public bool ShowKuaishouRepostSecondary =>
        ShowDeclarationSecondary && SelectedDraft?.Declaration.Kind == DeclarationKind.Repost
        && SelectedDraft.EnabledTargets.Any(t => t.PlatformKey == PlatformNames.Kuaishou);

    /// <summary>小红书二级：转载填来源；自行拍摄填拍摄地点 + 时间</summary>
    public bool ShowXhsSecondary =>
        ShowDeclarationSecondary
        && SelectedDraft.EnabledTargets.Any(t => t.PlatformKey == PlatformNames.Xiaohongshu);

    /// <summary>视频号二级：转载填来源（选填）；自行拍摄填拍摄时间 + 国省市</summary>
    public bool ShowWechatSecondary =>
        ShowDeclarationSecondary
        && SelectedDraft.EnabledTargets.Any(t => t.PlatformKey == PlatformNames.WechatVideo);

    public bool IsRepost => SelectedDraft?.Declaration.Kind == DeclarationKind.Repost;
    public bool IsSelfShot => SelectedDraft?.Declaration.Kind == DeclarationKind.SelfShot;

    /// <summary>抖音转载来源切换（true = 抖音作品，需要选具体作品）</summary>
    public bool DouyinRepostFromInside
    {
        get => SelectedDraft?.Declaration.DouyinRepostFromInside ?? false;
        set
        {
            if (SelectedDraft is null || SelectedDraft.Declaration.DouyinRepostFromInside == value) return;
            SelectedDraft.Declaration.DouyinRepostFromInside = value;
            if (!value) ShowDouyinRepostPicker = false;   // 切回「站外」时把作品面板收起
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowDouyinRepostItemPicker));
        }
    }

    public bool ShowDouyinRepostItemPicker => DouyinRepostFromInside;

    /// <summary>快手转载来源平台（1=快手 / 2=其他）</summary>
    public int KuaishouRepostPlatform
    {
        get => SelectedDraft?.Declaration.KuaishouRepostPlatform ?? 2;
        set
        {
            if (SelectedDraft is null) return;
            SelectedDraft.Declaration.KuaishouRepostPlatform = value;
            OnPropertyChanged();
        }
    }

    /// <summary>通用转载来源文本（小红书 / 视频号用）</summary>
    public string RepostSourceText
    {
        get => SelectedDraft?.Declaration.RepostSource ?? "";
        set
        {
            if (SelectedDraft is null) return;
            SelectedDraft.Declaration.RepostSource = value;
            OnPropertyChanged();
        }
    }

    /// <summary>快手转载地区（**省市区文本，不是编码**）</summary>
    public string KuaishouRepostLocation
    {
        get => SelectedDraft?.Declaration.KuaishouRepostLocation ?? "";
        set
        {
            if (SelectedDraft is null) return;
            SelectedDraft.Declaration.KuaishouRepostLocation = value;
            OnPropertyChanged();
        }
    }

    /// <summary>转载发生时间（快手存**毫秒**，其余用 DateTime）</summary>
    public DateTimeOffset? RepostTime
    {
        get => SelectedDraft?.Declaration.KuaishouRepostTimeMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        set
        {
            if (SelectedDraft is null) return;
            SelectedDraft.Declaration.KuaishouRepostTimeMs = value?.ToUnixTimeMilliseconds();
            OnPropertyChanged();
        }
    }

    /// <summary>自行拍摄时间</summary>
    public DateTimeOffset? ShootTime
    {
        get => SelectedDraft?.Declaration.ShootTime is { } t ? new DateTimeOffset(t) : null;
        set
        {
            if (SelectedDraft is null) return;
            SelectedDraft.Declaration.ShootTime = value?.DateTime;
            OnPropertyChanged();
        }
    }

    /// <summary>快手转载发生时间的日期部分（DatePicker 只给日期）</summary>
    public DateTimeOffset? RepostDate
    {
        get => RepostTime?.Date is { } d ? new DateTimeOffset(d) : null;
        set
        {
            if (value is null) { RepostTime = null; return; }
            var time = RepostTime?.TimeOfDay ?? TimeSpan.Zero;
            RepostTime = new DateTimeOffset(value.Value.Date.Add(time));
            OnPropertyChanged();
        }
    }

    /// <summary>自行拍摄时间的日期部分（小红书 / 视频号共用同一个 ShootTime）</summary>
    public DateTimeOffset? ShootDate
    {
        get => ShootTime?.Date is { } d ? new DateTimeOffset(d) : null;
        set
        {
            if (value is null) { ShootTime = null; return; }
            var time = ShootTime?.TimeOfDay ?? TimeSpan.Zero;
            ShootTime = new DateTimeOffset(value.Value.Date.Add(time));
            OnPropertyChanged();
        }
    }

    /// <summary>快手来源平台下拉序号（0=快手 / 1=其他平台，对应协议值 1 / 2）</summary>
    public int KuaishouRepostPlatformIndex
    {
        get => KuaishouRepostPlatform == 1 ? 0 : 1;
        set
        {
            KuaishouRepostPlatform = value == 0 ? 1 : 2;
            OnPropertyChanged();
        }
    }

    // ═══ 视频号「自行拍摄」的地区（平台接口树：国家 → 省 → 市）═══
    //
    // 协议要的是整数码（shootInfo.countryCode / provinceCode / cityCode），以前是三个手填数字框
    // —— 没人记得住 1017 / 10160 / 100032。平台接口 finder_get_object_tag_list 的 tagType=5
    // 直接返回整棵地区树（页面「拍摄地点」选择器同一个数据源），所以改成级联选择。
    // 数据源 = 当前条目第一个启用的视频号账号（与话题 / 位置解析同一条惯例）。

    /// <summary>地区面板是否展开（编辑器级：一次只编辑一条草稿）</summary>
    [ObservableProperty] private bool _isWechatShootRegionPanelOpen;

    [ObservableProperty] private bool _isWechatShootRegionBusy;

    /// <summary>地区接口的状态行（加载结果 / 失败原因 / 层级提示）</summary>
    [ObservableProperty] private string _wechatShootRegionStatusText = "";
    [ObservableProperty] private bool _wechatShootRegionStatusIsError;

    [ObservableProperty] private IReadOnlyList<WechatShootRegionOption> _wechatShootCountries = [];
    [ObservableProperty] private IReadOnlyList<WechatShootRegionOption> _wechatShootProvinces = [];
    [ObservableProperty] private IReadOnlyList<WechatShootRegionOption> _wechatShootCities = [];

    /// <summary>
    /// 三个下拉的选中序号（-1 = 未选）。级联只绑 <c>SelectedIndex</c> ——
    /// Avalonia 换掉 ItemsSource 会把 SelectedItem 清空，绑它选择会来回丢。
    /// </summary>
    [ObservableProperty] private int _wechatShootCountryIndex = -1;
    [ObservableProperty] private int _wechatShootProvinceIndex = -1;
    [ObservableProperty] private int _wechatShootCityIndex = -1;

    /// <summary>回填已存编码时抑制「写回草稿」（否则会把旧市码挂到新省下面）</summary>
    private bool _wechatShootRegionRestoring;

    public bool HasWechatShootRegionStatus => WechatShootRegionStatusText.Length > 0;

    partial void OnWechatShootRegionStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasWechatShootRegionStatus));

    public bool HasWechatShootProvinces => WechatShootProvinces.Count > 0;
    partial void OnWechatShootProvincesChanged(IReadOnlyList<WechatShootRegionOption> value) =>
        OnPropertyChanged(nameof(HasWechatShootProvinces));

    public bool HasWechatShootCities => WechatShootCities.Count > 0;
    partial void OnWechatShootCitiesChanged(IReadOnlyList<WechatShootRegionOption> value) =>
        OnPropertyChanged(nameof(HasWechatShootCities));

    /// <summary>已经选了地区（有国家码）——「清除拍摄地点」只在这时出现</summary>
    public bool HasWechatShootRegionPicked =>
        SelectedDraft?.Declaration.ShootRegion is { CountryCode: not null };

    /// <summary>
    /// 已选地区的中文展示；地区树还没拉过时退化成整数码（名称未知，不瞎编）。
    /// </summary>
    public string WechatShootRegionPickedText
    {
        get
        {
            var r = SelectedDraft?.Declaration.ShootRegion;
            if (r is null || (r.CountryCode is null && r.ProvinceCode is null && r.CityCode is null))
                return "尚未选择拍摄地点";
            var parts = new List<string>();
            if (r.CountryCode is int c) parts.Add(RegionLabel(WechatShootCountries, c, "国家"));
            if (r.ProvinceCode is int p) parts.Add(RegionLabel(WechatShootProvinces, p, "省"));
            if (r.CityCode is int ci) parts.Add(RegionLabel(WechatShootCities, ci, "市"));
            return string.Join(" · ", parts);
        }
    }

    private static string RegionLabel(IReadOnlyList<WechatShootRegionOption> list, int code, string level)
    {
        var hit = list.FirstOrDefault(o => o.Code == code);
        return hit is null ? $"{level}码 {code}" : $"{hit.Display}（{code}）";
    }

    // ── 级联：换国家清省 / 市，换省清市；选到哪级就写到哪级 ──

    partial void OnWechatShootCountryIndexChanged(int value)
    {
        var country = ItemAt(WechatShootCountries, value);
        WechatShootProvinces = country?.Sub ?? [];
        WechatShootProvinceIndex = -1;   // 触发省级处理器（清市）
        if (!_wechatShootRegionRestoring)
        {
            ApplyWechatShootRegion(country, null);
            UpdateWechatShootRegionHint();
        }
        NotifyWechatShootRegionChanged();
    }

    partial void OnWechatShootProvinceIndexChanged(int value)
    {
        var province = ItemAt(WechatShootProvinces, value);
        WechatShootCities = province?.Sub ?? [];
        WechatShootCityIndex = -1;
        if (!_wechatShootRegionRestoring)
        {
            ApplyWechatShootRegion(null, province);
            UpdateWechatShootRegionHint();
        }
        NotifyWechatShootRegionChanged();
    }

    partial void OnWechatShootCityIndexChanged(int value)
    {
        var city = ItemAt(WechatShootCities, value);
        if (!_wechatShootRegionRestoring)
        {
            ApplyWechatShootRegion(null, null, city);
            UpdateWechatShootRegionHint();
        }
        NotifyWechatShootRegionChanged();
    }

    private void NotifyWechatShootRegionChanged()
    {
        OnPropertyChanged(nameof(WechatShootRegionPickedText));
        OnPropertyChanged(nameof(HasWechatShootRegionPicked));
    }

    private void ApplyWechatShootRegion(
        WechatShootRegionOption? country, WechatShootRegionOption? province,
        WechatShootRegionOption? city = null)
    {
        if (SelectedDraft is null) return;
        var cur = SelectedDraft.Declaration.ShootRegion ?? new RegionRef();
        SelectedDraft.Declaration.ShootRegion = country is not null
            ? new RegionRef(country.Code, null, null)
            : province is not null
                ? cur with { ProvinceCode = province.Code, CityCode = null }
                : city is not null
                    ? cur with { CityCode = city.Code }
                    : cur;
    }

    /// <summary>
    /// 层级提示：某个国家 / 省在平台列表里没有下级时，明说「只下发到哪一级」
    /// （直辖市没有城市子项 —— 这些行的市码按 0 下发，与旧手填口径一致）。
    /// </summary>
    private void UpdateWechatShootRegionHint()
    {
        if (_wechatShootRegionRestoring || WechatShootCountries.Count == 0) return;

        var country = ItemAt(WechatShootCountries, WechatShootCountryIndex);
        if (country is null)
        {
            WechatShootRegionStatusText = "";
            WechatShootRegionStatusIsError = false;
            return;
        }
        if (WechatShootProvinces.Count == 0)
        {
            WechatShootRegionStatusText = "该国家/地区在平台列表里没有下级省份 —— 只下发国家码（省 / 市按 0）";
            WechatShootRegionStatusIsError = false;
            return;
        }
        var province = ItemAt(WechatShootProvinces, WechatShootProvinceIndex);
        if (province is not null && WechatShootCities.Count == 0)
        {
            WechatShootRegionStatusText = "该省份在平台列表里没有下级城市 —— 下发国家码 + 省码（市按 0）";
            WechatShootRegionStatusIsError = false;
            return;
        }
        WechatShootRegionStatusText = "";
        WechatShootRegionStatusIsError = false;
    }

    /// <summary>展开 / 收起拍摄地区面板；首次展开自动拉列表。</summary>
    [RelayCommand]
    private async Task ToggleWechatShootRegionPanelAsync()
    {
        if (IsWechatShootRegionPanelOpen)
        {
            IsWechatShootRegionPanelOpen = false;
            return;
        }
        IsWechatShootRegionPanelOpen = true;
        if (WechatShootCountries.Count == 0) await LoadWechatShootRegionsAsync();
    }

    /// <summary>拉取平台地区树（finder_get_object_tag_list 的 tagType=5）。**永不抛**。</summary>
    [RelayCommand]
    private async Task LoadWechatShootRegionsAsync()
    {
        if (IsWechatShootRegionBusy) return;
        var draft = SelectedDraft;
        if (draft is null) return;

        // 与话题 / 位置解析同一惯例：用该条目里第一个启用的视频号账号取凭证
        var wx = draft.EnabledTargets.FirstOrDefault(t => t.PlatformKey == PlatformNames.WechatVideo);
        if (wx is null)
        {
            WechatShootRegionStatusText = "本条目没有勾选视频号账号 —— 拉取不到地区列表";
            WechatShootRegionStatusIsError = true;
            return;
        }

        IsWechatShootRegionBusy = true;
        WechatShootRegionStatusText = "正在拉取视频号「拍摄地点」列表…";
        WechatShootRegionStatusIsError = false;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(wx.SessionId);
            if (creds?.Cookie is null)
            {
                WechatShootRegionStatusText = "视频号账号凭证不可用 —— 到「平台账号」重新登录后再试";
                WechatShootRegionStatusIsError = true;
                return;
            }

            var extra = CredentialFields.Extra(creds.ExtraDataJson);
            var deviceId = extra.GetValueOrDefault("deviceId")?.ToString()
                ?? extra.GetValueOrDefault("device_id")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                WechatShootRegionStatusText = "账号凭证里没有设备标识（deviceId）—— 到「平台账号」重新登录后再试";
                WechatShootRegionStatusIsError = true;
                return;
            }

            var options = new PlatformOptions
            {
                SessionId = wx.SessionId,
                Environment = PublishResolver.ParseEnv(creds.ExtraDataJson),
            };
            var svc = new WechatVideoRegionService(options)
            {
                FinderId = CredentialFields.LocalStorageField(creds.LocalStorageJson, "finder_username"),
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (countries, error) = await svc.GetRegionsAsync(creds.Cookie, deviceId, cts.Token);
            if (error is not null)
            {
                WechatShootRegionStatusText = error;
                WechatShootRegionStatusIsError = true;
                return;
            }

            WechatShootCountries = [.. countries.Select(MapWechatShootRegion)];
            RestoreWechatShootRegionSelection();
            WechatShootRegionStatusText = $"已加载 {countries.Count} 个国家/地区（拍摄地点来自平台接口）";
            WechatShootRegionStatusIsError = false;
        }
        catch (OperationCanceledException)
        {
            WechatShootRegionStatusText = "拉取超时 —— 可重试（不影响已填充的编码）";
            WechatShootRegionStatusIsError = true;
        }
        catch (Exception ex)
        {
            WechatShootRegionStatusText = "拉取失败：" + ex.Message + " —— 稍后重试";
            WechatShootRegionStatusIsError = true;
            Log.Warning(ex, "[Publish] 视频号拍摄地区列表拉取失败");
        }
        finally
        {
            IsWechatShootRegionBusy = false;
        }
    }

    /// <summary>把当前草稿里已存的编码回填到三个下拉（列表刷新 / 换条目时用）。</summary>
    private void RestoreWechatShootRegionSelection()
    {
        var r = SelectedDraft?.Declaration.ShootRegion;
        _wechatShootRegionRestoring = true;
        try
        {
            // 强制先落 -1 再落目标值 —— 同值 setter 不触发回调，级联列表会停在旧内容
            WechatShootCountryIndex = -1;
            WechatShootCountryIndex = r?.CountryCode is int cc ? IndexOfRegion(WechatShootCountries, cc) : -1;
            WechatShootProvinceIndex = r?.ProvinceCode is int pc ? IndexOfRegion(WechatShootProvinces, pc) : -1;
            WechatShootCityIndex = r?.CityCode is int cit ? IndexOfRegion(WechatShootCities, cit) : -1;
        }
        finally { _wechatShootRegionRestoring = false; }
        NotifyWechatShootRegionChanged();
    }

    /// <summary>清空拍摄地区（三个下拉一起回到未选）。</summary>
    [RelayCommand]
    private void ClearWechatShootRegion()
    {
        if (SelectedDraft is null) return;
        SelectedDraft.Declaration.ShootRegion = null;

        _wechatShootRegionRestoring = true;
        try
        {
            WechatShootCountryIndex = -1;
            WechatShootProvinceIndex = -1;
            WechatShootCityIndex = -1;
        }
        finally { _wechatShootRegionRestoring = false; }

        WechatShootRegionStatusText = "";
        WechatShootRegionStatusIsError = false;
        NotifyWechatShootRegionChanged();
    }

    private static WechatShootRegionOption? ItemAt(IReadOnlyList<WechatShootRegionOption> list, int index) =>
        index >= 0 && index < list.Count ? list[index] : null;

    private static int IndexOfRegion(IReadOnlyList<WechatShootRegionOption> list, int code)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Code == code) return i;
        return -1;
    }

    private static WechatShootRegionOption MapWechatShootRegion(WechatVideoRegion r) =>
        new(r.Code, r.Name, [.. r.Sub.Select(MapWechatShootRegion)]);

    // ═══ 小红书「自行拍摄」地点（独立于「位置」的第二个地点概念）═══

    [ObservableProperty] private string _xhsShootPoiQuery = "";

    /// <summary>已选拍摄地点的展示文案</summary>
    public string XhsShootPoiText =>
        SelectedDraft?.Declaration.ShootPoi is { } p ? $"已选：{p.Name}" : "尚未选择拍摄地点";

    /// <summary>拍摄地点也走联邦解析，但只问小红书一家 —— 它是小红书独有的概念</summary>
    [RelayCommand]
    private async Task SearchXhsShootPoiAsync()
    {
        var draft = SelectedDraft;
        var keyword = XhsShootPoiQuery.Trim();
        if (draft is null || keyword.Length == 0) return;

        var xhs = draft.EnabledTargets.FirstOrDefault(t => t.PlatformKey == PlatformNames.Xiaohongshu);
        if (xhs is null)
        {
            SubmitMessage = "没有选中的小红书账号，无法搜索拍摄地点";
            return;
        }

        var rows = await _resolver.ResolveLocationAsync(keyword,
            [new ResolutionTarget(xhs.SessionId, xhs.PlatformKey, xhs.AccountLabel,
                ChannelBrandHex(xhs.PlatformKey), PlatformNames.GetDisplayName(xhs.PlatformKey))]);

        var hit = rows.FirstOrDefault(r => r.Found)?.Value;
        if (hit is null)
        {
            SubmitMessage = "没找到这个拍摄地点";
            return;
        }
        draft.Declaration.ShootPoi = hit;
        OnPropertyChanged(nameof(XhsShootPoiText));
    }

    // ═══ 抖音「转载」的站内作品选择 ═══

    /// <summary>作品选择面板展开态（项目惯例：原地展开，不弹窗；老版是独立弹窗，语义等价）</summary>
    [ObservableProperty] private bool _showDouyinRepostPicker;

    /// <summary>作品列表加载中</summary>
    [ObservableProperty] private bool _isLoadingRepostItems;

    /// <summary>还有下一页（控制「加载更多」）</summary>
    [ObservableProperty] private bool _repostItemsHasMore;

    public ObservableCollection<DouyinRepostItemRow> DouyinRepostItems { get; } = [];

    private string _repostCursor = "0";

    /// <summary>
    /// 选中作品的标题 —— **只瞬态保存、不落盘**：展示字段不能进 ContentDeclaration
    /// （它会被原样塞进各平台的 ExtraDataJson 发到服务端，属 wire 契约）；重启后回落显示作品 ID。
    /// </summary>
    private string? _douyinRepostTitle;

    /// <summary>抖音站内作品 ID（= 转载 payload 的 from_item_id，随草稿落盘）</summary>
    public string DouyinRepostItemId
    {
        get => SelectedDraft?.Declaration.DouyinRepostItemId ?? "";
        set
        {
            if (SelectedDraft is null) return;
            SelectedDraft.Declaration.DouyinRepostItemId =
                string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DouyinRepostPickedText));
            OnPropertyChanged(nameof(HasDouyinRepostItem));
        }
    }

    public bool HasDouyinRepostItem => !string.IsNullOrWhiteSpace(DouyinRepostItemId);

    public string DouyinRepostPickedText =>
        HasDouyinRepostItem
            ? $"已选：{_douyinRepostTitle ?? $"作品 {DouyinRepostItemId}"}"
            : "尚未选择作品";

    /// <summary>打开 / 收起作品列表；打开时加载第一页（20 条，游标翻页 —— 与老版选择器同取数）</summary>
    [RelayCommand]
    private async Task PickDouyinRepostItem()
    {
        if (SelectedDraft is null) return;

        if (ShowDouyinRepostPicker)
        {
            ShowDouyinRepostPicker = false;
            return;
        }

        SubmitMessage = "";
        DouyinRepostItems.Clear();
        _repostCursor = "0";
        RepostItemsHasMore = false;
        ShowDouyinRepostPicker = true;
        await LoadDouyinRepostPageAsync(refresh: true);
    }

    [RelayCommand]
    private async Task LoadMoreDouyinRepostAsync()
    {
        if (IsLoadingRepostItems || !RepostItemsHasMore) return;
        await LoadDouyinRepostPageAsync(refresh: false);
    }

    private async Task LoadDouyinRepostPageAsync(bool refresh)
    {
        var draft = SelectedDraft;
        if (draft is null) return;

        // 用该条目里第一个启用的抖音账号取凭证（与话题/位置解析同一惯例）
        var dy = draft.EnabledTargets.FirstOrDefault(t => t.PlatformKey == PlatformNames.Douyin);
        if (dy is null)
        {
            SubmitMessage = "没有选中的抖音账号，无法读取作品列表";
            ShowDouyinRepostPicker = false;
            return;
        }

        IsLoadingRepostItems = true;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(dy.SessionId);
            if (creds?.Cookie is null)
            {
                SubmitMessage = "抖音账号凭证不可用，请到「平台账号」重新登录";
                ShowDouyinRepostPicker = false;
                return;
            }

            var extra = CredentialFields.Extra(creds.ExtraDataJson);
            var msToken = extra.GetValueOrDefault("msToken")?.ToString();
            var aBogus = extra.GetValueOrDefault("aBogus")?.ToString();

            // Environment 快照要带上：a_bogus 与 UA 指纹绑定（与 PublishPipeline 同一取法）
            var options = new PlatformOptions
            {
                SessionId = dy.SessionId,
                Environment = CredentialFields.EnvironmentSnapshot(creds.ExtraDataJson),
            };
            var result = await new DouyinMediaService(options)
                .ListHistoryAsync(creds.Cookie, refresh ? "0" : _repostCursor, 20, msToken, aBogus);

            if (result.StatusCode != 0)
            {
                // 老版这里只会显示「暂无作品」—— 实际多半是凭证过期，新版把话说清楚
                SubmitMessage = "作品列表读取失败（多半是登录凭证过期），请到「平台账号」重新登录抖音";
                if (refresh) ShowDouyinRepostPicker = false;
                return;
            }

            if (refresh) DouyinRepostItems.Clear();
            foreach (var item in result.Items)
                DouyinRepostItems.Add(new DouyinRepostItemRow(item));
            _repostCursor = result.MaxCursor;
            RepostItemsHasMore = result.HasMore;

            if (refresh && DouyinRepostItems.Count == 0)
            {
                SubmitMessage = "这个抖音账号还没有已发布的作品";
                ShowDouyinRepostPicker = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 抖音作品列表读取失败");
            SubmitMessage = "作品列表读取失败：" + ex.Message;
            if (refresh) ShowDouyinRepostPicker = false;
        }
        finally { IsLoadingRepostItems = false; }
    }

    /// <summary>选中一条作品作为转载来源（写 from_item_id）</summary>
    [RelayCommand]
    private void SelectDouyinRepostItem(DouyinRepostItemRow? row)
    {
        if (SelectedDraft is null || row is null) return;

        SelectedDraft.Declaration.DouyinRepostItemId = row.ItemId;
        _douyinRepostTitle = row.Title;
        ShowDouyinRepostPicker = false;
        SubmitMessage = "";
        OnPropertyChanged(nameof(DouyinRepostItemId));
        OnPropertyChanged(nameof(DouyinRepostPickedText));
        OnPropertyChanged(nameof(HasDouyinRepostItem));
        RunPreflight(SelectedDraft);
    }

    [RelayCommand]
    private void ClearDouyinRepostItem()
    {
        if (SelectedDraft is null) return;
        SelectedDraft.Declaration.DouyinRepostItemId = null;
        _douyinRepostTitle = null;
        OnPropertyChanged(nameof(DouyinRepostItemId));
        OnPropertyChanged(nameof(DouyinRepostPickedText));
        OnPropertyChanged(nameof(HasDouyinRepostItem));
        RunPreflight(SelectedDraft);
    }

    // ═══════════════════════════════════════════════════════════════
    //  全局区：话题（联邦解析）
    // ═══════════════════════════════════════════════════════════════

    [ObservableProperty] private string _topicInput = "";
    public ObservableCollection<TopicResolutionRow> TopicRows { get; } = [];
    [ObservableProperty] private bool _showTopicResults;
    [ObservableProperty] private bool _isResolvingTopics;

    /// <summary>把一个话题名加进草稿（只加名字 —— ID 由解析器查）</summary>
    [RelayCommand]
    private async Task AddTopicAsync()
    {
        var draft = SelectedDraft;
        var name = TopicInput.Trim().TrimStart('#');
        if (draft is null || name.Length == 0) return;
        if (draft.Topics.Contains(name)) { TopicInput = ""; return; }

        draft.Topics.Add(name);
        TopicInput = "";
        OnPropertyChanged(nameof(HasTopics));
        NotifyKuaishouTopicLimit();
        await ResolveTopicsAsync();
    }

    [RelayCommand]
    private async Task RemoveTopicAsync(string? name)
    {
        var draft = SelectedDraft;
        if (draft is null || name is null) return;

        draft.Topics.Remove(name);
        // 各目标里的解析结果也要同步移除，否则提交时会带上已删话题的 ID
        foreach (var p in draft.Overrides.Values) p.Topics.RemoveAll(t => t.Name == name);
        OnPropertyChanged(nameof(HasTopics));
        NotifyKuaishouTopicLimit();

        if (draft.Topics.Count == 0) { TopicRows.Clear(); ShowTopicResults = false; }
        else await ResolveTopicsAsync();
    }

    public bool HasTopics => SelectedDraft?.Topics.Count > 0;

    /// <summary>
    /// 解析全部话题 × 全部启用目标（联邦扇出：一次输入、多平台并行）。
    /// 结果同时写回各目标的 <see cref="PlatformParams.Topics"/>，供提交时组装。
    /// </summary>
    [RelayCommand]
    private async Task ResolveTopicsAsync()
    {
        var draft = SelectedDraft;
        if (draft is null || draft.Topics.Count == 0) return;

        IsResolvingTopics = true;
        try
        {
            LoadTopicRows(await ResolveTopicsCoreAsync(draft));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 话题解析失败");
        }
        finally { IsResolvingTopics = false; }
    }

    /// <summary>把解析行装进结果区：**按关键词分组**，组头画在每组第一行上（与账号排序页同一手法）</summary>
    private void LoadTopicRows(IReadOnlyList<TopicResolutionRow> rows)
    {
        TopicRows.Clear();
        string? previous = null;
        foreach (var row in rows)
        {
            row.IsGroupFirst = row.Keyword != previous;
            previous = row.Keyword;
            TopicRows.Add(row);
        }
        ShowTopicResults = TopicRows.Count > 0;
    }

    /// <summary>
    /// 展开/收起某行的候选列表；**回填行（候选没拉过）先现拉再展开**。
    ///
    /// 为什么要现拉：候选列表不落盘，导入文件夹 / 重开编辑器后回填出来的行一个候选都没有 ——
    /// 原来这种行的「换」是关的，用户看到小红书把「狗狗冷知识」匹配成「#亲近大自然」也改不了
    /// （2026-10-22 用户实测）。现在点「换」按需拉一次，当前采用的那个排在最前、仍标「当前」。
    /// </summary>
    [RelayCommand]
    private async Task ToggleCandidatesAsync(ResolutionRow? row)
    {
        if (row is null) return;
        if (row.IsExpanded) { row.IsExpanded = false; return; }

        if (row.Candidates.Count > 0) { row.IsExpanded = true; return; }
        if (SelectedDraft is not { } draft || !row.NeedsSearch || row.IsBusy) return;

        row.IsBusy = true;
        try
        {
            var targets = BuildResolutionTargets(draft)
                .Where(t => string.Equals(t.PlatformKey, row.PlatformKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var fetched = await _resolver.LoadCandidatesAsync(
                row, row is TopicResolutionRow, targets);
            if (fetched.Count == 0) return;   // 原因已写进 NotFoundText / 日志

            var list = fetched.ToList();
            var keepId = row switch
            {
                TopicResolutionRow t => t.TopicId,
                PoiResolutionRow p => p.Value?.PoiId,
                _ => null,
            };

            // 当前采用的值不在候选里（平台没把它返回，或换了关键词）→ 补一条排最前，
            // 让「当前」看得见、且不会因为展开候选就被第一个新候选静默顶掉
            if (!string.IsNullOrWhiteSpace(keepId)
                && list.All(c => !string.Equals(c.Id, keepId, StringComparison.OrdinalIgnoreCase)))
            {
                switch (row)
                {
                    case TopicResolutionRow t:
                        list.Insert(0, new TopicCandidate(
                            keepId, $"#{t.TopicName ?? t.Keyword}", "当前采用", t.TopicName ?? t.Keyword));
                        break;
                    case PoiResolutionRow p when p.Value is { } poi:
                        list.Insert(0, new PoiCandidate(
                            keepId, PublishResolver.PoiPrimary(poi), "当前采用", poi));
                        break;
                }
            }

            row.SetCandidates(list, keepId);
            row.IsExpanded = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 换候选失败 {Platform}/{Keyword}", row.PlatformKey, row.Keyword);
        }
        finally { row.IsBusy = false; }
    }

    /// <summary>
    /// 候选列表底部的「加载更多」—— 追加下一页。
    /// 平台不支持分页时按钮压根不出现（<see cref="PublishResolver.SupportsPaging"/>）：
    /// 抖音/小红书的话题是「联想/推荐」接口、视频号位置的请求体里都没有分页字段，
    /// 给了按钮也只会点了没反应。
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreCandidatesAsync(ResolutionRow? row)
    {
        if (row is null || row.IsLoadingMore) return;
        row.IsLoadingMore = true;
        try
        {
            var added = await _resolver.LoadMoreAsync(row, row is TopicResolutionRow);
            if (added.Count == 0)
                Log.Debug("[Publish] 「加载更多」没有新候选（{Platform}/{Keyword}）", row.PlatformKey, row.Keyword);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 加载更多失败 {Platform}", row.PlatformKey);
            row.CanLoadMore = false;   // 出错就给个了断，别让人反复点
        }
        finally { row.IsLoadingMore = false; }
    }

    /// <summary>换话题候选（展开后点「用这个」）—— 立刻写回该平台所有账号</summary>
    [RelayCommand]
    private void PickTopicCandidate(ResolutionCandidate? candidate)
    {
        if (candidate is not TopicCandidate picked) return;
        if (SelectedDraft is not { } draft) return;

        var row = TopicRows.FirstOrDefault(r => r.Candidates.Contains(picked));
        if (row is null) return;

        row.Selected = picked;     // 行内文案与「已采用」跟着换
        row.IsExpanded = false;
        WriteTopicResult(draft, row);
        RefreshTargetSummaries();
        RunPreflight(draft);
    }

    /// <summary>换位置候选 —— 立刻写回该平台所有账号</summary>
    [RelayCommand]
    private void PickPoiCandidate(ResolutionCandidate? candidate)
    {
        if (candidate is not PoiCandidate picked) return;
        if (SelectedDraft is not { } draft) return;

        var row = PoiRows.FirstOrDefault(r => r.Candidates.Contains(picked));
        if (row is null) return;

        row.Selected = picked;
        row.IsExpanded = false;
        ApplyToPlatformTargets(draft, row.PlatformKey, p => p.Location = picked.Value);
        RefreshTargetSummaries();
        RunPreflight(draft);
    }

    /// <summary>
    /// 话题解析的内核 —— **不依赖选中态**（不碰 TopicRows / IsResolvingTopics），
    /// 编辑器与导入路径共用。返回最后一个话题的解析行（导入路径不看它）。
    ///
    /// 为什么导入后必须跑一遍：组装 payload 时读的是 `Params.Topics`（带平台话题 ID 的
    /// 解析结果），**不是** `draft.Topics`。而写回只发生在编辑器点话题芯片的时候 ——
    /// 导入 10 条带话题的视频、不进编辑器直接提交，抖音/快手/小红书的话题会一条都发不出去，
    /// 页面上还完全看不出来（与「点了导入没反应」同一类问题）。
    /// </summary>
    private async Task<IReadOnlyList<TopicResolutionRow>> ResolveTopicsCoreAsync(PublishDraft draft)
    {
        if (draft.Topics.Count == 0) return [];
        var targets = BuildResolutionTargets(draft);
        if (targets.Count == 0) return [];

        // **每个话题都出结果**，不是只留最后一个：原先 UI 只显示最后一个词的解析行，
        // 前几个已经写进草稿、会被发出去，界面上却完全看不见
        // （与「隐形白名单」同一类问题，见 RefreshPrivacy 的注释）。
        var all = new List<TopicResolutionRow>();
        foreach (var name in draft.Topics)
        {
            var rows = await _resolver.ResolveTopicsAsync(name, targets);
            foreach (var row in rows) WriteTopicResult(draft, row);
            all.AddRange(rows);
        }
        return all;
    }

    /// <summary>把一个平台的话题解析结果写进该平台所有账号</summary>
    private void WriteTopicResult(PublishDraft draft, TopicResolutionRow row)
    {
        // 未命中的也写（PlatformId=null）—— 这样「正文保留纯文本」在数据上是显式的（缺陷 L2）
        ApplyToPlatformTargets(draft, row.PlatformKey, p =>
        {
            p.Topics.RemoveAll(t => t.Name == row.Keyword);
            p.Topics.Add(new ResolvedTopic(row.Keyword, row.TopicId, row.TopicName));
        });
    }

    /// <summary>
    /// 把某个平台的解析结果写进**该平台所有账号**的目标参数。
    ///
    /// 为什么是所有账号：话题/位置的 ID 是**平台级**的（2026-09-19 实测关闭 T3），
    /// 结果行本身也是每平台一行 —— 只写「第一个账号」会让同平台的其它账号漏掉，
    /// 提交时那几条就没有话题/位置（而且是静默的，页面上看不出来）。
    /// </summary>
    private void ApplyToPlatformTargets(PublishDraft draft, string platformKey,
        Action<PlatformParams> apply)
    {
        foreach (var target in draft.Targets)
        {
            if (!string.Equals(target.PlatformKey, platformKey, StringComparison.OrdinalIgnoreCase))
                continue;
            var key = PublishDraftStore.KeyOf(target.PlatformKey, target.SessionId);
            apply(draft.ParamsFor(target, _defaults.GetValueOrDefault(key)));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  全局区：位置（联邦解析）
    // ═══════════════════════════════════════════════════════════════

    [ObservableProperty] private string _locationQuery = "";
    public ObservableCollection<PoiResolutionRow> PoiRows { get; } = [];
    [ObservableProperty] private bool _showPoiResults;
    [ObservableProperty] private bool _isResolvingPoi;

    [RelayCommand]
    private async Task ResolveLocationAsync()
    {
        var draft = SelectedDraft;
        var keyword = LocationQuery.Trim();
        if (draft is null || keyword.Length == 0) return;

        IsResolvingPoi = true;
        try
        {
            var rows = await ResolveLocationCoreAsync(draft, keyword);
            PoiRows.Clear();
            foreach (var r in rows) PoiRows.Add(r);
            ShowPoiResults = PoiRows.Count > 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 位置解析失败");
        }
        finally { IsResolvingPoi = false; }
    }

    /// <summary>上次预热小红书签名的时间 —— 免得反复进编辑器时把预热任务堆在宿主闸门上</summary>
    private static DateTime _xhsWarmedAt = DateTime.MinValue;

    /// <summary>
    /// 后台预热小红书签名宿主（进编辑器 / 导入批次时各调一次，fire-and-forget）。
    ///
    /// 为什么要提前：签名宿主是 WebView2 页面里的 SDK，**冷启动要几十秒**（建环境 + 首次导航
    /// + 页面 webpack 加载）。等用户点了「搜索」才开始热，那次搜索多半来不及 ——
    /// 于是小红书那行只能显示「签名模块未就绪」（2026-09-19 实测：日志里 PollMnsv2Async
    /// 被 20s 的单平台预算掐断，紧接着请求就裸发出去了）。提前热好，正式签名只花几十毫秒。
    /// </summary>
    private void WarmXhsSigner(PublishDraft draft)
    {
        var sid = draft.Targets
            .FirstOrDefault(t => t.PlatformKey == PlatformNames.Xiaohongshu)?.SessionId;
        if (sid is not { } id) return;

        // 30 秒内只热一次：宿主冷启动本来就要几十秒，连点几下不该叠出一串等待
        if ((DateTime.UtcNow - _xhsWarmedAt).TotalSeconds < 30) return;
        _xhsWarmedAt = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            try { await XhsSignerService.PrepareAsync(id, CancellationToken.None); }
            catch (Exception ex) { Log.Debug(ex, "[XhsSign] 后台预热失败（不影响搜索，会裸发）"); }
        });
    }

    /// <summary>
    /// 位置解析内核 —— **不依赖选中态**，编辑器与导入路径（params.txt 的「位置:」）共用。
    ///
    /// 写回各平台**所有账号**：命中的写 PoiRef，未命中的**清空**（明确搜过但没找到，
    /// 就该是「不带位置」，而不是留着上一次的旧位置或预设里的）。
    /// </summary>
    private async Task<IReadOnlyList<PoiResolutionRow>> ResolveLocationCoreAsync(
        PublishDraft draft, string keyword)
    {
        var targets = BuildResolutionTargets(draft);
        if (targets.Count == 0) return [];

        var rows = await _resolver.ResolveLocationAsync(keyword, targets);
        foreach (var row in rows)
            ApplyToPlatformTargets(draft, row.PlatformKey, p => p.Location = row.Value);

        RefreshTargetSummaries();
        RunPreflight(draft);
        return rows;
    }

    /// <summary>清空位置（各目标一起清）</summary>
    [RelayCommand]
    private void ClearLocation()
    {
        var draft = SelectedDraft;
        if (draft is null) return;
        LocationQuery = "";
        foreach (var row in TargetRows) row.Params.Location = null;
        PoiRows.Clear();
        ShowPoiResults = false;
        RefreshTargetSummaries();
        RunPreflight(draft);
    }

    /// <summary>
    /// 回填话题解析结果 —— 重开编辑器时把上次解析到的（已存进各目标 PlatformParams）显示出来。
    /// 版式与实时解析后一致：**每平台一行、按关键词分组**。
    ///
    /// 候选列表不落盘，所以回填出来的行没有「换」（点「重新解析」才会重新拉候选）；
    /// 但「已采用」是真的 —— 那正是要写进 payload 的值。
    /// </summary>
    private void RefreshTopicRows()
    {
        TopicRows.Clear();
        var draft = SelectedDraft;
        if (draft is null) { ShowTopicResults = false; return; }

        var platforms = draft.EnabledTargets
            .GroupBy(t => t.PlatformKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // 关键词顺序 = 草稿的统一话题列表（唯一事实来源；不再从某个平台的解析缓存里猜顺序）
        var keywords = draft.Topics.ToList();

        foreach (var keyword in keywords)
        {
            var isFirst = true;
            foreach (var target in platforms)
            {
                var topic = draft.Overrides.GetValueOrDefault(target.Key)?
                    .Topics.FirstOrDefault(t => t.Name == keyword);
                if (topic is null) continue;

                var isWechat = target.PlatformKey == PlatformNames.WechatVideo;
                var row = new TopicResolutionRow
                {
                    Keyword = keyword,
                    IsGroupFirst = isFirst,
                    PlatformKey = target.PlatformKey,
                    SessionId = target.SessionId,
                    AccountLabel = target.AccountLabel,
                    BrandHex = ChannelBrandHex(target.PlatformKey),
                    DisplayName = PlatformNames.GetDisplayName(target.PlatformKey),
                    NeedsSearch = !isWechat,
                    // 视频号是纯文本（铁律三），命中与否对它没意义
                    Found = isWechat || !string.IsNullOrWhiteSpace(topic.PlatformId),
                    PrimaryText = isWechat
                        ? "纯文本，无需匹配"
                        : !string.IsNullOrWhiteSpace(topic.PlatformName) ? $"#{topic.PlatformName}" : $"#{topic.Name}",
                    NotFoundText = "未命中 → 正文里保留为纯文本",
                };
                // 记住当前采用值：行尾「换」现拉候选时靠它保住已选项（候选不落盘）
                row.SeedCurrent(topic.PlatformId, topic.PlatformName ?? topic.Name);
                TopicRows.Add(row);
                isFirst = false;
            }
        }
        ShowTopicResults = TopicRows.Count > 0;
    }

    private void RefreshPoiRows()
    {
        PoiRows.Clear();
        ShowPoiResults = false;
        var draft = SelectedDraft;
        if (draft is null) return;

        LocationQuery = draft.Targets
            .Select(t => t.Key)
            .Select(k => draft.Overrides.GetValueOrDefault(k)?.Location?.Name)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";

        // 已采用的位置也回填成行（候选不落盘 —— 行尾「换」点一下按需现拉，同话题）
        foreach (var target in draft.EnabledTargets
                     .GroupBy(t => t.PlatformKey, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.First()))
        {
            var poi = draft.Overrides.GetValueOrDefault(target.Key)?.Location;
            if (poi is null) continue;
            var row = new PoiResolutionRow
            {
                PlatformKey = target.PlatformKey,
                // 现拉候选要拿它去搜（漏了「换」会按空关键词直接返回）
                Keyword = poi.Name,
                SessionId = target.SessionId,
                AccountLabel = target.AccountLabel,
                BrandHex = ChannelBrandHex(target.PlatformKey),
                DisplayName = PlatformNames.GetDisplayName(target.PlatformKey),
                Found = true,
                PrimaryText = PublishResolver.PoiPrimary(poi),
                NotFoundText = "未找到匹配 → 该平台将不带位置",
            };
            row.SeedCurrent(poi);
            PoiRows.Add(row);
        }
        ShowPoiResults = PoiRows.Count > 0;
    }

    private List<ResolutionTarget> BuildResolutionTargets(PublishDraft draft) =>
        draft.EnabledTargets.Select(t => new ResolutionTarget(
            t.SessionId, t.PlatformKey, t.AccountLabel,
            ChannelBrandHex(t.PlatformKey),
            PlatformNames.GetDisplayName(t.PlatformKey))).ToList();

    private void RefreshTargetSummaries()
    {
        foreach (var row in TargetRows) row.RaiseAll();
        RefreshCollectionSelection();
    }

    // ═══════════════════════════════════════════════════════════════
    //  全局区：合集（查已同步映射，不扇出搜索）
    // ═══════════════════════════════════════════════════════════════

    public ObservableCollection<CollectionOptionViewModel> CollectionOptions { get; } = [];
    public ObservableCollection<CollectionMappingRow> CollectionRows { get; } = [];
    [ObservableProperty] private bool _isLoadingCollections;

    /// <summary>被过滤掉的文章合集数（视频发布页只列视频合集；为 0 时不占位）</summary>
    [ObservableProperty] private int _hiddenArticleCollectionCount;

    public bool HasHiddenArticleCollections => HiddenArticleCollectionCount > 0;
    public string HiddenArticleCollectionText =>
        $"{HiddenArticleCollectionCount} 个文章合集仅用于图文发布，不在视频发布列表里";

    partial void OnHiddenArticleCollectionCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasHiddenArticleCollections));
        OnPropertyChanged(nameof(HiddenArticleCollectionText));
    }

    /// <summary>逻辑合集列表（来自合集页已同步的数据；文章合集不进视频发布）</summary>
    private async Task LoadCollectionOptionsAsync()
    {
        IsLoadingCollections = true;
        try
        {
            var resp = await ApiClient.PostAsync<CollectionListResponse>("/api/collections/list");
            CollectionOptions.Clear();
            var hiddenArticles = 0;
            foreach (var c in resp?.Collections ?? [])
            {
                // 文章合集的平台侧 ID 是头条 collection_id，挂到视频上必然失败（3.2）
                if (c.ContentType == 1) { hiddenArticles++; continue; }

                var synced = c.Platforms?.Count(p => !string.IsNullOrWhiteSpace(p.PlatformCollectionId)) ?? 0;
                CollectionOptions.Add(new CollectionOptionViewModel
                {
                    CollectionId = c.Id,
                    Name = c.Title,
                    SyncedCount = synced,
                    TotalCount = PlatformNames.Collections.Count,   // 合集的平台范围 = 已接合集的平台（含头条）
                    ContentType = c.ContentType,
                });
            }
            HiddenArticleCollectionCount = hiddenArticles;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 合集列表加载失败");
        }
        finally { IsLoadingCollections = false; }
    }

    [RelayCommand]
    private async Task PickCollectionAsync(CollectionOptionViewModel? option)
    {
        var draft = SelectedDraft;
        if (draft is null) return;

        foreach (var o in CollectionOptions) o.IsSelected = ReferenceEquals(o, option);

        if (option is null)
        {
            foreach (var row in TargetRows)
            {
                row.Params.CollectionId = null;
                row.Params.CollectionName = null;
            }
            CollectionRows.Clear();
            RefreshTargetSummaries();
            return;
        }

        // 从合集页已同步的数据里查每个目标（平台 × 账号）的映射
        var detail = await FetchCollectionPlatformsAsync(option.CollectionId);
        var rows = MapCollectionToTargets(draft, option.Name, detail);
        RefreshTargetSummaries();   // 先刷平台卡（它内部会回填下拉），再贴状态行 —— 顺序反了行会被清掉
        ShowCollectionRows(rows);
        RunPreflight(draft);
    }

    /// <summary>
    /// 把合集的平台映射铺到草稿的每个目标上（平台 × 账号），并产出「每个账号一行」的状态。
    ///
    /// 编辑器里点选下拉与 <c>params.txt</c> 的「合集:」共用这一份 —— 两条路各写一遍必然漂移
    /// （导入那条路还要靠这份行数据生成给用户看的提示）。
    /// </summary>
    private List<CollectionMappingRow> MapCollectionToTargets(
        PublishDraft draft, string name, IReadOnlyList<CollectionPlatformSummary> detail)
    {
        var rows = new List<CollectionMappingRow>();

        foreach (var t in draft.EnabledTargets)
        {
            var key = PublishDraftStore.KeyOf(t.PlatformKey, t.SessionId);
            var target = draft.Targets.FirstOrDefault(x => x.Key == key);
            if (target is null) continue;
            var p = draft.ParamsFor(target, _defaults.GetValueOrDefault(key));

            var mapping = detail.FirstOrDefault(d =>
                string.Equals(d.PlatformKey, t.PlatformKey, StringComparison.OrdinalIgnoreCase)
                && d.PlatformSessionId == t.SessionId);

            if (!string.IsNullOrWhiteSpace(mapping?.PlatformCollectionId))
            {
                p.CollectionId = mapping.PlatformCollectionId;
                p.CollectionName = name;
                rows.Add(new CollectionMappingRow
                {
                    PlatformName = PlatformNames.GetDisplayName(t.PlatformKey),
                    AccountLabel = t.AccountLabel,
                    BrandHex = ChannelBrandHex(t.PlatformKey),
                    ValueText = $"{name} · {mapping.PlatformCollectionId}",
                    StatusText = "✓ 已同步",
                    StatusColorHex = "#0A5C28",
                });
            }
            else
            {
                // ⚠️ 映射缺失 = 该账号从未同步过这个合集。
                // **必须显式提示** —— 读成「某平台没有合集」是错的：四家都支持合集，
                // 「未同步」是同步状态，不是平台能力（设计文档 §4.4）。
                p.CollectionId = null;
                p.CollectionName = null;
                rows.Add(new CollectionMappingRow
                {
                    PlatformName = PlatformNames.GetDisplayName(t.PlatformKey),
                    AccountLabel = t.AccountLabel,
                    BrandHex = ChannelBrandHex(t.PlatformKey),
                    ValueText = "该账号从未同步此合集 → 将不带合集",
                    StatusText = "去合集页同步",
                    StatusColorHex = "#4B3BC3",
                    IsMissing = true,
                });
            }
        }

        return rows;
    }

    /// <summary>把上一步产出的行数据贴到编辑器上（下拉选中态一并回填）</summary>
    private void ShowCollectionRows(IEnumerable<CollectionMappingRow> rows)
    {
        CollectionRows.Clear();
        foreach (var r in rows) CollectionRows.Add(r);
        RefreshCollectionSelection();
    }

    /// <summary>取某合集的平台映射明细（列表项里只有摘要，缺会话级细节时再查详情）</summary>
    private async Task<List<CollectionPlatformSummary>> FetchCollectionPlatformsAsync(Guid collectionId)
    {
        try
        {
            var resp = await ApiClient.PostAsync<CollectionListResponse>("/api/collections/list");
            var item = resp?.Collections.FirstOrDefault(c => c.Id == collectionId);
            return item?.Platforms ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 合集映射查询失败 {Id}", collectionId);
            return [];
        }
    }

    /// <summary>下拉当前选中项（ComboBox 双向绑定用；set 时触发映射查询）</summary>
    private CollectionOptionViewModel? _selectedCollectionOption;
    public CollectionOptionViewModel? SelectedCollectionOption
    {
        get => _selectedCollectionOption;
        set
        {
            if (ReferenceEquals(_selectedCollectionOption, value)) return;
            _selectedCollectionOption = value;
            OnPropertyChanged();
            _ = PickCollectionAsync(value);
        }
    }

    /// <summary>
    /// 回填编辑器的合集选中态（各目标用的都是同一个逻辑合集才算选中）。
    ///
    /// 下拉用的是**静默**回填：走 <c>SelectedCollectionOption</c> 的公共 setter 会连带
    /// 触发一次映射查询与覆盖写入 —— 切个条目就把人家配好的合集重写一遍，代价与风险都不值。
    /// 这个函数**不碰** CollectionRows（那是「本次点选/导入的结果」，清它的时机在切条目那一步）。
    /// </summary>
    private void RefreshCollectionSelection()
    {
        var draft = SelectedDraft;
        if (draft is null) { SetSelectedCollectionSilently(null); return; }

        // 多目标用了不同的平台合集 ID（同属一个逻辑合集）—— 按名字回填
        var name = draft.EnabledTargets
            .Select(t => draft.Overrides.GetValueOrDefault(t.Key)?.CollectionName)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

        foreach (var o in CollectionOptions)
            o.IsSelected = !string.IsNullOrWhiteSpace(name) && o.Name == name;

        SetSelectedCollectionSilently(CollectionOptions.FirstOrDefault(o => o.IsSelected));
    }

    /// <summary>只挪下拉的选中项，不重跑映射（见 <see cref="RefreshCollectionSelection"/>）</summary>
    private void SetSelectedCollectionSilently(CollectionOptionViewModel? value)
    {
        if (ReferenceEquals(_selectedCollectionOption, value)) return;
        _selectedCollectionOption = value;
        OnPropertyChanged(nameof(SelectedCollectionOption));
    }

    // ═══════════════════════════════════════════════════════════════
    //  可见性 + 小红书白名单（白名单紧贴可见性，不放平台卡）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 全局「原创声明」—— 2026-11-06 从小红书平台卡上提到全局区
    /// （与「允许他人下载」并列）。原创是**内容属性**，不是账号属性：
    /// 用户在本条上开一次，小红书/B站/头条各自映射；抖音/快手/视频号没有投稿字段。
    ///
    /// 旧的 <see cref="PlatformParams.IsOriginal"/> 保留为覆盖位（兼容历史预设/草稿），
    /// 用户一动这个开关就把覆盖清掉 —— 否则会出现「全局关着、某个账号却仍发原创」。
    /// </summary>
    public bool IsOriginal
    {
        get => SelectedDraft?.IsOriginal ?? false;
        set
        {
            var draft = SelectedDraft;
            if (draft is null || draft.IsOriginal == value) return;

            draft.IsOriginal = value;
            foreach (var p in draft.Overrides.Values) p.IsOriginal = null;

            foreach (var row in TargetRows)
            {
                row.GlobalIsOriginal = value;
                row.RaiseAll();
            }

            OnPropertyChanged();
            RunPreflight(draft);
            RefreshSummaryCounts();
        }
    }

    public ObservableCollection<VisibilityOptionViewModel> VisibilityOptions { get; }

    // 同 BuildDeclarationOptions：标签唯一来源在 VisibilityMapping，顺序 = 枚举声明顺序。
    private static ObservableCollection<VisibilityOptionViewModel> BuildVisibilityOptions() =>
        [.. Enum.GetValues<PublishVisibility>()
            .Select(v => new VisibilityOptionViewModel
            {
                Kind = v,
                Label = VisibilityMapping.VisibilityLabel(v),
            })];

    [RelayCommand]
    private void SelectVisibility(PublishVisibility kind)
    {
        var draft = SelectedDraft;
        if (draft is null) return;

        draft.Visibility = kind;
        SyncVisibilitySelection();
        // 全局可见性变了 → 各平台卡的回退提示与摘要都要跟着变
        foreach (var row in TargetRows)
        {
            row.Params.Visibility = null;   // 跟随全局
            row.GlobalVisibility = kind;
            row.VisibilityNote = VisibilityMapping.FallbackNote(row.Target.PlatformKey, kind);
            row.RefreshSummary();
        }
        RefreshPrivacy();
        RunPreflight(draft);
        RefreshSummaryCounts();
    }

    private void SyncVisibilitySelection()
    {
        var kind = SelectedDraft?.Visibility ?? PublishVisibility.Public;
        foreach (var o in VisibilityOptions)
            o.IsSelected = o.Kind == kind;
        OnPropertyChanged(nameof(VisibilityNote));
    }

    /// <summary>聚合回退说明：哪些平台会退回「公开」（设计文档 §4.2 —— 不静默忽略）</summary>
    public string VisibilityNote
    {
        get
        {
            var draft = SelectedDraft;
            if (draft is null) return "";
            var notes = draft.EnabledTargets
                .Select(t => VisibilityMapping.FallbackNote(t.PlatformKey, draft.Visibility))
                .Where(n => n is not null)
                .Distinct()
                .ToList();
            return notes.Count == 0
                ? "所选平台都支持此可见性"
                : string.Join("；", notes.Select(n => $"{n}"));
        }
    }

    // ═══ 小红书白名单（只在「只给谁看 / 不给谁看」时出现）═══

    public ObservableCollection<PrivacyUserRow> PrivacyResults { get; } = [];

    [ObservableProperty] private string _privacySearchText = "";
    [ObservableProperty] private bool _showPrivacyWhitelist;
    [ObservableProperty] private bool _isSearchingPrivacyUsers;

    /// <summary>老版规则：ShowXhsPrivacyUserSearch => XhsPrivacyType == 3 || == 4
    /// （<c>HomeViewModel.cs:2371</c>）—— 新版语义相同：只有「只给谁看 / 不给谁看」要选人</summary>
    private void RefreshPrivacy()
    {
        var kind = SelectedDraft?.Visibility ?? PublishVisibility.Public;
        ShowPrivacyWhitelist = kind is PublishVisibility.OnlyUsers or PublishVisibility.ExcludeUsers;
        if (!ShowPrivacyWhitelist) PrivacyResults.Clear();
        // 重开编辑器时把已存的白名单填回 chips —— 否则用户看不到自己上次选了谁，
        // 而提交时那些人**照样会被发出去**（隐形的白名单是最糟的一种状态）
        PrivacySearchText = "";
        RefreshPrivacyChips();
    }

    /// <summary>已选白名单（卡片上以 chip 展示，可逐个 ×）</summary>
    public ObservableCollection<PrivacyUserRow> PrivacyChips { get; } = [];

    [RelayCommand]
    private async Task SearchPrivacyUsersAsync()
    {
        var draft = SelectedDraft;
        var keyword = PrivacySearchText.Trim();
        if (draft is null || keyword.Length == 0) return;

        var xhs = draft.EnabledTargets.FirstOrDefault(t => t.PlatformKey == PlatformNames.Xiaohongshu);
        if (xhs is null)
        {
            SubmitMessage = "没有选中的小红书账号，无法搜索用户";
            return;
        }

        IsSearchingPrivacyUsers = true;
        try
        {
            var creds = await PlatformSessionApi.GetCredentialsAsync(xhs.SessionId);
            if (creds?.Cookie is null)
            {
                SubmitMessage = "小红书账号凭证不可用，请重新登录";
                return;
            }

            var options = new PlatformOptions { SessionId = xhs.SessionId };
            await XhsSignerService.PrepareAsync(xhs.SessionId, default);
            var users = await new XhsPrivacyService(options).SearchUsersAsync(creds.Cookie, keyword);

            PrivacyResults.Clear();
            foreach (var u in users)
            {
                PrivacyResults.Add(new PrivacyUserRow
                {
                    UserId = u.UserId,
                    Nickname = u.Nickname,
                    Avatar = u.Avatar,
                    IsSelected = draft.VisibilityUserIds.Contains(u.UserId),
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Publish] 小红书用户搜索失败");
            SubmitMessage = "用户搜索失败：" + ex.Message;
        }
        finally { IsSearchingPrivacyUsers = false; }
    }

    [RelayCommand]
    private void TogglePrivacyUser(PrivacyUserRow? user)
    {
        var draft = SelectedDraft;
        if (draft is null || user is null) return;

        if (user.IsSelected)
        {
            if (!draft.VisibilityUserIds.Contains(user.UserId)) draft.VisibilityUserIds.Add(user.UserId);
        }
        else
        {
            draft.VisibilityUserIds.Remove(user.UserId);
        }
        RefreshPrivacyChips();
        RunPreflight(draft);
    }

    [RelayCommand]
    private void RemovePrivacyUser(string? userId)
    {
        var draft = SelectedDraft;
        if (draft is null || userId is null) return;
        draft.VisibilityUserIds.Remove(userId);
        foreach (var r in PrivacyResults.Where(r => r.UserId == userId)) r.IsSelected = false;
        RefreshPrivacyChips();
        RunPreflight(draft);
        OnPropertyChanged(nameof(PrivacyChips));
    }

    private void RefreshPrivacyChips()
    {
        var draft = SelectedDraft;
        PrivacyChips.Clear();
        if (draft is null) return;

        foreach (var id in draft.VisibilityUserIds)
        {
            // 已选的用户可能不在当前搜索结果里（换过关键词）—— 名字查不到就显示 ID 缩写，
            // 总之要看得见、能删掉，不能变成隐形的白名单项
            var known = PrivacyResults.FirstOrDefault(r => r.UserId == id);
            PrivacyChips.Add(new PrivacyUserRow
            {
                UserId = id,
                Nickname = known?.Nickname ?? $"{id[..Math.Min(6, id.Length)]}…",
                Avatar = known?.Avatar ?? "",
                IsSelected = true,
            });
        }
        OnPropertyChanged(nameof(PrivacyChips));
    }
}

/// <summary>发布预设的一行（账号级默认值的摘要）</summary>
public sealed class PresetRowViewModel
{
    public required string SessionKey { get; init; }
    public required Guid SessionId { get; init; }
    public required string PlatformKey { get; init; }
    public required string PlatformName { get; init; }
    public required string AccountLabel { get; init; }
    public required string BrandHex { get; init; }
    public required string SummaryText { get; init; }

    public string Initial => PlatformName.Length > 0 ? PlatformName[..1] : "?";
}

/// <summary>小红书白名单用户行（搜索结果 / 已选 chip 共用）</summary>
public sealed partial class PrivacyUserRow : ObservableObject
{
    public required string UserId { get; init; }
    public required string Nickname { get; init; }
    public required string Avatar { get; init; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 视频号拍摄地区下拉项（国家 / 省 / 市）—— 名称给界面看，Code 是 shootInfo 里的整数码。
/// 三级共用一种类型；<see cref="Sub"/> 是下一级列表（市为空列表）。
/// </summary>
public sealed class WechatShootRegionOption(int code, string name, IReadOnlyList<WechatShootRegionOption> sub)
{
    public int Code { get; } = code;
    public string Name { get; } = name;
    public IReadOnlyList<WechatShootRegionOption> Sub { get; } = sub;

    /// <summary>列表展示名（平台偶尔给空名时退化成编码，别显示成一片空白）</summary>
    public string Display => Name.Length > 0 ? Name : $"码 {Code}";

    public override string ToString() => Display;
}

/// <summary>
/// 抖音历史作品行（转载来源选择器）—— 封面是远程 http 图，新版没有远程图加载器，
/// 只给标题/时长/日期（老版也是占位封面，信息量等价）。
/// </summary>
public sealed class DouyinRepostItemRow
{
    public DouyinRepostItemRow(HistoryMediaItem item)
    {
        ItemId = item.ItemId;
        Title = string.IsNullOrWhiteSpace(item.Title) ? "无标题" : item.Title;
        DurationText = item.DurationS > 0 ? $"{item.DurationS / 60}:{item.DurationS % 60:00}" : "";
        CreatedText = item.CreateTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(item.CreateTime).LocalDateTime.ToString("yyyy-MM-dd")
            : "";
    }

    public string ItemId { get; }
    public string Title { get; }
    public string DurationText { get; }
    public string CreatedText { get; }
}
