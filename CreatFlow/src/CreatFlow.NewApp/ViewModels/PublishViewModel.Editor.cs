using CreatFlow.Platforms.Core;
using CreatFlow.Platforms.Douyin;
using CreatFlow.Platforms.Topics;
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
    /// 左栏全局「官方活动」区（2.6.9）：**每平台一行**（抖音/快手/小红书/B站），
    /// 与话题一样合并到公共区；头条/视频号没有活动接口，不出现行。
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
                // 话题能力（2.6.3）：六平台统一目录，视频号也会返回一个「无接口」的能力对象
                TopicCapability = TopicServiceCatalog.CapabilityOf(t.PlatformKey),
            };
            row.RefreshTopicChips();
            // 已选活动（2.6.4）：草稿/预设里已有的活动（params.txt 导入、旧档迁移）装配时就要显示
            row.RefreshActivityChips();
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

    // ═══ 视频号「自行拍摄」的地区码（协议要整数码，不是名字）═══

    public string ShootCountryCode
    {
        get => SelectedDraft?.Declaration.ShootRegion?.CountryCode?.ToString() ?? "";
        set => SetShootRegion(country: value);
    }

    public string ShootProvinceCode
    {
        get => SelectedDraft?.Declaration.ShootRegion?.ProvinceCode?.ToString() ?? "";
        set => SetShootRegion(province: value);
    }

    public string ShootCityCode
    {
        get => SelectedDraft?.Declaration.ShootRegion?.CityCode?.ToString() ?? "";
        set => SetShootRegion(city: value);
    }

    private void SetShootRegion(string? country = null, string? province = null, string? city = null)
    {
        var d = SelectedDraft?.Declaration;
        if (d is null) return;

        var cur = d.ShootRegion ?? new RegionRef();
        d.ShootRegion = cur with
        {
            CountryCode = ParseOrNull(country) ?? cur.CountryCode,
            ProvinceCode = ParseOrNull(province) ?? cur.ProvinceCode,
            CityCode = ParseOrNull(city) ?? cur.CityCode,
        };
        OnPropertyChanged(nameof(ShootCountryCode));
        OnPropertyChanged(nameof(ShootProvinceCode));
        OnPropertyChanged(nameof(ShootCityCode));
    }

    /// <summary>空串要落成 null（清零）而不是保留旧值 —— 用户删掉就是不想填</summary>
    private static int? ParseOrNull(string? s) =>
        int.TryParse(s?.Trim(), out var v) ? v : null;

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
