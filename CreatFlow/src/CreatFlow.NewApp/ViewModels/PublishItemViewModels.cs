using Avalonia.Media.Imaging;
using CreatFlow.Platforms.Bilibili;
using CreatFlow.Platforms.Douyin;
using CreatFlow.Platforms.Kuaishou;
using CreatFlow.Platforms.Topics;
using CreatFlow.Platforms.Toutiao;
using CreatFlow.Platforms.WechatVideo;
using CreatFlow.Platforms.Xiaohongshu;

namespace CreatFlow.NewApp.ViewModels;

// ═══════════════════════════════════════════════════════════════════════════
//  发布页的条目级 VM（工作台的账号芯片 / 编辑器的平台卡 / 单选组）
//
//  约定：这些 VM **只持 Guid 与展示状态**，不持 PlatformSession 对象引用
//  （平台页/合集页同款 —— 跨组件联动一律以 SessionId 为主键）。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 工作台的「发布到」账号芯片 —— 一个（平台 × 账号）。
/// 勾选态即「新导入的条目默认发到这些账号」。
/// </summary>
public sealed partial class AccountChipViewModel : ObservableObject
{
    public required Guid SessionId { get; init; }
    public required string PlatformKey { get; init; }
    public required string PlatformName { get; init; }
    public required string AccountLabel { get; init; }
    public required string BrandHex { get; init; }
    /// <summary>登录态是否可用（过期账号仍可勾，但提交前预检会拦）</summary>
    public bool IsLoggedIn { get; init; } = true;
    /// <summary>该平台是否可勾选（2026-11-06 起 B站/头条也可勾选；发布执行链路仍在阶段 4/5）</summary>
    public bool CanPublish { get; init; } = true;
    /// <summary>主账号（排序页要标出来 —— 一个平台好多号时靠它认人）</summary>
    public bool IsPrimary { get; init; }

    [ObservableProperty]
    private bool _isOn = true;

    public string Initial => PlatformName.Length > 0 ? PlatformName[..1] : "?";

    /// <summary>登录失效的芯片加提示（不隐藏 —— 用户要能看到并去登录）</summary>
    public bool ShowExpired => !IsLoggedIn;

    /// <summary>不可勾选的芯片加灰色「未接入」小标（2026-11-06 起 B站/头条可勾选，不再出现）</summary>
    public bool ShowUnsupported => !CanPublish;
}

/// <summary>
/// 排序页的一行（平台 × 账号）。
///
/// **组头画在每组首行的模板里**（<see cref="IsGroupFirst"/>），而不是套一层分组容器：
/// 嵌套 ItemsControl 会让模板里的 <c>$parent[ItemsControl]</c> 命中**内层**那个，
/// 命令绑定全部错位到内层 DataContext。扁平列表 + 首行带组头没有这个问题，
/// 也省掉把扁平序列重新组织成分组结构的往返。
/// </summary>
public sealed partial class AccountOrderRowViewModel : ObservableObject
{
    public required string SessionKey { get; init; }
    public required Guid SessionId { get; init; }
    public required string PlatformKey { get; init; }
    public required string PlatformName { get; init; }
    public required string AccountLabel { get; init; }
    public required string BrandHex { get; init; }
    public bool IsPrimary { get; init; }

    public string Initial => PlatformName.Length > 0 ? PlatformName[..1] : "?";

    // ── 四个边界态：决定哪些按钮该灰掉（每次移动后由 RefreshOrderBoundaries 统一重算，
    //    不要在 XAML 里用转换器现算 —— 那样每加一种移动方式就要多写一套判断） ──

    /// <summary>所在平台组的首行 —— 决定它上方是否画组头，同时是行级 ↑ 的禁用条件</summary>
    [ObservableProperty] private bool _isGroupFirst;
    /// <summary>组内末行 → 行级 ↓ 禁用（到组边界停住，跨组请用整组移动）</summary>
    [ObservableProperty] private bool _isLastInGroup;
    /// <summary>首个平台组 → 整组 ↑ 禁用</summary>
    [ObservableProperty] private bool _isFirstGroup;
    /// <summary>末个平台组 → 整组 ↓ 禁用</summary>
    [ObservableProperty] private bool _isLastGroup;

    /// <summary>主号小标（零 emoji：品牌紫文字标）</summary>
    public bool ShowPrimary => IsPrimary;
}

/// <summary>
/// 编辑器的平台卡（右栏，默认折叠成一行摘要）。
///
/// 摘要行 = 可见性 · 合集 · 位置；展开才看长尾开关。
/// 绝大多数时候用户不展开 —— 这正是把 50 个字段压成「看一眼就够」的关键。
/// </summary>
public sealed partial class TargetRowViewModel : ObservableObject
{
    public required PublishTarget Target { get; init; }
    public required PlatformParams Params { get; init; }
    /// <summary>账号级预设（「恢复账号默认」的比对基准；null = 该账号还没有预设）</summary>
    public PlatformParams? Preset { get; init; }

    public required string PlatformName { get; init; }
    public required string AccountLabel { get; init; }
    public required string BrandHex { get; init; }

    public string SessionKey => Target.Key;
    public string Initial => PlatformName.Length > 0 ? PlatformName[..1] : "?";
    public bool IsPrimary => Target.IsPrimary;
    public bool IsLoggedIn => Target.IsLoggedIn;

    public bool IsDouyin => Target.PlatformKey == PlatformNames.Douyin;
    public bool IsKuaishou => Target.PlatformKey == PlatformNames.Kuaishou;
    public bool IsXhs => Target.PlatformKey == PlatformNames.Xiaohongshu;
    public bool IsWechatVideo => Target.PlatformKey == PlatformNames.WechatVideo;
    public bool IsBilibili => Target.PlatformKey == PlatformNames.Bilibili;

    /// <summary>
    /// B站分区（阶段 4）：<c>tid</c> / <c>human_type2</c>，默认 21 / 1015（家装房产）。
    /// params.txt 的「B站分区 / B站二级分区」仍可覆盖。
    ///
    /// 2026-11-10：投稿页的「分区」下拉就是 <c>human_type2</c>（30 个领域，真账号列表已验），
    /// UI 从这里点选；<c>tid</c> 是旧版分区（typelist 对当前账号只回父分区，映射未知）——
    /// 保持默认 / params 覆盖，不在 UI 里猜。
    /// </summary>
    public string BilibiliPartitionText
    {
        get
        {
            var tid = Params.Bilibili?.Tid ?? 21;
            var type2 = Params.Bilibili?.HumanType2 ?? 1015;
            var type = _pickedType2 == Params.Bilibili?.HumanType2 ? _pickedType2Name : null;
            return type is null ? $"{tid} / {type2}" : $"{tid} / {type2}（{type}）";
        }
    }

    // ═══ B站分区选择器（2026-11-10：human/type2/list）═══
    // 平台投稿页「分区」= human_type2；选中仍只写 BilibiliExtras.HumanType2（wire 不变）。

    [ObservableProperty] private bool _isBiliPartitionPanelOpen;
    [ObservableProperty] private bool _isBiliPartitionBusy;
    [ObservableProperty] private string _biliPartitionStatusText = "";
    [ObservableProperty] private bool _biliPartitionStatusIsError;

    public ObservableCollection<BiliTypePickViewModel> BiliPartitionCandidates { get; } = [];
    public bool HasBiliPartitionCandidates => BiliPartitionCandidates.Count > 0;
    public bool HasBiliPartitionStatus => BiliPartitionStatusText.Length > 0;

    partial void OnBiliPartitionStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasBiliPartitionStatus));

    /// <summary>分区行展示：选过显示「家装房产（1015）」；否则显示 human_type2 值。</summary>
    public string BilibiliPartitionDisplay
    {
        get
        {
            var type2 = Params.Bilibili?.HumanType2 ?? 1015;
            return _pickedType2 == Params.Bilibili?.HumanType2 && _pickedType2Name is { Length: > 0 } name
                ? $"{name}（{type2}）"
                : $"human_type2 {type2}（默认）";
        }
    }

    /// <summary>旧版 tid 的只读说明（不在 UI 里选 —— typelist 只回父分区，映射未知）</summary>
    public string BilibiliTidHint =>
        $"旧版分区 tid 仍按 {Params.Bilibili?.Tid ?? 21} 下发（params.txt「B站分区」可覆盖）";

    private int? _pickedType2;
    private string? _pickedType2Name;

    /// <summary>装分区候选（human_type2）。</summary>
    public void SetBiliPartitionCandidates(IReadOnlyList<BilibiliTypeOption> types)
    {
        BiliPartitionCandidates.Clear();
        foreach (var t in types)
            BiliPartitionCandidates.Add(new BiliTypePickViewModel { Type = t });
        OnPropertyChanged(nameof(HasBiliPartitionCandidates));
    }

    /// <summary>选中一个分区：写 <c>human_type2</c> + 记住展示名。</summary>
    public void ApplyPickedBiliPartition(BilibiliTypeOption type)
    {
        _pickedType2 = type.Id;
        _pickedType2Name = type.Name;
        BiliGroup.HumanType2 = type.Id;
        OnPropertyChanged(nameof(BilibiliPartitionDisplay));
        OnPropertyChanged(nameof(BilibiliPartitionText));
    }

    // ═══ 话题（2.6.3）：平台卡「话题」块 ═══

    /// <summary>该平台的话题能力（搜索 / 推荐 / 推荐是否依赖上传）—— 由
    /// <see cref="TopicServiceCatalog.CapabilityOf"/> 装配，XAML 里不再 if-platform-key</summary>
    public TopicCapability TopicCapability { get; init; } = new();

    public bool TopicSupportsSearch => TopicCapability.SupportsSearch;
    public bool TopicSupportsRecommend => TopicCapability.SupportsRecommend;
    public bool TopicHasApi => TopicCapability.HasAnyTopicApi;
    public bool TopicHasNoApi => !TopicHasApi;
    /// <summary>视频号这类「无接口」平台的说明（原样来自平台实现，绝不编造推荐）</summary>
    public string TopicNoInterfaceText => TopicCapability.NoInterfaceReason ?? "";
    /// <summary>该平台的推荐依赖上传完成后的上下文（快手 editSessionId / 头条 Vid）</summary>
    public bool TopicRecommendNeedsUpload =>
        TopicCapability.SupportsRecommend && TopicCapability.RecommendNeedsUpload;

    /// <summary>话题块下方的能力说明；空串 = 不占位。</summary>
    public string TopicApiHint => TopicHasNoApi
        ? TopicNoInterfaceText
        : TopicRecommendNeedsUpload
            ? "该平台的推荐要等视频上传完成后才有"
            : "";

    public bool HasTopicApiHint => TopicApiHint.Length > 0;

    /// <summary>折叠态卡头的一行说明 —— 无接口平台别写「平台接口搜索」自相矛盾</summary>
    public string TopicHeaderHint => TopicHasNoApi
        ? "无话题接口 · 手动输入"
        : "平台接口搜索 / 推荐，或直接输入";

    public string TopicSearchWatermark => "输入关键词搜索平台话题，回车搜索";

    public string TopicManualWatermark => TopicHasNoApi
        ? "该平台无话题接口：直接输入话题名，回车添加"
        : "也可直接输入话题名（纯文本也能发），回车添加";

    /// <summary>已选话题芯片（由 <see cref="Params"/>.<c>TopicRefs</c> 重建，不单独存）</summary>
    public ObservableCollection<TopicChipViewModel> TopicChips { get; } = [];
    public bool HasTopicChips => TopicChips.Count > 0;

    /// <summary>搜索 / 推荐拉回来的候选（会话级，不落盘）</summary>
    public ObservableCollection<TopicPickViewModel> TopicSuggestions { get; } = [];
    public bool HasTopicSuggestions => TopicSuggestions.Count > 0;

    [ObservableProperty] private bool _isTopicPanelOpen;
    [ObservableProperty] private string _topicQuery = "";
    [ObservableProperty] private string _topicManualInput = "";
    [ObservableProperty] private string _topicStatusText = "";
    [ObservableProperty] private bool _topicStatusIsError;
    [ObservableProperty] private bool _isTopicBusy;

    /// <summary>
    /// 上一次「直接从缓存装上」的查询键（2.6.7）—— 同一个键再点一次 = 强制刷新，
    /// 不走缓存（用户要的是最新推荐时不必等 10 分钟）。不参与 UI 绑定，只是命令间的记账。
    /// </summary>
    public string? CachedTopicKey { get; set; }

    public string TopicPanelButtonText => IsTopicPanelOpen ? "收起" : "添加话题";

    /// <summary>空态提示只在面板展开时出现 —— 默认折叠的卡片别为「还没有话题」占两行</summary>
    public bool ShowTopicEmptyHint => !HasTopicChips && IsTopicPanelOpen;

    /// <summary>状态行文案（说明 / 已添加 / 失败原因）；空串 = 不显示</summary>
    public bool HasTopicStatus => TopicStatusText.Length > 0;
    public string TopicStatusColorHex => TopicStatusIsError ? "#CB2634" : "#86909C";

    partial void OnIsTopicPanelOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(TopicPanelButtonText));
        OnPropertyChanged(nameof(ShowTopicEmptyHint));
    }

    partial void OnTopicStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasTopicStatus));

    partial void OnTopicStatusIsErrorChanged(bool value) =>
        OnPropertyChanged(nameof(TopicStatusColorHex));

    /// <summary>按 <see cref="Params"/>.<c>TopicRefs</c> 重建芯片（增删 / 选活动带回话题 / 恢复预设后调）</summary>
    public void RefreshTopicChips()
    {
        TopicChips.Clear();
        foreach (var t in Params.TopicRefs)
            TopicChips.Add(new TopicChipViewModel { Topic = t });
        OnPropertyChanged(nameof(HasTopicChips));
        OnPropertyChanged(nameof(ShowTopicEmptyHint));
        RefreshTopicPickStates();
    }

    /// <summary>装上一批候选（去重 + 截断），并把「已添加」状态同步成当前话题列表</summary>
    public void SetTopicSuggestions(IEnumerable<TopicSuggestion> items, bool fromRecommend)
    {
        TopicSuggestions.Clear();
        foreach (var s in TopicSelection.Dedupe(items).Take(TopicSelection.MaxSuggestions))
            TopicSuggestions.Add(new TopicPickViewModel { Suggestion = s, FromRecommend = fromRecommend });
        OnPropertyChanged(nameof(HasTopicSuggestions));
        RefreshTopicPickStates();
    }

    /// <summary>候选行上的「添加 / 已添加」跟着话题列表走</summary>
    public void RefreshTopicPickStates()
    {
        foreach (var pick in TopicSuggestions)
            pick.IsSelected = Params.TopicRefs.Any(t =>
                string.Equals(t.Name, pick.Suggestion.Name, StringComparison.Ordinal));
    }

    // ═══ 官方活动（2.6.4）：平台卡「官方活动」块 ═══
    //
    //  只有抖音 / 快手 / 小红书 / B站 / 视频号 出现（能力来自 TopicCapability.SupportsActivities，
    //  头条没有活动接口 → 整块不渲染，不误导用户；视频号 2026-11-14 起有 post_search_event）。
    //  列表是**单选**：点一张卡 = 替换 <see cref="Params"/> 里的活动列表（1 个）；
    //  已结束的活动灰显不可选；选中经 UpsertActivity 自动带活动关联话题（2.6.4 核心）。

    /// <summary>该平台有没有官方活动接口（头条 = false，整块隐藏）</summary>
    public bool ActivitySupported => TopicCapability.SupportsActivities;

    /// <summary>折叠态卡头说明（与话题块同一行文风格）</summary>
    public string ActivityHeaderHint => "关联官方活动可在活动页获得额外曝光";

    /// <summary>已选活动（由 <see cref="Params"/>.<c>Activities</c> 重建；params.txt 可导入多个）</summary>
    public ObservableCollection<ActivityChipViewModel> ActivityChips { get; } = [];
    public bool HasActivityChips => ActivityChips.Count > 0;

    /// <summary>拉取回来的活动候选（会话级，不落盘）</summary>
    public ObservableCollection<ActivityCardViewModel> ActivityOptions { get; } = [];
    public bool HasActivityOptions => ActivityOptions.Count > 0;

    [ObservableProperty] private bool _isActivityPanelOpen;
    [ObservableProperty] private string _activityStatusText = "";
    [ObservableProperty] private bool _activityStatusIsError;
    [ObservableProperty] private bool _isActivityBusy;

    /// <summary>活动块的同款记账（2.6.7）：同键再点一次 = 强制刷新。</summary>
    public string? CachedActivityKey { get; set; }

    public string ActivityPanelButtonText => IsActivityPanelOpen ? "收起" : "选择活动";

    /// <summary>空态提示只在面板展开时出现（折叠卡不为「还没选活动」占行）</summary>
    public bool ShowActivityEmptyHint => !HasActivityChips && IsActivityPanelOpen;

    public bool HasActivityStatus => ActivityStatusText.Length > 0;
    public string ActivityStatusColorHex => ActivityStatusIsError ? "#CB2634" : "#86909C";

    partial void OnIsActivityPanelOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ActivityPanelButtonText));
        OnPropertyChanged(nameof(ShowActivityEmptyHint));
    }

    partial void OnActivityStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasActivityStatus));

    partial void OnActivityStatusIsErrorChanged(bool value) =>
        OnPropertyChanged(nameof(ActivityStatusColorHex));

    /// <summary>按 <see cref="Params"/>.<c>Activities</c> 重建已选活动芯片（选择 / 清除 / 恢复预设后调）</summary>
    public void RefreshActivityChips()
    {
        ActivityChips.Clear();
        foreach (var a in Params.Activities)
            ActivityChips.Add(new ActivityChipViewModel { Activity = a });
        OnPropertyChanged(nameof(HasActivityChips));
        OnPropertyChanged(nameof(ShowActivityEmptyHint));
        RefreshActivityPickStates();
    }

    /// <summary>
    /// 装上一批活动候选（按 ID 去重；已结束的排在最后 —— 用户要找的是还能参加的活动）。
    /// 不碰网络：封面加载由命令在后台单独发起（冒烟造景因此零网络）。
    /// </summary>
    public void SetActivityOptions(IEnumerable<PlatformActivity> items)
    {
        ActivityOptions.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = items
            .Where(a => !string.IsNullOrWhiteSpace(a.Id) && seen.Add(a.Id))
            .OrderBy(a => a.IsEnded)   // false(进行中) 在前
            .ToList();
        foreach (var a in ordered)
            ActivityOptions.Add(new ActivityCardViewModel { Activity = a });
        OnPropertyChanged(nameof(HasActivityOptions));
        RefreshActivityPickStates();
    }

    /// <summary>候选卡上的「选择 / 已选择」跟着已选活动走（按 ID 匹配，活动名可能被平台改过）</summary>
    public void RefreshActivityPickStates()
    {
        foreach (var card in ActivityOptions)
            card.IsSelected = Params.Activities.Any(a => a.Id == card.Activity.Id);
    }

    /// <summary>
    /// 后台加载候选封面（封面失败只显示首字占位，**永不抛**）。
    /// 命令在拿到活动列表后 fire-and-forget 调用；冒烟造景不带封面 URL，不触网。
    /// </summary>
    public async Task LoadActivityCoversAsync()
    {
        var referer = Services.ActivityCoverLoader.RefererFor(Target.PlatformKey);
        var pending = ActivityOptions
            .Where(c => c.HasCoverUrl)
            .Select(async card =>
            {
                try
                {
                    card.Cover = await Services.ActivityCoverLoader.LoadAsync(
                        card.Activity.CoverUrl, referer);
                }
                catch (Exception ex)
                {
                    // 加载器本身不抛；这里只兜「线程被取消」之类的意外，绝不让封面拖垮列表
                    Log.Debug(ex, "[Publish] 活动封面加载异常");
                }
            })
            .ToList();
        if (pending.Count > 0) await Task.WhenAll(pending);
    }

    [ObservableProperty]
    private bool _isOn = true;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// 「开发者选项」折叠区（原始 JSON 透传）的展开态。**六个平台卡共用这一个** ——
    /// 一张卡 = 一个平台账号，同一时刻只会显示其中一个平台块；默认收起，
    /// 行重建（换草稿 / 重进编辑器）即复位。
    /// </summary>
    [ObservableProperty]
    private bool _devExpanded;

    /// <summary>被本条覆盖过账号预设 —— 摘要行左侧加品牌紫竖条</summary>
    [ObservableProperty]
    private bool _isOverridden;

    // ═══ 摘要行（可见性 · 合集 · 位置）═══

    private string _summaryText = "";
    public string SummaryText { get => _summaryText; set => SetProperty(ref _summaryText, value); }

    /// <summary>该平台被全局可见性回退（如抖音不支持「只给谁看」）时的说明行</summary>
    private string? _visibilityNote;
    public string? VisibilityNote { get => _visibilityNote; set { SetProperty(ref _visibilityNote, value); OnPropertyChanged(nameof(HasVisibilityNote)); } }
    public bool HasVisibilityNote => !string.IsNullOrEmpty(_visibilityNote);

    // ═══ 长尾开关（直接读写 Params）═══

    public bool ShowInNearby
    {
        get => Params.ShowInNearby;
        set { if (Params.ShowInNearby == value) return; Params.ShowInNearby = value; OnPropertyChanged(); }
    }

    public bool AllowSameFrame
    {
        get => Params.AllowSameFrame;
        set { if (Params.AllowSameFrame == value) return; Params.AllowSameFrame = value; OnPropertyChanged(); }
    }

    /// <summary>该账号实际生效的原创（账号覆盖 &gt; 全局）—— 2026-11-06 起 UI 开关在全局区，
    /// 这里只用于摘要与「转载」联动展示。</summary>
    public bool IsOriginal
    {
        get => Params.IsOriginal ?? GlobalIsOriginal;
        set
        {
            if (Params.IsOriginal == value) return;
            Params.IsOriginal = value;
            OnPropertyChanged();
            // 原创声明与「内容为转载」联动（老版 HomeViewModel.cs:2295 / :2288）：
            // 开启后「内容为转载」不可选 —— 这里只更新可点态，实际互斥由预检兜底
            OnPropertyChanged(nameof(IsRepostDisabledByOriginal));
            RefreshSummary();
        }
    }

    /// <summary>全局原创开关（编辑器里改 → 每行摘要跟着变）</summary>
    public bool GlobalIsOriginal { get; set; }

    public string? HotspotSentence
    {
        get => Params.HotspotSentence;
        set
        {
            if (Params.HotspotSentence == value) return;
            Params.HotspotSentence = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasHotspot));
            OnPropertyChanged(nameof(HotspotDisplay));
        }
    }

    // ═══ 抖音热点词选择器（2026-11-10：平台接口 DouyinHotspotService）═══
    // 「热点词」不是自由文本 —— 平台要求必须是它自己的热词（common.hot_sentence），
    // 所以这里改成「搜索 / 热门 → 点选」的选择器；不选 = 不参与热点（hashtag_source 走默认）。

    [ObservableProperty] private bool _isHotspotPanelOpen;
    [ObservableProperty] private string _hotspotQuery = "";
    [ObservableProperty] private string _hotspotStatusText = "";
    [ObservableProperty] private bool _hotspotStatusIsError;
    [ObservableProperty] private bool _isHotspotBusy;

    public ObservableCollection<HotspotPickViewModel> HotspotCandidates { get; } = [];
    public bool HasHotspotCandidates => HotspotCandidates.Count > 0;
    public bool HasHotspotStatus => HotspotStatusText.Length > 0;
    public bool HasHotspot => !string.IsNullOrWhiteSpace(Params.HotspotSentence);
    public string HotspotDisplay => Params.HotspotSentence is { Length: > 0 } w ? w : "未选择";

    partial void OnHotspotStatusTextChanged(string value) => OnPropertyChanged(nameof(HasHotspotStatus));

    /// <summary>替换候选列表（空结果同样清空 —— 不要让上一次的旧列表留在面板里）。</summary>
    public void SetHotspotCandidates(IReadOnlyList<HotspotItem> items)
    {
        HotspotCandidates.Clear();
        foreach (var item in items)
            HotspotCandidates.Add(new HotspotPickViewModel { Item = item });
        OnPropertyChanged(nameof(HasHotspotCandidates));
    }

    // ═══ 抖音背景音乐选择器（2026-11-10：平台接口 DouyinMusicService）═══
    // 背景音乐同样只认平台自己的音乐库 —— 手填一个音乐 ID 既不友好也没法验证。
    // 选中结果仍然只写 PlatformParams.MusicId（wire 不变），这里只补「搜索 / 列表 → 点选」。

    [ObservableProperty] private bool _isMusicPanelOpen;
    [ObservableProperty] private string _musicQuery = "";
    [ObservableProperty] private string _musicStatusText = "";
    [ObservableProperty] private bool _musicStatusIsError;
    [ObservableProperty] private bool _isMusicBusy;

    /// <summary>还有下一页（控制「加载更多」；平台说没有 / 游标原地打转 / 请求失败 → 关按钮）</summary>
    [ObservableProperty] private bool _musicHasMore;

    /// <summary>下一页游标（上一页响应的 <c>cursor</c>；页面同款翻页字段。不落盘、不进 wire）</summary>
    public long MusicNextCursor { get; private set; }

    /// <summary>上一次列表请求的来源 —— 「加载更多」要按同一来源重放，绝不能读当前输入框</summary>
    public bool MusicSourceIsSearch => _musicSourceIsSearch;
    public string MusicSourceKeyword => _musicSourceKeyword;
    public MusicCategory? MusicSourceCategory => _musicSourceCategory;

    private bool _musicSourceIsSearch;
    private string _musicSourceKeyword = "";
    private MusicCategory? _musicSourceCategory;

    public ObservableCollection<MusicPickViewModel> MusicCandidates { get; } = [];
    public bool HasMusicCandidates => MusicCandidates.Count > 0;
    public bool HasMusicStatus => MusicStatusText.Length > 0;

    /// <summary>
    /// 音乐分类页签（平台 <c>music/category</c> 下发，发布页面的二级页签）。
    /// 首次拉「推荐」时填充；切换页签只重拉列表，不重复拉分类。
    /// </summary>
    public ObservableCollection<MusicCategoryPickViewModel> MusicCategories { get; } = [];
    public bool HasMusicCategories => MusicCategories.Count > 0;
    public bool HasMusic => !string.IsNullOrWhiteSpace(Params.MusicId);

    /// <summary>
    /// 已选音乐的展示文本。选过（有曲名）时显示「歌名 — 作者」；
    /// 只有 ID（预设 / params.txt 带进来）时显示 ID，绝不假装知道是哪首歌。
    /// </summary>
    public string MusicDisplay => !HasMusic
        ? "未选择"
        // 预设 / params.txt 会整对象改写 Params（绕过 MusicIdText setter）——曲名标签只在
        // 标签记的 ID 与当前 ID 一致时才可信，否则退回显示 ID，绝不把旧歌名安到新 ID 上
        : (_pickedMusicId == Params.MusicId ? _pickedMusicLabel : null) ?? $"ID {Params.MusicId}";

    private string? _pickedMusicId;
    private string? _pickedMusicLabel;

    partial void OnMusicStatusTextChanged(string value) => OnPropertyChanged(nameof(HasMusicStatus));

    /// <summary>
    /// 替换候选列表（空结果同样清空 —— 不要让上一次的旧列表留在面板里），
    /// 同时记下这一页的来源与游标，供「加载更多」按同一请求重放。
    /// </summary>
    public void SetMusicCandidates(
        IReadOnlyList<MusicItem> items,
        long nextCursor = 0,
        bool hasMore = false,
        bool sourceIsSearch = false,
        string sourceKeyword = "",
        MusicCategory? sourceCategory = null)
    {
        MusicCandidates.Clear();
        foreach (var item in items)
            MusicCandidates.Add(new MusicPickViewModel { Item = item });
        MusicNextCursor = nextCursor;
        MusicHasMore = hasMore;
        _musicSourceIsSearch = sourceIsSearch;
        _musicSourceKeyword = sourceKeyword;
        _musicSourceCategory = sourceCategory;
        OnPropertyChanged(nameof(HasMusicCandidates));
    }

    /// <summary>
    /// 追加下一页候选，返回**新增**条数（同 ID 跳过 —— 平台游标回指同一页时不会叠出重复行）。
    /// 游标回到当前这份（或平台没给新游标）一律判「到底了」，防「加载更多」原地打转。
    /// </summary>
    public int AppendMusicCandidates(
        IReadOnlyList<MusicItem> items, long nextCursor, bool hasMore)
    {
        var added = 0;
        foreach (var item in items)
        {
            if (MusicCandidates.Any(c => c.Item.Id == item.Id)) continue;
            MusicCandidates.Add(new MusicPickViewModel { Item = item });
            added++;
        }

        MusicHasMore = hasMore && nextCursor != MusicNextCursor;
        MusicNextCursor = nextCursor;
        OnPropertyChanged(nameof(HasMusicCandidates));
        return added;
    }

    /// <summary>替换分类页签（只在首次拉推荐时调用；已填充后切换页签不重复拉）。</summary>
    public void SetMusicCategories(IReadOnlyList<MusicCategory> categories)
    {
        MusicCategories.Clear();
        foreach (var category in categories)
            MusicCategories.Add(new MusicCategoryPickViewModel { Category = category });
        OnPropertyChanged(nameof(HasMusicCategories));
    }

    /// <summary>高亮当前分类页签（其余取消）。传 null = 无页签高亮（搜索结果不属于任何分类）。</summary>
    public void MarkMusicCategory(MusicCategory? category)
    {
        foreach (var pick in MusicCategories)
            pick.IsSelected = category is not null
                && pick.Category.CategoryId == category.CategoryId
                && pick.Category.Type == category.Type;
    }

    /// <summary>清除页签高亮（关键词搜索后回到「无分类」态）。</summary>
    public void ClearMusicCategorySelection() => MarkMusicCategory(null);

    /// <summary>选中一首歌：写 music_id + 记住展示用的「歌名 — 作者」。</summary>
    public void ApplyPickedMusic(MusicItem item)
    {
        _pickedMusicId = item.Id;
        _pickedMusicLabel = string.IsNullOrWhiteSpace(item.Author)
            ? item.Title
            : $"{item.Title} — {item.Author}";
        MusicIdText = item.Id;   // setter 里 v == _pickedMusicId，不会把标签清掉
        OnPropertyChanged(nameof(MusicDisplay));
    }

    /// <summary>清除已选音乐。</summary>
    public void ClearMusic()
    {
        _pickedMusicId = null;
        _pickedMusicLabel = null;
        MusicIdText = "";
        OnPropertyChanged(nameof(MusicDisplay));
    }

    // ═══ 抖音「添加标签」选择器（2026-11-11 抓包：影视演艺 / 游戏手柄 / 标记万物 / 小程序）═══
    // 页面同一个 anchor 是**单选**（四类互斥；位置也走它，仍由左栏位置卡供）。
    // 小程序 = 粘贴分享链接搜索（anchor/search，anchor_type=4）；发布体额外带
    // anchor_business_type + anchor_content（见 DouyinAnchorService）。
    // 选中前先过平台 anchor/check —— 平台说不行就不进发布体。

    [ObservableProperty] private bool _isAnchorPanelOpen;
    [ObservableProperty] private string _anchorQuery = "";
    [ObservableProperty] private string _anchorStatusText = "";
    [ObservableProperty] private bool _anchorStatusIsError;
    [ObservableProperty] private bool _isAnchorBusy;

    /// <summary>四类标签（默认选中影视演艺；能力位拉到后按平台可用性禁用）。</summary>
    public ObservableCollection<AnchorTypePickViewModel> AnchorTypes { get; } =
    [
        new() { Type = DouyinAnchorTypes.Movie, Name = DouyinAnchorTypes.Label(DouyinAnchorTypes.Movie), IsSelected = true },
        new() { Type = DouyinAnchorTypes.Game, Name = DouyinAnchorTypes.Label(DouyinAnchorTypes.Game) },
        new() { Type = DouyinAnchorTypes.Spu, Name = DouyinAnchorTypes.Label(DouyinAnchorTypes.Spu) },
        new() { Type = DouyinAnchorTypes.Applet, Name = DouyinAnchorTypes.Label(DouyinAnchorTypes.Applet) },
    ];

    public ObservableCollection<AnchorPickViewModel> AnchorCandidates { get; } = [];
    public bool HasAnchorCandidates => AnchorCandidates.Count > 0;
    public bool HasAnchorStatus => AnchorStatusText.Length > 0;
    public bool HasAnchor => Params.Douyin?.Anchor is not null;

    /// <summary>能力位（<c>anchor/list</c>）是否已拉到 —— 只拉一次；失败不置位，下次展开再试。</summary>
    public bool AnchorCapabilitiesLoaded { get; set; }

    /// <summary>「影视演艺 · 流浪地球」/「未选择」。</summary>
    public string AnchorDisplay => Params.Douyin?.Anchor is { } anchor
        ? $"{DouyinAnchorTypes.Label(anchor.Type)} · {anchor.Title}"
        : "未选择";

    partial void OnAnchorStatusTextChanged(string value) => OnPropertyChanged(nameof(HasAnchorStatus));

    /// <summary>当前选中的标签类型。</summary>
    public int SelectedAnchorType =>
        AnchorTypes.FirstOrDefault(t => t.IsSelected)?.Type ?? DouyinAnchorTypes.Movie;

    /// <summary>切换类型（单选高亮；调用方负责清空候选并重拉）。</summary>
    public void SelectAnchorType(int type)
    {
        foreach (var pick in AnchorTypes)
            pick.IsSelected = pick.Type == type;
        OnPropertyChanged(nameof(SelectedAnchorType));
    }

    /// <summary>
    /// 应用能力位（<c>anchor/list</c>）：禁用的类型连原因一起存（平台给的是现成文案）。
    /// 当前选中项被禁用时自动切到第一个可用项。
    /// </summary>
    public void SetAnchorCapabilities(DouyinAnchorCapabilities caps)
    {
        foreach (var pick in AnchorTypes)
        {
            pick.IsEnabled = caps.IsAvailable(pick.Type);
            pick.Reason = caps.ReasonFor(pick.Type) ?? "";
        }

        if (AnchorTypes.FirstOrDefault(t => t.IsSelected) is { IsEnabled: false } selected)
        {
            if (AnchorTypes.FirstOrDefault(t => t.IsEnabled) is { } fallback)
                SelectAnchorType(fallback.Type);
            else
                AnchorStatusText = selected.Reason.Length > 0
                    ? "本账号暂不可用任何标签：" + selected.Reason
                    : "本账号暂不可用任何标签";
        }
    }

    /// <summary>替换候选（空结果同样清空 —— 不留上一次的旧列表）。</summary>
    public void SetAnchorCandidates(IReadOnlyList<DouyinAnchorCandidate> items)
    {
        AnchorCandidates.Clear();
        foreach (var item in items)
            AnchorCandidates.Add(new AnchorPickViewModel { Item = item });
        OnPropertyChanged(nameof(HasAnchorCandidates));
    }

    /// <summary>选中一个标签（已过 <c>anchor/check</c>）。</summary>
    public void ApplyPickedAnchor(DouyinAnchorCandidate item)
    {
        DouyinGroup.Anchor = new DouyinAnchorRef
        {
            Type = item.Type,
            Id = item.Id,
            Title = item.Title,
            Content = item.Content,
        };
        OnPropertyChanged(nameof(HasAnchor));
        OnPropertyChanged(nameof(AnchorDisplay));
        RefreshSummary();
    }

    /// <summary>清除已选标签。</summary>
    public void ClearAnchor()
    {
        if (Params.Douyin?.Anchor is null) return;
        DouyinGroup.Anchor = null;
        OnPropertyChanged(nameof(HasAnchor));
        OnPropertyChanged(nameof(AnchorDisplay));
        RefreshSummary();
    }

    // ═══ 抖音 @好友 选择器（2026-11-11 补抓：discover/search）═══
    // 发布体三件套：common.mentions（uid 数组）+ 正文「@昵称」+ text_extra type=0 条目。
    // 选择器只写 DouyinExtras.MentionUsers；正文补写与 text_extra 定位由
    // PublishPayloadBuilder → DouyinMentionRules 统一做（预检同源），wire 形状见 B1 抓包。
    // 旧开发者选项的原始 mentions JSON 仍然可用：列表为空时按它种子化展示（uid 当昵称），
    // 选择器一动手就清掉原始串，避免两处口径打架。

    [ObservableProperty] private bool _isDouyinMentionPanelOpen;
    [ObservableProperty] private bool _isDouyinMentionBusy;
    [ObservableProperty] private string _douyinMentionQuery = "";
    [ObservableProperty] private string _douyinMentionStatusText = "";
    [ObservableProperty] private bool _douyinMentionStatusIsError;

    public ObservableCollection<DouyinMentionPickViewModel> DouyinMentionCandidates { get; } = [];
    public ObservableCollection<DouyinMentionPickViewModel> DouyinMentions { get; } = [];

    public bool HasDouyinMentionCandidates => DouyinMentionCandidates.Count > 0;
    public bool HasDouyinMentionStatus => DouyinMentionStatusText.Length > 0;
    public bool HasDouyinMentions => DouyinMentions.Count > 0;

    partial void OnDouyinMentionStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasDouyinMentionStatus));

    /// <summary>@好友行展示：未选择 / 「已选 @A、@B」（太长由 TextTrimming 收尾）。</summary>
    public string DouyinMentionDisplay => DouyinMentions.Count == 0
        ? "未选择"
        : "已选 " + string.Join("、", DouyinMentions.Select(m => "@" + m.Nickname));

    /// <summary>
    /// 从存档重新种子化已选列表（草稿重建 / 套用预设 / 换账号时调用）。
    /// 结构化列表优先；旧档只有原始 mentions JSON 时按 uid 展示（昵称=uid）。
    /// </summary>
    public void RefreshDouyinMentions()
    {
        DouyinMentions.Clear();
        var stored = Params.Douyin?.MentionUsers;
        var seeds = stored is { Count: > 0 }
            ? stored
            : DouyinMentionRules.ParseLegacyUids(Params.Douyin?.Mentions);
        foreach (var user in seeds)
        {
            DouyinMentions.Add(new DouyinMentionPickViewModel
            {
                User = new DouyinMentionUser(user.Uid, user.Nickname, user.UniqueId,
                    AvatarUrl: user.AvatarUrl),
                IsPicked = true,
            });
        }
        SyncDouyinMentionPickState();
        OnPropertyChanged(nameof(HasDouyinMentions));
        OnPropertyChanged(nameof(DouyinMentionDisplay));
    }

    /// <summary>替换候选列表（搜索是替换式；空结果同样清空旧候选）。</summary>
    public void SetDouyinMentionCandidates(IReadOnlyList<DouyinMentionUser> users)
    {
        DouyinMentionCandidates.Clear();
        foreach (var user in users)
            DouyinMentionCandidates.Add(new DouyinMentionPickViewModel { User = user });
        SyncDouyinMentionPickState();
        OnPropertyChanged(nameof(HasDouyinMentionCandidates));
    }

    /// <summary>加入一个 @好友；已在列表里则忽略。返回是否新增。</summary>
    public bool AddDouyinMention(DouyinMentionUser user)
    {
        if (DouyinMentions.Any(m => m.User.Uid == user.Uid)) return false;
        DouyinMentions.Add(new DouyinMentionPickViewModel { User = user, IsPicked = true });
        SyncDouyinMentionPickState();
        RebuildDouyinMentions();
        OnPropertyChanged(nameof(HasDouyinMentions));
        OnPropertyChanged(nameof(DouyinMentionDisplay));
        return true;
    }

    /// <summary>移除一个已选 @好友。</summary>
    public void RemoveDouyinMention(DouyinMentionPickViewModel pick)
    {
        if (!DouyinMentions.Remove(pick)) return;
        SyncDouyinMentionPickState();
        RebuildDouyinMentions();
        OnPropertyChanged(nameof(HasDouyinMentions));
        OnPropertyChanged(nameof(DouyinMentionDisplay));
    }

    /// <summary>清空已选 @好友（发布体不再下发 common.mentions）。</summary>
    public void ClearDouyinMentions()
    {
        if (DouyinMentions.Count == 0) return;
        DouyinMentions.Clear();
        SyncDouyinMentionPickState();
        RebuildDouyinMentions();
        OnPropertyChanged(nameof(HasDouyinMentions));
        OnPropertyChanged(nameof(DouyinMentionDisplay));
    }

    /// <summary>候选行的「选 / 已选」态按已选列表（用户 ID）同步。</summary>
    private void SyncDouyinMentionPickState()
    {
        foreach (var c in DouyinMentionCandidates)
            c.IsPicked = DouyinMentions.Any(m => m.User.Uid == c.User.Uid);
    }

    /// <summary>已选列表 → <see cref="DouyinExtras.MentionUsers"/>（空 = null，发布体不出现）。</summary>
    private void RebuildDouyinMentions()
    {
        var list = DouyinMentions.Select(m => new DouyinMentionRef
        {
            Uid = m.User.Uid,
            Nickname = m.User.Nickname,
            UniqueId = m.User.UniqueId,
            AvatarUrl = m.User.AvatarUrl,
        }).ToList();
        DouyinGroup.MentionUsers = list.Count == 0 ? null : list;
        // 选择器是唯一写者：旧原始串清掉，否则「清空选择」后它还会继续下发
        DouyinGroup.Mentions = null;
        RefreshSummary();
    }

    // ═══ 抖音长尾（阶段 2.1）—— 文本代理，解析规则见 PublishFieldText ═══

    /// <summary>背景音乐 ID（留空 = 不选音乐）</summary>
    public string MusicIdText
    {
        get => Params.MusicId ?? "";
        set
        {
            var v = value.Trim();
            var cur = Params.MusicId ?? "";
            if (cur == v) return;
            Params.MusicId = v.Length == 0 ? null : v;
            // ID 被外部改写（预设 / params.txt / 手改存档）→ 旧曲名标签已经对不上了
            if (_pickedMusicId != v)
            {
                _pickedMusicId = null;
                _pickedMusicLabel = null;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasMusic));
            OnPropertyChanged(nameof(MusicDisplay));
            RefreshSummary();
        }
    }

    /// <summary>章节（每行 <c>mm:ss 标题</c>）</summary>
    public string ChaptersText
    {
        get => PublishFieldText.FormatChapters(Params.Chapters);
        set
        {
            var list = PublishFieldText.ParseChapters(value);
            if (Params.Chapters.SequenceEqual(list)) return;
            Params.Chapters = list;
            OnPropertyChanged();
        }
    }

    /// <summary>同步到今日头条 / 西瓜（<c>sync.should_sync</c> + <c>sync_to_toutiao</c>）</summary>
    public bool SyncToToutiao
    {
        get => Params.SyncTargets.Any(t => string.Equals(t, "toutiao", StringComparison.OrdinalIgnoreCase));
        set
        {
            if (SyncToToutiao == value) return;
            Params.SyncTargets.RemoveAll(t => string.Equals(t, "toutiao", StringComparison.OrdinalIgnoreCase));
            if (value) Params.SyncTargets.Add("toutiao");
            OnPropertyChanged();
        }
    }

    /// <summary>自定义封面裁剪宽（px）</summary>
    public string CoverCropWidthText
    {
        get => PublishFieldText.FormatInt(Params.Douyin?.CoverCropWidth);
        set
        {
            var v = PublishFieldText.ParseInt(value);
            if (Params.Douyin?.CoverCropWidth == v) return;
            DouyinGroup.CoverCropWidth = v;
            OnPropertyChanged();
        }
    }

    /// <summary>自定义封面裁剪高（px）</summary>
    public string CoverCropHeightText
    {
        get => PublishFieldText.FormatInt(Params.Douyin?.CoverCropHeight);
        set
        {
            var v = PublishFieldText.ParseInt(value);
            if (Params.Douyin?.CoverCropHeight == v) return;
            DouyinGroup.CoverCropHeight = v;
            OnPropertyChanged();
        }
    }

    // ═══ 抖音封面双槽（B1 抓包 2026-11-11）═══
    // 竖版 3:4 = 全局主封面（cover.poster + custom_cover_image_width/height）；
    // 横版 4:3 = 本槽（cover.horizontal_custom_cover_image_uri），空 = 不单独下发。

    public bool HasDouyinLandscapeCover =>
        !string.IsNullOrWhiteSpace(Params.Douyin?.CoverLandscapePath);

    /// <summary>横版封面预览源（本地路径；未选 = null → 灰块）</summary>
    public string? DouyinLandscapeCoverPathForPreview => Params.Douyin?.CoverLandscapePath;

    /// <summary>横版封面状态行：文件名 / 「未选择（不单独下发）」</summary>
    public string DouyinLandscapeCoverDisplay => Params.Douyin?.CoverLandscapePath is { Length: > 0 } path
        ? Path.GetFileName(path)
        : "未选择（不单独下发）";

    /// <summary>选中横版 4:3 封面（文件选择由页面 VM 的 CoverPicker 提供）。</summary>
    public void ApplyDouyinLandscapeCover(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        DouyinGroup.CoverLandscapePath = path;
        OnPropertyChanged(nameof(HasDouyinLandscapeCover));
        OnPropertyChanged(nameof(DouyinLandscapeCoverPathForPreview));
        OnPropertyChanged(nameof(DouyinLandscapeCoverDisplay));
        RefreshSummary();
    }

    /// <summary>清除横版 4:3 封面（发布体不再下发 horizontal_custom_cover_image_uri）。</summary>
    public void ClearDouyinLandscapeCover()
    {
        if (Params.Douyin?.CoverLandscapePath is null) return;
        DouyinGroup.CoverLandscapePath = null;
        OnPropertyChanged(nameof(HasDouyinLandscapeCover));
        OnPropertyChanged(nameof(DouyinLandscapeCoverPathForPreview));
        OnPropertyChanged(nameof(DouyinLandscapeCoverDisplay));
        RefreshSummary();
    }

    /// <summary>合集内排序（0 = 按发布时间顺序）</summary>
    public string CollectionOrderText
    {
        get => PublishFieldText.FormatInt(Params.CollectionOrder);
        set
        {
            var v = PublishFieldText.ParseInt(value);
            if (Params.CollectionOrder == v) return;
            Params.CollectionOrder = v;
            OnPropertyChanged();
        }
    }

    /// <summary>该账号是否带合集 —— 决定「合集排序」输入框显隐</summary>
    public bool HasCollection => !string.IsNullOrWhiteSpace(Params.CollectionId);

    /// <summary>DouyinExtras 是懒建的：老预设没有整组时，用户一动字段就补一组</summary>
    private DouyinExtras DouyinGroup => Params.Douyin ??= new DouyinExtras();

    // ═══ 快手长尾（阶段 2.2）—— 通知 / 封面文字 / PK 封面 / 画质 AI / 禁止二创 / 领域 / 短剧 / 章节 / 共创 ═══

    /// <summary>发布后通知粉丝（<c>notifyResult</c>：1 = 通知 / 0 = 不通知）</summary>
    public bool NotifyFollowers
    {
        get => Params.NotifyFollowers;
        set { if (Params.NotifyFollowers == value) return; Params.NotifyFollowers = value; OnPropertyChanged(); }
    }

    /// <summary>封面文字 —— <c>coverTitle</c>（公共封面标题的快手落点）</summary>
    public string KuaishouCoverTitleText
    {
        get => Params.CoverTitle ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.CoverTitle ?? "") == v) return;
            Params.CoverTitle = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>PK 封面 key</summary>
    public string PkCoverKeyText
    {
        get => Params.Kuaishou?.PkCoverKey ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Kuaishou?.PkCoverKey ?? "") == v) return;
            KuaishouGroup.PkCoverKey = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>PK 封面帧（界面秒 / 模型毫秒；空 = 未设置 → 提交端 -1）</summary>
    public string PkCoverTimeStampText
    {
        get => Params.Kuaishou?.PkCoverTimeStamp is { } ts && ts >= 0
            ? PublishFieldText.FormatMsAsSeconds(ts) : "";
        set
        {
            var v = PublishFieldText.ParseSecondsToMs(value);
            if (Params.Kuaishou?.PkCoverTimeStamp == v) return;
            KuaishouGroup.PkCoverTimeStamp = v;
            OnPropertyChanged();
        }
    }

    /// <summary>PK 封面类型（抓包默认 1）</summary>
    public string PkCoverTypeText
    {
        get => PublishFieldText.FormatInt(Params.Kuaishou?.PkCoverType);
        set
        {
            var v = PublishFieldText.ParseInt(value);
            if (Params.Kuaishou?.PkCoverType == v) return;
            KuaishouGroup.PkCoverType = v;
            OnPropertyChanged();
        }
    }

    /// <summary>PK 封面尺寸（平台原样字符串）</summary>
    public string PkCoverSizeText
    {
        get => Params.Kuaishou?.PkCoverSize ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Kuaishou?.PkCoverSize ?? "") == v) return;
            KuaishouGroup.PkCoverSize = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>个人主页封面 key</summary>
    public string ProfileCoverKeyText
    {
        get => Params.Kuaishou?.ProfileCoverKey ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Kuaishou?.ProfileCoverKey ?? "") == v) return;
            KuaishouGroup.ProfileCoverKey = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    public bool RiseQuality
    {
        get => Params.Kuaishou?.RiseQuality ?? false;
        set
        {
            if ((Params.Kuaishou?.RiseQuality ?? false) == value) return;
            KuaishouGroup.RiseQuality = value;
            OnPropertyChanged();
        }
    }

    public bool UseAiCaption
    {
        get => Params.Kuaishou?.UseAiCaption ?? false;
        set
        {
            if ((Params.Kuaishou?.UseAiCaption ?? false) == value) return;
            KuaishouGroup.UseAiCaption = value;
            OnPropertyChanged();
        }
    }

    public bool UseAiCaptionCover
    {
        get => Params.Kuaishou?.UseAiCaptionCover ?? false;
        set
        {
            if ((Params.Kuaishou?.UseAiCaptionCover ?? false) == value) return;
            KuaishouGroup.UseAiCaptionCover = value;
            OnPropertyChanged();
        }
    }

    public bool UseAiCover
    {
        get => Params.Kuaishou?.UseAiCover ?? false;
        set
        {
            if ((Params.Kuaishou?.UseAiCover ?? false) == value) return;
            KuaishouGroup.UseAiCover = value;
            OnPropertyChanged();
        }
    }

    public bool UseIdealTime
    {
        get => Params.Kuaishou?.UseIdealTime ?? false;
        set
        {
            if ((Params.Kuaishou?.UseIdealTime ?? false) == value) return;
            KuaishouGroup.UseIdealTime = value;
            OnPropertyChanged();
        }
    }

    public bool DisallowRecreation
    {
        get => Params.Kuaishou?.DisallowRecreation ?? false;
        set
        {
            if ((Params.Kuaishou?.DisallowRecreation ?? false) == value) return;
            KuaishouGroup.DisallowRecreation = value;
            OnPropertyChanged();
        }
    }

    /// <summary>专属领域 —— <c>domain</c>（平台列表值就是领域名本身）</summary>
    public string DomainText
    {
        get => Params.Kuaishou?.Domain ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Kuaishou?.Domain ?? "") == v) return;
            KuaishouGroup.Domain = v.Length == 0 ? null : v;
            // 领域被外部改写（预设 / 导入存档）→ 面板里那套「二级领域属于谁」的上下文已经对不上
            if (!string.Equals(KsDomainSelectedName, v, StringComparison.Ordinal))
                ResetKsSecondDomainCandidates();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasKsDomain));
            OnPropertyChanged(nameof(KsDomainDisplay));
            OnPropertyChanged(nameof(HasKsDomainOrSecond));
        }
    }

    /// <summary>二级领域 —— <c>secondDomain</c>（必须配合专属领域一起下发）</summary>
    public string SecondDomainText
    {
        get => Params.Kuaishou?.SecondDomain ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Kuaishou?.SecondDomain ?? "") == v) return;
            KuaishouGroup.SecondDomain = v.Length == 0 ? null : v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasKsSecondDomain));
            OnPropertyChanged(nameof(KsSecondDomainDisplay));
            OnPropertyChanged(nameof(HasKsDomainOrSecond));
        }
    }

    // ═══ 快手「专属领域 / 二级领域」选择器（2026-11-10：upload/domain/list）═══
    // 平台领域是两级列表（专属领域 → 二级领域），手填字符串既记不住也验证不了。
    // 选中仍只写 KuaishouExtras.Domain / SecondDomain（wire 不变：两个字符串）。

    [ObservableProperty] private bool _isKsDomainPanelOpen;
    [ObservableProperty] private bool _isKsDomainBusy;
    [ObservableProperty] private string _ksDomainStatusText = "";
    [ObservableProperty] private bool _ksDomainStatusIsError;
    [ObservableProperty] private string _ksDomainSelectedName = "";

    public ObservableCollection<KsDomainPickViewModel> KsDomainCandidates { get; } = [];
    public ObservableCollection<KsSecondDomainPickViewModel> KsSecondDomainCandidates { get; } = [];

    public bool HasKsDomainCandidates => KsDomainCandidates.Count > 0;
    public bool HasKsSecondDomainCandidates => KsSecondDomainCandidates.Count > 0;
    public bool HasKsDomainStatus => KsDomainStatusText.Length > 0;

    public bool HasKsDomain => !string.IsNullOrWhiteSpace(Params.Kuaishou?.Domain);
    public bool HasKsSecondDomain => !string.IsNullOrWhiteSpace(Params.Kuaishou?.SecondDomain);
    public bool HasKsDomainOrSecond => HasKsDomain || HasKsSecondDomain;

    /// <summary>专属领域行展示：未选显示「未选择」；预设/params 带进来的值原样显示。</summary>
    public string KsDomainDisplay => HasKsDomain ? Params.Kuaishou!.Domain! : "未选择";

    /// <summary>二级领域行展示（同上，不假装知道预设里的值是什么）。</summary>
    public string KsSecondDomainDisplay =>
        HasKsSecondDomain ? Params.Kuaishou!.SecondDomain! : "未选择";

    /// <summary>面板里二级领域列表的标题（跟着当前点选的专属领域走）。</summary>
    public string KsSecondDomainCaption => KsDomainSelectedName.Length > 0
        ? $"二级领域（{KsDomainSelectedName}）"
        : "二级领域";

    partial void OnKsDomainStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasKsDomainStatus));

    partial void OnKsDomainSelectedNameChanged(string value) =>
        OnPropertyChanged(nameof(KsSecondDomainCaption));

    /// <summary>装专属领域候选；若当前已有 domain 值，顺手把它的二级领域摊开（面板重开不丢上下文）。</summary>
    public void SetKsDomainCandidates(IReadOnlyList<KuaishouDomain> domains)
    {
        KsDomainCandidates.Clear();
        foreach (var d in domains)
            KsDomainCandidates.Add(new KsDomainPickViewModel { Domain = d });
        OnPropertyChanged(nameof(HasKsDomainCandidates));

        var current = Params.Kuaishou?.Domain;
        if (!string.IsNullOrWhiteSpace(current)
            && domains.FirstOrDefault(d => string.Equals(d.Name, current, StringComparison.Ordinal))
                is { } match)
            ShowKsSecondDomains(match);
    }

    /// <summary>选中一个专属领域：写 <c>domain</c>，并摊开它的二级领域。</summary>
    public void ApplyKsFirstDomain(KuaishouDomain domain)
    {
        var switching = !string.Equals(Params.Kuaishou?.Domain, domain.Name, StringComparison.Ordinal);
        ShowKsSecondDomains(domain);          // 先摆正「二级属于谁」，再写值（setter 不会误清）
        DomainText = domain.Name;
        if (switching && HasKsSecondDomain)
            SecondDomainText = "";            // 二级领域属于旧的一级 —— 换了就清掉，免得配错
    }

    /// <summary>选中一个二级领域（专属领域必须已经选好 —— UI 上二级列表只在一级选中后出现）。</summary>
    public void ApplyKsSecondDomain(string name) => SecondDomainText = name;

    /// <summary>清除两级领域（发布时不下发 domain / secondDomain）。</summary>
    public void ClearKsDomains()
    {
        DomainText = "";
        SecondDomainText = "";
        ResetKsSecondDomainCandidates();
    }

    private void ShowKsSecondDomains(KuaishouDomain domain)
    {
        KsDomainSelectedName = domain.Name;
        KsSecondDomainCandidates.Clear();
        foreach (var name in domain.SecondDomains)
            KsSecondDomainCandidates.Add(new KsSecondDomainPickViewModel { Name = name });
        OnPropertyChanged(nameof(HasKsSecondDomainCandidates));
        OnPropertyChanged(nameof(KsSecondDomainCaption));
    }

    private void ResetKsSecondDomainCandidates()
    {
        KsDomainSelectedName = "";
        KsSecondDomainCandidates.Clear();
        OnPropertyChanged(nameof(HasKsSecondDomainCandidates));
        OnPropertyChanged(nameof(KsSecondDomainCaption));
    }

    // ═══ 快手「关联热点」选择器（B3 抓包 2026-11-11：hotspot/list → hotspotId）═══
    // 平台只认自己的热点（数字 hotspotId + hotspotConnectType:1），手填 ID 既记不住也验证不了。
    // 选中写 KuaishouExtras.HotspotId / HotspotContent（后者仅展示快照，不下发）。

    [ObservableProperty] private bool _isKsHotspotPanelOpen;
    [ObservableProperty] private string _ksHotspotQuery = "";
    [ObservableProperty] private string _ksHotspotStatusText = "";
    [ObservableProperty] private bool _ksHotspotStatusIsError;
    [ObservableProperty] private bool _isKsHotspotBusy;
    [ObservableProperty] private bool _ksHotspotHasMore;

    /// <summary>下一页游标（上一页响应的 <c>nextCursor</c>；不落盘、不进 wire）</summary>
    public long KsHotspotNextCursor { get; private set; }

    /// <summary>上一次列表请求实际用的关键词（「加载更多」沿用它，不读正在编辑的输入框）</summary>
    public string KsHotspotLastQuery { get; set; } = "";

    public ObservableCollection<KsHotspotPickViewModel> KsHotspotCandidates { get; } = [];

    public bool HasKsHotspotCandidates => KsHotspotCandidates.Count > 0;
    public bool HasKsHotspotStatus => KsHotspotStatusText.Length > 0;

    public bool HasKsHotspot => Params.Kuaishou?.HotspotId is > 0;

    /// <summary>行展示：文案快照优先；只有 ID（旧档/手改）时显示 ID，不假装知道文案。</summary>
    public string KsHotspotDisplay => HasKsHotspot
        ? Params.Kuaishou!.HotspotContent is { Length: > 0 } content
            ? content
            : $"热点 {Params.Kuaishou.HotspotId}"
        : "未选择";

    partial void OnKsHotspotStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasKsHotspotStatus));

    /// <summary>
    /// 装热点候选。<paramref name="append"/> = 翻页（保留已有列表）；
    /// 找不到更多时 <paramref name="nextCursor"/> 传 0。
    /// </summary>
    public void SetKsHotspotCandidates(
        IReadOnlyList<KuaishouHotspotResult> items, long nextCursor, bool append)
    {
        if (!append) KsHotspotCandidates.Clear();
        foreach (var item in items)
            KsHotspotCandidates.Add(new KsHotspotPickViewModel { Item = item });
        KsHotspotNextCursor = nextCursor > 0 ? nextCursor : 0;
        KsHotspotHasMore = KsHotspotNextCursor > 0;
        OnPropertyChanged(nameof(HasKsHotspotCandidates));
    }

    /// <summary>选中一条热点：写 <c>hotspotId</c> + 文案快照。</summary>
    public void ApplyKsHotspot(KuaishouHotspotResult item)
    {
        KuaishouGroup.HotspotId = item.HotspotId > 0 ? item.HotspotId : null;
        KuaishouGroup.HotspotContent = KuaishouGroup.HotspotId is null ? null : item.Content;
        OnPropertyChanged(nameof(HasKsHotspot));
        OnPropertyChanged(nameof(KsHotspotDisplay));
    }

    /// <summary>清除已选热点（发布时 <c>hotspotId</c> / <c>hotspotConnectType</c> 都不下发）。</summary>
    public void ClearKsHotspot()
    {
        if (Params.Kuaishou is null) return;
        Params.Kuaishou.HotspotId = null;
        Params.Kuaishou.HotspotContent = null;
        OnPropertyChanged(nameof(HasKsHotspot));
        OnPropertyChanged(nameof(KsHotspotDisplay));
    }

    // ═══ 快手「作者服务 · 关联小程序」选择器（2026-11-15 真页面抓包）═══
    // 平台要的是 relation/list 解析出的整个 associateTaskId 对象（submit.associateTasks），
    // 所以做成「粘链接 → 解析 → 点选」；不选 = 不带 associateTasks 键。

    [ObservableProperty] private bool _isKsAssociatePanelOpen;
    [ObservableProperty] private bool _isKsAssociateBusy;
    [ObservableProperty] private string _ksAssociateLink = "";
    [ObservableProperty] private string _ksAssociateStatusText = "";
    [ObservableProperty] private bool _ksAssociateStatusIsError;

    public ObservableCollection<KsRelationPickViewModel> KsAssociateCandidates { get; } = [];

    public bool HasKsAssociateCandidates => KsAssociateCandidates.Count > 0;
    public bool HasKsAssociateStatus => KsAssociateStatusText.Length > 0;

    public bool HasKsAssociate => Params.Kuaishou?.AssociateTask is { BindId.Length: > 0 };

    /// <summary>行展示：小程序名优先，其次链接，最后 bindId（不假装知道名字）。</summary>
    public string KsAssociateDisplay => HasKsAssociate
        ? Params.Kuaishou!.AssociateTask!.Title is { Length: > 0 } title
            ? title
            : Params.Kuaishou.AssociateTask.Link is { Length: > 0 } link
                ? link
                : $"小程序 {Params.Kuaishou.AssociateTask.BindId}"
        : "未选择";

    partial void OnKsAssociateStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasKsAssociateStatus));

    /// <summary>替换候选（每次解析都是替换式；空结果同样清空旧候选）。</summary>
    public void SetKsAssociateCandidates(IReadOnlyList<KuaishouRelationItem> items)
    {
        KsAssociateCandidates.Clear();
        foreach (var item in items)
            KsAssociateCandidates.Add(new KsRelationPickViewModel { Item = item });
        OnPropertyChanged(nameof(HasKsAssociateCandidates));
    }

    /// <summary>选中一条关联项：五字段 + 展示快照一起落 <see cref="KuaishouExtras.AssociateTask"/>。</summary>
    public void ApplyKsAssociate(KuaishouRelationItem item, string link)
    {
        var task = item.Task;
        KuaishouGroup.AssociateTask = new KuaishouAssociateRef(
            task.TaskId, task.BindId, task.EntranceType, task.BindType, task.TaskType,
            Title: item.Title, Link: link);
        KsAssociateLink = link;
        OnPropertyChanged(nameof(HasKsAssociate));
        OnPropertyChanged(nameof(KsAssociateDisplay));
    }

    /// <summary>清除已关联小程序（发布时 <c>associateTasks</c> 键都不下发）。</summary>
    public void ClearKsAssociate()
    {
        if (Params.Kuaishou is null) return;
        Params.Kuaishou.AssociateTask = null;
        OnPropertyChanged(nameof(HasKsAssociate));
        OnPropertyChanged(nameof(KsAssociateDisplay));
    }

    // ═══ 快手「共同创作」选择器（2026-11-15 发布页 bundle 定案）═══
    // wire：submit.coPublishUser = [{ userId, userName, userHead, coPublishRole }]；
    // 角色枚举 -1 默认 / 0 作者 / 1 共创 / 2 出镜。选人搜索复用 at/search（bundle 证实同端点）。
    // 账号无共创权限（config.hasCoPublishQuota=false）时官方页面不显示该块，平台会拒/忽略。

    [ObservableProperty] private bool _isKsCoPublishPanelOpen;
    [ObservableProperty] private bool _isKsCoPublishBusy;
    [ObservableProperty] private string _ksCoPublishQuery = "";
    [ObservableProperty] private string _ksCoPublishStatusText = "";
    [ObservableProperty] private bool _ksCoPublishStatusIsError;

    public ObservableCollection<KsMentionPickViewModel> KsCoPublishCandidates { get; } = [];
    public ObservableCollection<KsCoPublishPickViewModel> KsCoPublishUsers { get; } = [];

    public bool HasKsCoPublishCandidates => KsCoPublishCandidates.Count > 0;
    public bool HasKsCoPublishUsers => KsCoPublishUsers.Count > 0;
    public bool HasKsCoPublishStatus => KsCoPublishStatusText.Length > 0;

    /// <summary>行展示：未选择 / 「已选 张三（共创）、李四（默认）」。</summary>
    public string KsCoPublishDisplay => KsCoPublishUsers.Count == 0
        ? "未选择"
        : "已选 " + string.Join("、", KsCoPublishUsers.Select(u => $"{u.UserName}（{u.RoleText}）"));

    partial void OnKsCoPublishStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasKsCoPublishStatus));

    /// <summary>从存档重新种子化已选共创（草稿重建 / 套用预设 / 换账号时调用）。</summary>
    public void RefreshKsCoPublishUsers()
    {
        foreach (var pick in KsCoPublishUsers) pick.RoleChanged -= OnKsCoPublishRoleChanged;
        KsCoPublishUsers.Clear();
        foreach (var stored in Params.Kuaishou?.CoPublishUsers ?? [])
        {
            if (stored.UserId <= 0 || string.IsNullOrWhiteSpace(stored.UserName)) continue;
            var pick = new KsCoPublishPickViewModel
            {
                UserId = stored.UserId,
                UserName = stored.UserName,
                UserHead = stored.UserHead,
                RoleIndex = RoleToIndex(stored.Role),
            };
            pick.RoleChanged += OnKsCoPublishRoleChanged;
            KsCoPublishUsers.Add(pick);
        }
        OnPropertyChanged(nameof(HasKsCoPublishUsers));
        OnPropertyChanged(nameof(KsCoPublishDisplay));
    }

    /// <summary>替换候选（搜索是替换式；空结果同样清空旧候选）。</summary>
    public void SetKsCoPublishCandidates(IReadOnlyList<KuaishouMentionUser> users)
    {
        KsCoPublishCandidates.Clear();
        foreach (var user in users)
            KsCoPublishCandidates.Add(new KsMentionPickViewModel { User = user });
        OnPropertyChanged(nameof(HasKsCoPublishCandidates));
    }

    /// <summary>加入一名共创；同一 userId 已选则忽略。返回是否新增。</summary>
    public bool AddKsCoPublish(KuaishouMentionUser user)
    {
        if (KsCoPublishUsers.Any(u => u.UserId == user.UserId)) return false;
        var pick = new KsCoPublishPickViewModel
        {
            UserId = user.UserId,
            UserName = user.UserName,
            UserHead = user.HeadUrl,
            RoleIndex = 0, // 默认
        };
        pick.RoleChanged += OnKsCoPublishRoleChanged;
        KsCoPublishUsers.Add(pick);
        RebuildKsCoPublish();
        return true;
    }

    /// <summary>移除一名已选共创。</summary>
    public void RemoveKsCoPublish(KsCoPublishPickViewModel pick)
    {
        if (!KsCoPublishUsers.Remove(pick)) return;
        pick.RoleChanged -= OnKsCoPublishRoleChanged;
        RebuildKsCoPublish();
    }

    /// <summary>清空共创（发布时 <c>coPublishUser</c> 为空数组）。</summary>
    public void ClearKsCoPublish()
    {
        foreach (var pick in KsCoPublishUsers) pick.RoleChanged -= OnKsCoPublishRoleChanged;
        KsCoPublishUsers.Clear();
        RebuildKsCoPublish();
    }

    private void OnKsCoPublishRoleChanged(KsCoPublishPickViewModel pick) => RebuildKsCoPublish();

    /// <summary>已选 → <c>KuaishouExtras.CoPublishUsers</c>（role 从下拉下标还原成 wire 值）。</summary>
    private void RebuildKsCoPublish()
    {
        if (Params.Kuaishou is { } ks)
        {
            ks.CoPublishUsers = KsCoPublishUsers
                .Select(u => new KuaishouCoPublishRef(u.UserId, u.UserName, u.UserHead, u.Role))
                .ToList();
        }
        OnPropertyChanged(nameof(HasKsCoPublishUsers));
        OnPropertyChanged(nameof(KsCoPublishDisplay));
    }

    /// <summary>wire 角色 → 下拉下标（-1/0/1/2 → 0/1/2/3）。</summary>
    private static int RoleToIndex(int role) => role switch
    {
        0 => 1,
        1 => 2,
        2 => 3,
        _ => 0,
    };

    // ═══ 快手 @好友 选择器（B3 抓包 2026-11-11：at/search + at/choose）═══
    // 落点不是独立字段：选中写 KuaishouExtras.MentionUsers，发布执行端把 caption 里的
    // @昵称 换成纯文本 token `@昵称(O<userId>)`（KuaishouMentionText），并对每人先调 at/choose。

    [ObservableProperty] private bool _isKsMentionPanelOpen;
    [ObservableProperty] private bool _isKsMentionBusy;
    [ObservableProperty] private string _ksMentionQuery = "";
    [ObservableProperty] private string _ksMentionStatusText = "";
    [ObservableProperty] private bool _ksMentionStatusIsError;

    public ObservableCollection<KsMentionPickViewModel> KsMentionCandidates { get; } = [];
    public ObservableCollection<KsMentionPickViewModel> KsMentions { get; } = [];

    public bool HasKsMentionCandidates => KsMentionCandidates.Count > 0;
    public bool HasKsMentionStatus => KsMentionStatusText.Length > 0;
    public bool HasKsMentions => KsMentions.Count > 0;

    partial void OnKsMentionStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasKsMentionStatus));

    /// <summary>@好友行展示：未选择 / 「已选 @A、@B」。</summary>
    public string KsMentionDisplay => KsMentions.Count == 0
        ? "未选择"
        : "已选 " + string.Join("、", KsMentions.Select(m => "@" + m.UserName));

    /// <summary>从存档重新种子化已选列表（草稿重建 / 套用预设 / 换账号时调用）。</summary>
    public void RefreshKsMentions()
    {
        KsMentions.Clear();
        foreach (var stored in Params.Kuaishou?.MentionUsers ?? [])
        {
            if (stored.UserId <= 0 || string.IsNullOrWhiteSpace(stored.UserName)) continue;
            KsMentions.Add(new KsMentionPickViewModel
            {
                User = new KuaishouMentionUser(stored.UserId, stored.UserName, 0, null),
                IsPicked = true,
            });
        }
        SyncKsMentionPickState();
        OnPropertyChanged(nameof(HasKsMentions));
        OnPropertyChanged(nameof(KsMentionDisplay));
    }

    /// <summary>替换候选列表（搜索是替换式；空结果同样清空旧候选）。</summary>
    public void SetKsMentionCandidates(IReadOnlyList<KuaishouMentionUser> users)
    {
        KsMentionCandidates.Clear();
        foreach (var user in users)
            KsMentionCandidates.Add(new KsMentionPickViewModel { User = user });
        SyncKsMentionPickState();
        OnPropertyChanged(nameof(HasKsMentionCandidates));
    }

    /// <summary>加入一个 @好友；同一 userId 已选则忽略。返回是否新增。</summary>
    public bool AddKsMention(KuaishouMentionUser user)
    {
        if (KsMentions.Any(m => m.UserId == user.UserId)) return false;
        KsMentions.Add(new KsMentionPickViewModel { User = user, IsPicked = true });
        SyncKsMentionPickState();
        RebuildKsMentions();
        OnPropertyChanged(nameof(HasKsMentions));
        OnPropertyChanged(nameof(KsMentionDisplay));
        return true;
    }

    /// <summary>移除一个已选 @好友。</summary>
    public void RemoveKsMention(KsMentionPickViewModel pick)
    {
        if (!KsMentions.Remove(pick)) return;
        SyncKsMentionPickState();
        RebuildKsMentions();
        OnPropertyChanged(nameof(HasKsMentions));
        OnPropertyChanged(nameof(KsMentionDisplay));
    }

    /// <summary>清空已选 @好友（caption 不再带 at-user-item HTML）。</summary>
    public void ClearKsMentions()
    {
        if (KsMentions.Count == 0) return;
        KsMentions.Clear();
        SyncKsMentionPickState();
        RebuildKsMentions();
        OnPropertyChanged(nameof(HasKsMentions));
        OnPropertyChanged(nameof(KsMentionDisplay));
    }

    /// <summary>候选行的「选 / 已选」态同步（平台按 userId 认人）。</summary>
    private void SyncKsMentionPickState()
    {
        foreach (var c in KsMentionCandidates)
            c.IsPicked = KsMentions.Any(m => m.UserId == c.UserId);
    }

    /// <summary>已选列表 → <see cref="KuaishouExtras.MentionUsers"/>（空 = 空表，发布体不下发 @）。</summary>
    private void RebuildKsMentions()
    {
        KuaishouGroup.MentionUsers = [.. KsMentions.Select(m => new KuaishouMentionRef(
            m.UserId, m.UserName))];
        RefreshSummary();
    }

    /// <summary>关联短剧 ID</summary>
    public string KuaishouMovieIdText
    {
        get => Params.Kuaishou?.MovieId ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Kuaishou?.MovieId ?? "") == v) return;
            KuaishouGroup.MovieId = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>项目 ID</summary>
    public string KuaishouProjectIdText
    {
        get => Params.Kuaishou?.ProjectId ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Kuaishou?.ProjectId ?? "") == v) return;
            KuaishouGroup.ProjectId = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>共创用户（原始 JSON 数组串 —— 元素结构未抓到，先透传）</summary>
    public string CoPublishUsersJson
    {
        get => Params.Kuaishou?.CoPublishUsersJson ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Kuaishou?.CoPublishUsersJson ?? "") == v) return;
            KuaishouGroup.CoPublishUsersJson = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>KuaishouExtras 是懒建的：老预设没有整组时，用户一动字段就补一组</summary>
    private KuaishouExtras KuaishouGroup => Params.Kuaishou ??= new KuaishouExtras();

    // ═══ 小红书长尾（阶段 2.3）—— 评论 / 摘要 / 章节同步 / 速览合集 / 订单 / 带货 / @ / 业务关系 ═══

    /// <summary>允许评论（<c>interactionPermissionBind.commentPermission</c>：0 = 允许 / 1 = 不允许）</summary>
    public bool AllowComment
    {
        get => Params.AllowComment ?? true;
        set
        {
            if ((Params.AllowComment ?? true) == value) return;
            Params.AllowComment = value;
            OnPropertyChanged();
        }
    }

    /// <summary>视频摘要 —— <c>video_info.summary</c></summary>
    public string XhsSummaryText
    {
        get => Params.Summary ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Summary ?? "") == v) return;
            Params.Summary = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>章节文字同步 —— <c>video_info.chapter_sync_text</c></summary>
    public bool ChapterSyncText
    {
        get => Params.Xhs?.ChapterSyncText ?? false;
        set
        {
            if ((Params.Xhs?.ChapterSyncText ?? false) == value) return;
            XhsGroup.ChapterSyncText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>速览合集 ID —— <c>noteSketchCollectionBind.id</c>（图文速览合集，与普通合集分开）</summary>
    public string XhsSketchCollectionIdText
    {
        get => Params.Xhs?.SketchCollectionId ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Xhs?.SketchCollectionId ?? "") == v) return;
            XhsGroup.SketchCollectionId = v.Length == 0 ? null : v;
            // ID 被外部改写（预设 / params.txt / 手改存档）→ 旧合集名标签已经对不上了
            if (_pickedSketchId != v)
            {
                _pickedSketchId = null;
                _pickedSketchLabel = null;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSketch));
            OnPropertyChanged(nameof(SketchDisplay));
        }
    }

    // ═══ 小红书「速览合集」选择器（2026-11-10：平台 collection/pc/list?type=1）═══
    // 速览合集 ID 是 24 位十六进制，手填既不友好也验证不了 —— 从账号的速览合集列表点选。
    // 选中结果仍然只写 XhsExtras.SketchCollectionId（wire 不变）。

    [ObservableProperty] private bool _isSketchPanelOpen;
    [ObservableProperty] private bool _isSketchBusy;
    [ObservableProperty] private string _sketchStatusText = "";
    [ObservableProperty] private bool _sketchStatusIsError;

    /// <summary>还有下一页（控制「加载更多」；游标来自上一页最后一条的 cursor）</summary>
    [ObservableProperty] private bool _sketchHasMore;

    public ObservableCollection<SketchPickViewModel> SketchCandidates { get; } = [];
    public bool HasSketchCandidates => SketchCandidates.Count > 0;
    public bool HasSketchStatus => SketchStatusText.Length > 0;
    public bool HasSketch => !string.IsNullOrWhiteSpace(Params.Xhs?.SketchCollectionId);

    /// <summary>
    /// 已选速览合集的展示文本。选过（有合集名）时显示名字；
    /// 只有 ID（预设 / params.txt 带进来）时显示 ID，绝不假装知道是哪个合集。
    /// </summary>
    public string SketchDisplay => !HasSketch
        ? "未选择"
        : (_pickedSketchId == Params.Xhs?.SketchCollectionId ? _pickedSketchLabel : null)
          ?? $"ID {Params.Xhs?.SketchCollectionId}";

    private string? _pickedSketchId;
    private string? _pickedSketchLabel;

    /// <summary>下一页游标（空 = 没有下一页）。翻页用；不落盘、不进 wire。</summary>
    public string SketchNextCursor { get; private set; } = "";

    partial void OnSketchStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasSketchStatus));

    /// <summary>
    /// 替换候选列表（空结果同样清空 —— 不要让上一次的旧列表留在面板里）。
    /// <paramref name="nextCursor"/> 是平台给的下一页游标（空 = 没有下一页）。
    /// </summary>
    public void SetSketchCandidates(
        IReadOnlyList<XhsSketchCollection> items, string? nextCursor = null)
    {
        SketchCandidates.Clear();
        foreach (var item in items)
            SketchCandidates.Add(new SketchPickViewModel { Item = item });
        SketchNextCursor = nextCursor ?? "";
        SketchHasMore = SketchNextCursor.Length > 0;
        OnPropertyChanged(nameof(HasSketchCandidates));
    }

    /// <summary>
    /// 追加下一页候选，返回**新增**条数（重复 ID 跳过 —— 平台游标回指同一页时不会叠出重复行）。
    /// 游标回到当前这份（或平台没给新游标）一律判「到底了」，防「加载更多」原地打转。
    /// </summary>
    public int AppendSketchCandidates(
        IReadOnlyList<XhsSketchCollection> items, string? nextCursor)
    {
        var added = 0;
        foreach (var item in items)
        {
            if (SketchCandidates.Any(c => c.Item.Id == item.Id)) continue;
            SketchCandidates.Add(new SketchPickViewModel { Item = item });
            added++;
        }

        var cursor = nextCursor ?? "";
        SketchHasMore = cursor.Length > 0 && cursor != SketchNextCursor;
        SketchNextCursor = cursor;
        OnPropertyChanged(nameof(HasSketchCandidates));
        return added;
    }

    /// <summary>选中一个速览合集：写 ID + 记住展示用的合集名。</summary>
    public void ApplyPickedSketch(XhsSketchCollection item)
    {
        _pickedSketchId = item.Id;
        _pickedSketchLabel = string.IsNullOrWhiteSpace(item.Name) ? item.Id : item.Name;
        XhsSketchCollectionIdText = item.Id;   // setter 里 v == _pickedSketchId，不会把标签清掉
        OnPropertyChanged(nameof(SketchDisplay));
    }

    /// <summary>清除已选速览合集（回到不绑定）。</summary>
    public void ClearSketch()
    {
        _pickedSketchId = null;
        _pickedSketchLabel = null;
        XhsSketchCollectionIdText = "";
        OnPropertyChanged(nameof(SketchDisplay));
    }

    // ═══ 小红书「选择群聊」选择器（2026-11-15：平台 group_list）═══
    // 发布体 business_binds.groupBind = {groupId,groupName,desc,avatar}（bundle 逐字）。
    // 面板打开才拉列表；账号没有群时显示空态，不拦发布。

    [ObservableProperty] private bool _isXhsGroupPanelOpen;
    [ObservableProperty] private bool _isXhsGroupBusy;
    [ObservableProperty] private string _xhsGroupStatusText = "";
    [ObservableProperty] private bool _xhsGroupStatusIsError;

    public ObservableCollection<XhsGroupPickViewModel> XhsGroupCandidates { get; } = [];
    public bool HasXhsGroupCandidates => XhsGroupCandidates.Count > 0;
    public bool HasXhsGroupStatus => XhsGroupStatusText.Length > 0;
    public bool HasXhsGroup => Params.Xhs?.GroupBind is { GroupId.Length: > 0 };

    partial void OnXhsGroupStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasXhsGroupStatus));

    /// <summary>已选群聊展示：未选择 / 「群名」（ID 只作兜底）。</summary>
    public string XhsGroupDisplay => Params.Xhs?.GroupBind is { GroupId.Length: > 0 } group
        ? (string.IsNullOrWhiteSpace(group.GroupName) ? $"群 {group.GroupId}" : group.GroupName)
        : "未选择";

    /// <summary>替换候选列表（空结果同样清空 —— 不让上次的旧列表留在面板里）。</summary>
    public void SetXhsGroupCandidates(IReadOnlyList<XhsGroupItem> items)
    {
        XhsGroupCandidates.Clear();
        foreach (var item in items)
            XhsGroupCandidates.Add(new XhsGroupPickViewModel { Item = item });
        OnPropertyChanged(nameof(HasXhsGroupCandidates));
    }

    /// <summary>选中一个群聊：写快照（四字段原样落存档 / 发布体）。</summary>
    public void ApplyXhsGroup(XhsGroupItem item)
    {
        XhsGroup.GroupBind = new XhsGroupRef(
            item.GroupId, item.GroupName, item.Desc, item.Avatar);
        OnPropertyChanged(nameof(HasXhsGroup));
        OnPropertyChanged(nameof(XhsGroupDisplay));
    }

    /// <summary>清除已选群聊（回到不关联）。</summary>
    public void ClearXhsGroup()
    {
        if (Params.Xhs?.GroupBind is null) return;
        XhsGroup.GroupBind = null;
        OnPropertyChanged(nameof(HasXhsGroup));
        OnPropertyChanged(nameof(XhsGroupDisplay));
    }

    // ═══ 小红书 PK 封面（2026-11-15）═══
    // 页面「设置封面 → PK 封面」：主封面 + 若干附加图，发布体
    // video_info.pk_cover_biz_relations（bundle 逐字）。附加图逐张上传，
    // 主封面自动用发布封面/首帧；开关开了但没图 = 平台空列表（不生效）。

    public bool XhsPkCoverEnabled
    {
        get => Params.Xhs?.PkCoverEnabled ?? false;
        set
        {
            if ((Params.Xhs?.PkCoverEnabled ?? false) == value) return;
            XhsGroup.PkCoverEnabled = value ? true : null;
            if (value) XhsGroup.PkCoverImages ??= [];
            OnPropertyChanged();
            OnPropertyChanged(nameof(XhsPkCoverDisplay));
        }
    }

    public ObservableCollection<XhsPkCoverItemViewModel> XhsPkCovers { get; } = [];

    public bool HasXhsPkCovers => XhsPkCovers.Count > 0;

    public string XhsPkCoverDisplay => !XhsPkCoverEnabled
        ? "未启用"
        : XhsPkCovers.Count == 0
            ? "已启用 · 未选附加图（不会生效）"
            : $"已启用 · {XhsPkCovers.Count} 张附加图";

    /// <summary>从存档重挂已选图片（预设 / 草稿 / 换条目时调用）。</summary>
    public void RefreshXhsPkCovers()
    {
        XhsPkCovers.Clear();
        foreach (var path in Params.Xhs?.PkCoverImages ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path))
                XhsPkCovers.Add(new XhsPkCoverItemViewModel { Path = path });
        }
        OnPropertyChanged(nameof(HasXhsPkCovers));
        OnPropertyChanged(nameof(XhsPkCoverDisplay));
    }

    /// <summary>追加一张附加图（重复路径不重复加）。</summary>
    public void AddXhsPkCover(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var list = XhsGroup.PkCoverImages ??= [];
        var trimmed = path.Trim();
        if (list.Any(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase)))
            return;

        list.Add(trimmed);
        XhsGroup.PkCoverEnabled = true;
        RefreshXhsPkCovers();
        OnPropertyChanged(nameof(XhsPkCoverEnabled));
    }

    /// <summary>移除一张附加图。</summary>
    public void RemoveXhsPkCover(XhsPkCoverItemViewModel item)
    {
        if (item is null || Params.Xhs?.PkCoverImages is null) return;
        Params.Xhs.PkCoverImages.RemoveAll(p =>
            string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase));
        RefreshXhsPkCovers();
    }

    /// <summary>清空全部附加图（开关保留，显示「已启用 · 未选附加图」）。</summary>
    public void ClearXhsPkCovers()
    {
        if (Params.Xhs?.PkCoverImages is null) return;
        XhsGroup.PkCoverImages = [];
        RefreshXhsPkCovers();
    }

    // ═══ 小红书视频标记（2026-11-15：标记地点或标记朋友）═══
    // 搜索：朋友走 search/label?type=user；地点复用 POI 搜索（post_loc 同一数据源）。
    // 发布体 video_info.markers（bundle 逐字）；起止秒由用户填，wire 换毫秒。

    [ObservableProperty] private bool _isXhsMarkerPanelOpen;
    [ObservableProperty] private bool _isXhsMarkerBusy;
    [ObservableProperty] private string _xhsMarkerQuery = "";
    /// <summary>0 = 标记朋友（用户）/ 1 = 标记地点</summary>
    [ObservableProperty] private int _xhsMarkerTypeIndex;
    [ObservableProperty] private string _xhsMarkerStatusText = "";
    [ObservableProperty] private bool _xhsMarkerStatusIsError;

    public ObservableCollection<XhsMarkerPickViewModel> XhsMarkerCandidates { get; } = [];
    public ObservableCollection<XhsMarkerRowViewModel> XhsMarkers { get; } = [];

    public bool HasXhsMarkerCandidates => XhsMarkerCandidates.Count > 0;
    public bool HasXhsMarkerStatus => XhsMarkerStatusText.Length > 0;
    public bool HasXhsMarkers => XhsMarkers.Count > 0;

    public string XhsMarkerDisplay => XhsMarkers.Count == 0
        ? "未添加"
        : $"已标记 {XhsMarkers.Count} 个（{XhsMarkers.Count(m => m.Marker.Type == "location")} 个地点 / "
          + $"{XhsMarkers.Count(m => m.Marker.Type != "location")} 个朋友）";

    partial void OnXhsMarkerStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasXhsMarkerStatus));

    /// <summary>从存档重挂已选标记（预设 / 草稿 / 换条目时调用）。</summary>
    public void RefreshXhsMarkers()
    {
        XhsMarkers.Clear();
        foreach (var marker in Params.Xhs?.Markers ?? [])
        {
            if (string.IsNullOrWhiteSpace(marker.Id) || string.IsNullOrWhiteSpace(marker.Name))
                continue;
            XhsMarkers.Add(CreateMarkerRow(marker));
        }
        OnPropertyChanged(nameof(HasXhsMarkers));
        OnPropertyChanged(nameof(XhsMarkerDisplay));
    }

    private XhsMarkerRowViewModel CreateMarkerRow(XhsVideoMarker marker) =>
        new(marker, row =>
        {
            marker.StartSeconds = row.StartSeconds;
            marker.EndSeconds = row.EndSeconds;
        })
        {
            StartText = marker.StartSeconds.ToString(),
            EndText = marker.EndSeconds.ToString(),
        };

    /// <summary>替换候选列表（空结果同样清空）。</summary>
    public void SetXhsMarkerCandidates(IReadOnlyList<XhsMarkerPickViewModel> items)
    {
        XhsMarkerCandidates.Clear();
        foreach (var item in items) XhsMarkerCandidates.Add(item);
        OnPropertyChanged(nameof(HasXhsMarkerCandidates));
    }

    /// <summary>加入一个标记（同类型 + 同 ID 不重复加）。默认 0–5 秒。</summary>
    public void AddXhsMarker(XhsVideoMarker marker)
    {
        var list = XhsGroup.Markers;
        if (list.Any(m => m.Type == marker.Type && m.Id == marker.Id)) return;

        if (marker.EndSeconds <= marker.StartSeconds)
            marker.EndSeconds = Math.Min(marker.StartSeconds + 5, 86_400);
        list.Add(marker);
        XhsMarkers.Add(CreateMarkerRow(marker));
        OnPropertyChanged(nameof(HasXhsMarkers));
        OnPropertyChanged(nameof(XhsMarkerDisplay));
    }

    /// <summary>移除一个标记。</summary>
    public void RemoveXhsMarker(XhsMarkerRowViewModel row)
    {
        if (row is null) return;
        Params.Xhs?.Markers.RemoveAll(m =>
            m.Type == row.Marker.Type && m.Id == row.Marker.Id);
        RefreshXhsMarkers();
    }

    /// <summary>清空全部标记。</summary>
    public void ClearXhsMarkers()
    {
        if (Params.Xhs?.Markers is not { Count: > 0 }) return;
        XhsGroup.Markers = [];
        RefreshXhsMarkers();
    }

    // ═══ 小红书「添加路线」（2026-11-15：建路线 + 发布体两处）═══
    // 流程：搜 POI（query/poi/by/keyword）→ 点选换坐标（simple/markers）→
    // 「保存路线」（save/travel，空 polygon）→ 存 travelId 快照。
    // 发布时只带快照：hash_tag 的 map_traver + biz_relations 的 POI_MAP_TRAVEL。
    // 未保存的候选 / 草稿只在本行内存里，不进存档（与群聊「选完即快照」一致）。

    [ObservableProperty] private bool _isXhsRoutePanelOpen;
    [ObservableProperty] private bool _isXhsRouteBusy;
    [ObservableProperty] private string _xhsRouteQuery = "";
    [ObservableProperty] private string _xhsRouteName = "";
    [ObservableProperty] private string _xhsRouteStatusText = "";
    [ObservableProperty] private bool _xhsRouteStatusIsError;

    public ObservableCollection<XhsRoutePickViewModel> XhsRouteCandidates { get; } = [];
    public ObservableCollection<XhsRoutePointViewModel> XhsRoutePoints { get; } = [];

    public bool HasXhsRouteCandidates => XhsRouteCandidates.Count > 0;
    public bool HasXhsRoutePoints => XhsRoutePoints.Count > 0;
    public bool HasXhsRouteStatus => XhsRouteStatusText.Length > 0;
    public bool HasXhsRoute => Params.Xhs?.Route is { TravelId.Length: > 0 };

    /// <summary>已建路线后收起编辑器（要重做先「清除路线」）。</summary>
    public bool IsXhsRouteBuilderVisible => !HasXhsRoute;

    public string XhsRouteDisplay => Params.Xhs?.Route is { TravelId.Length: > 0 } route
        ? $"已添加「{route.RouteName}」· {route.PlanAmount} 段"
        : "未添加";

    public string XhsRouteSaveLabel => $"保存路线（{XhsRoutePoints.Count} 个地点）";

    partial void OnXhsRouteStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasXhsRouteStatus));

    /// <summary>替换 POI 候选（空结果同样清空）。</summary>
    public void SetXhsRouteCandidates(IReadOnlyList<XhsRoutePoi> items)
    {
        XhsRouteCandidates.Clear();
        foreach (var item in items)
            XhsRouteCandidates.Add(new XhsRoutePickViewModel { Item = item });
        OnPropertyChanged(nameof(HasXhsRouteCandidates));
    }

    /// <summary>加入一个地点（同 poiId 不重复加）。</summary>
    public void AddXhsRoutePoint(XhsRoutePoint point)
    {
        if (XhsRoutePoints.Any(p => p.Point.PoiId == point.PoiId)) return;
        XhsRoutePoints.Add(new XhsRoutePointViewModel { Point = point });
        OnPropertyChanged(nameof(HasXhsRoutePoints));
        OnPropertyChanged(nameof(XhsRouteSaveLabel));
    }

    /// <summary>从草稿里移除一个地点。</summary>
    public void RemoveXhsRoutePoint(XhsRoutePointViewModel row)
    {
        if (!XhsRoutePoints.Remove(row)) return;
        OnPropertyChanged(nameof(HasXhsRoutePoints));
        OnPropertyChanged(nameof(XhsRouteSaveLabel));
    }

    /// <summary>清空候选与草稿（不动已建路线）。</summary>
    public void ClearXhsRouteDraft()
    {
        XhsRouteCandidates.Clear();
        XhsRoutePoints.Clear();
        OnPropertyChanged(nameof(HasXhsRouteCandidates));
        OnPropertyChanged(nameof(HasXhsRoutePoints));
        OnPropertyChanged(nameof(XhsRouteSaveLabel));
    }

    /// <summary>写已建路线快照（发布体来源）。</summary>
    public void ApplyXhsRoute(XhsRouteRef route)
    {
        XhsGroup.Route = route;
        ClearXhsRouteDraft();
        OnPropertyChanged(nameof(HasXhsRoute));
        OnPropertyChanged(nameof(IsXhsRouteBuilderVisible));
        OnPropertyChanged(nameof(XhsRouteDisplay));
    }

    /// <summary>清除已建路线（回到不关联；草稿一并清）。</summary>
    public void ClearXhsRoute()
    {
        if (Params.Xhs?.Route is null && XhsRoutePoints.Count == 0) return;
        XhsGroup.Route = null;
        ClearXhsRouteDraft();
        OnPropertyChanged(nameof(HasXhsRoute));
        OnPropertyChanged(nameof(IsXhsRouteBuilderVisible));
        OnPropertyChanged(nameof(XhsRouteDisplay));
    }

    // ═══ 小红书「@用户」选择器（2026-11-10：平台 search/user_info）═══
    // 正文里的 @ 是平台结构化字段（common.ats）：页面在正文里插一个内联节点，
    // 发布体带 {nickname, userId, userid} 数组。手填 JSON 既难写也验证不了 ——
    // 按昵称搜平台用户后点选。选中仍只写 XhsExtras.Ats（wire 形状不变）；
    // 正文缺「@昵称」时由 PublishPayloadBuilder.XhsCaption 统一补（预检与组装同一份）。

    [ObservableProperty] private bool _isXhsMentionPanelOpen;
    [ObservableProperty] private bool _isXhsMentionBusy;
    [ObservableProperty] private string _xhsMentionQuery = "";
    [ObservableProperty] private string _xhsMentionStatusText = "";
    [ObservableProperty] private bool _xhsMentionStatusIsError;

    public ObservableCollection<XhsMentionPickViewModel> XhsMentionCandidates { get; } = [];

    /// <summary>已选 @用户（顺序 = 追加顺序；写回时去重）</summary>
    public ObservableCollection<XhsMentionPickViewModel> XhsMentions { get; } = [];

    public bool HasXhsMentionCandidates => XhsMentionCandidates.Count > 0;
    public bool HasXhsMentionStatus => XhsMentionStatusText.Length > 0;
    public bool HasXhsMentions => XhsMentions.Count > 0;

    partial void OnXhsMentionStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasXhsMentionStatus));

    /// <summary>@用户行展示：未选择 / 「已选 @A、@B」（太长由 TextTrimming 收尾）。</summary>
    public string XhsMentionDisplay => XhsMentions.Count == 0
        ? "未选择"
        : "已选 " + string.Join("、", XhsMentions.Select(m => "@" + m.Nickname));

    /// <summary>
    /// 从存档的 <c>common.ats</c> 重新种子化已选列表（草稿重建 / 套用预设 / 换账号时调用）。
    /// 旧档、params.txt、预设里可能已经有 ats JSON —— 面板打开时不能显示成「未选择」。
    /// </summary>
    public void RefreshXhsMentions()
    {
        XhsMentions.Clear();
        foreach (var user in XhsMentionService.ParseAts(Params.Xhs?.Ats))
            XhsMentions.Add(new XhsMentionPickViewModel { User = user, IsPicked = true });
        SyncXhsMentionPickState();
        OnPropertyChanged(nameof(HasXhsMentions));
        OnPropertyChanged(nameof(XhsMentionDisplay));
    }

    /// <summary>替换候选列表（搜索是替换式；空结果同样清空旧候选）。</summary>
    public void SetXhsMentionCandidates(IReadOnlyList<XhsMentionUser> users)
    {
        XhsMentionCandidates.Clear();
        foreach (var user in users)
            XhsMentionCandidates.Add(new XhsMentionPickViewModel { User = user });
        SyncXhsMentionPickState();
        OnPropertyChanged(nameof(HasXhsMentionCandidates));
    }

    /// <summary>加入一个 @用户；已在列表里则忽略。返回是否新增。</summary>
    public bool AddXhsMention(XhsMentionUser user)
    {
        if (XhsMentions.Any(m => m.User.UserId == user.UserId)) return false;
        XhsMentions.Add(new XhsMentionPickViewModel { User = user, IsPicked = true });
        SyncXhsMentionPickState();
        RebuildXhsAts();
        OnPropertyChanged(nameof(HasXhsMentions));
        OnPropertyChanged(nameof(XhsMentionDisplay));
        return true;
    }

    /// <summary>移除一个已选 @用户。</summary>
    public void RemoveXhsMention(XhsMentionPickViewModel pick)
    {
        if (!XhsMentions.Remove(pick)) return;
        SyncXhsMentionPickState();
        RebuildXhsAts();
        OnPropertyChanged(nameof(HasXhsMentions));
        OnPropertyChanged(nameof(XhsMentionDisplay));
    }

    /// <summary>清空已选 @用户（= 发布体不下发 common.ats）。</summary>
    public void ClearXhsMentions()
    {
        if (XhsMentions.Count == 0) return;
        XhsMentions.Clear();
        SyncXhsMentionPickState();
        RebuildXhsAts();
        OnPropertyChanged(nameof(HasXhsMentions));
        OnPropertyChanged(nameof(XhsMentionDisplay));
    }

    /// <summary>候选行的「选 / 已选」态按已选列表（用户 ID）同步。</summary>
    private void SyncXhsMentionPickState()
    {
        foreach (var c in XhsMentionCandidates)
            c.IsPicked = XhsMentions.Any(m => m.User.UserId == c.User.UserId);
    }

    /// <summary>已选列表 → <c>common.ats</c> JSON（空 = 不下发）。</summary>
    private void RebuildXhsAts()
    {
        XhsGroup.Ats = XhsMentionService.BuildAtsJson(XhsMentions.Select(m => m.User));
        OnPropertyChanged(nameof(XhsAtsJson));
    }

    /// <summary>@ 用户（原始 JSON 数组串）—— <c>common.ats</c>。
    /// 主界面入口已改为「@用户」选择器（2026-11-10：平台 search/user_info），
    /// 这个属性保留给存档往返与自检。</summary>
    public string XhsAtsJson
    {
        get => Params.Xhs?.Ats ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Xhs?.Ats ?? "") == v) return;
            XhsGroup.Ats = v.Length == 0 ? null : v;
            OnPropertyChanged();
            // 原始 JSON 被外部改写 → 已选列表按新存档重建（选择器与存档保持一致）
            RefreshXhsMentions();
        }
    }

    // ═══ 小红书「互动组件 · 投票」（A4，2026-11-11）═══
    // 只存用户输入（问题 + 选项文字）；widget 是发布时由 XhsInteractionService 现建，
    // 所以这里没有创建按钮、没有任何网络调用。
    // 规则（2~5 个选项 / 问题非空）收在 XhsPollRules.Analyze —— 预检与发布端共用同一份。

    /// <summary>投票问题 —— create_interact 的 <c>widget_name</c>，也是 hash_tag 条目的 <c>name</c></summary>
    public string XhsPollQuestionText
    {
        get => Params.Xhs?.PollQuestion ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Xhs?.PollQuestion ?? "") == v) return;
            XhsGroup.PollQuestion = v.Length == 0 ? null : v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasXhsPoll));
        }
    }

    /// <summary>投票选项行（UI 编辑用；写进 Params 的是去空白后的非空项）</summary>
    public ObservableCollection<XhsPollOptionViewModel> XhsPollOptions { get; } = [];

    /// <summary>还能加选项吗（上限 5；「添加选项」按钮的禁用态）</summary>
    public bool CanAddXhsPollOption => XhsPollOptions.Count < XhsPollRules.MaxOptions;

    /// <summary>有投票输入（「清除投票」按钮的可见性）</summary>
    public bool HasXhsPoll =>
        !string.IsNullOrWhiteSpace(Params.Xhs?.PollQuestion)
        || Params.Xhs?.PollOptions is { Count: > 0 };

    /// <summary>
    /// 从存档重建选项行（预设切换 / params / 旧档）。没有存档时铺两行空行 ——
    /// 2 是下限，省用户两次点「添加选项」。
    /// </summary>
    public void RefreshXhsPoll()
    {
        XhsPollOptions.Clear();
        var stored = Params.Xhs?.PollOptions;
        if (stored is { Count: > 0 })
        {
            foreach (var option in stored)
                XhsPollOptions.Add(new XhsPollOptionViewModel(OnXhsPollOptionEdited)
                {
                    Text = option,
                });
        }
        else
        {
            XhsPollOptions.Add(new XhsPollOptionViewModel(OnXhsPollOptionEdited));
            XhsPollOptions.Add(new XhsPollOptionViewModel(OnXhsPollOptionEdited));
        }

        OnPropertyChanged(nameof(XhsPollQuestionText));
        OnPropertyChanged(nameof(CanAddXhsPollOption));
        OnPropertyChanged(nameof(HasXhsPoll));
    }

    [RelayCommand]
    private void AddXhsPollOption()
    {
        if (!CanAddXhsPollOption) return;
        XhsPollOptions.Add(new XhsPollOptionViewModel(OnXhsPollOptionEdited));
        OnPropertyChanged(nameof(CanAddXhsPollOption));
    }

    [RelayCommand]
    private void RemoveXhsPollOption(XhsPollOptionViewModel? option)
    {
        if (option is null || !XhsPollOptions.Remove(option)) return;
        OnXhsPollOptionEdited();
        OnPropertyChanged(nameof(CanAddXhsPollOption));
    }

    [RelayCommand]
    private void ClearXhsPoll()
    {
        XhsPollOptions.Clear();
        // 直接清 Params（不走 question setter —— 空串与「已空」相等会提前 return）
        if (Params.Xhs is not null)
        {
            Params.Xhs.PollQuestion = null;
            Params.Xhs.PollOptions = null;
        }
        OnPropertyChanged(nameof(XhsPollQuestionText));
        OnPropertyChanged(nameof(CanAddXhsPollOption));
        OnPropertyChanged(nameof(HasXhsPoll));
    }

    /// <summary>选项行文字变化 → 回写 Params</summary>
    private void OnXhsPollOptionEdited()
    {
        RebuildXhsPoll();
        OnPropertyChanged(nameof(HasXhsPoll));
    }

    /// <summary>
    /// 把选项行（可含空行）写成干净的存档：空行丢弃、问题 trim。
    /// 问题/选项都空 = 关掉投票（两个字段都 null）。
    /// </summary>
    private void RebuildXhsPoll()
    {
        var question = XhsPollQuestionText.Trim();
        var options = XhsPollOptions
            .Select(o => o.Text.Trim())
            .Where(t => t.Length > 0)
            .ToList();
        XhsGroup.PollQuestion = question.Length == 0 ? null : question;
        XhsGroup.PollOptions = options.Count == 0 ? null : options;
    }

    // ═══ 小红书「引用笔记」选择器（A5，2026-11-11：平台 note/user/posted）═══
    // 平台页面同一时间只允许引用一条笔记；列表来自账号已发布笔记（tab=1），
    // 打开面板才拉（签名宿主冷启动可达 60s+，不拖慢进页）。
    // 选中只写 XhsExtras.ReferencedNote（快照：ID + 标题 + 封面），wire 由发布端拼 REF_POST。

    [ObservableProperty] private bool _isXhsNotePanelOpen;
    [ObservableProperty] private bool _isXhsNoteBusy;
    [ObservableProperty] private string _xhsNoteStatusText = "";
    [ObservableProperty] private bool _xhsNoteStatusIsError;

    public ObservableCollection<XhsNotePickViewModel> XhsNoteCandidates { get; } = [];

    public bool HasXhsNoteCandidates => XhsNoteCandidates.Count > 0;
    public bool HasXhsNoteStatus => XhsNoteStatusText.Length > 0;
    public bool HasXhsNote => !string.IsNullOrWhiteSpace(Params.Xhs?.ReferencedNote?.Id);

    /// <summary>引用笔记行展示：标题（平台没给标题就显示 ID）；未选「未选择」</summary>
    public string XhsNoteDisplay
    {
        get
        {
            if (Params.Xhs?.ReferencedNote is not { } note
                || string.IsNullOrWhiteSpace(note.Id)) return "未选择";
            return string.IsNullOrWhiteSpace(note.Title) ? $"ID {note.Id}" : note.Title;
        }
    }

    partial void OnXhsNoteStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasXhsNoteStatus));

    /// <summary>从存档重建已选状态（草稿重建 / 套用预设 / 换账号）—— 候选行的「已选」跟着走</summary>
    public void RefreshXhsReferencedNote()
    {
        SyncXhsNotePickState();
        OnPropertyChanged(nameof(HasXhsNote));
        OnPropertyChanged(nameof(XhsNoteDisplay));
    }

    /// <summary>替换候选列表（空结果同样清空旧候选 —— 别把上一次的旧列表留在面板里）</summary>
    public void SetXhsNoteCandidates(IReadOnlyList<XhsNoteItem> notes)
    {
        XhsNoteCandidates.Clear();
        foreach (var note in notes)
            XhsNoteCandidates.Add(new XhsNotePickViewModel { Item = note });
        SyncXhsNotePickState();
        OnPropertyChanged(nameof(HasXhsNoteCandidates));
    }

    /// <summary>选中一条笔记 → 写快照（平台同一时间只允许引用一条，替换式）。</summary>
    public void ApplyPickedNote(XhsNoteItem note)
    {
        XhsGroup.ReferencedNote = new XhsNoteRef
        {
            Id = note.Id,
            Title = note.Title,
            Cover = note.Cover,
        };
        SyncXhsNotePickState();
        OnPropertyChanged(nameof(HasXhsNote));
        OnPropertyChanged(nameof(XhsNoteDisplay));
    }

    /// <summary>清除引用笔记（发布体不再带 REF_POST）。</summary>
    public void ClearXhsNote()
    {
        if (Params.Xhs is not null) Params.Xhs.ReferencedNote = null;
        SyncXhsNotePickState();
        OnPropertyChanged(nameof(HasXhsNote));
        OnPropertyChanged(nameof(XhsNoteDisplay));
    }

    private void SyncXhsNotePickState()
    {
        var id = Params.Xhs?.ReferencedNote?.Id;
        foreach (var candidate in XhsNoteCandidates)
            candidate.IsPicked = candidate.Item.Id == id;
    }

    /// <summary>XhsExtras 是懒建的：老预设没有整组时，用户一动字段就补一组</summary>
    private XhsExtras XhsGroup => Params.Xhs ??= new XhsExtras();

    // ═══ 视频号长尾（阶段 2.4）—— 抓包 docs/wechatvideo/20261105 ═══

    /// <summary>短标题 —— <c>objectDesc.shortTitle</c>；留空 = 用标题（≥6 字才下发）</summary>
    public string WechatShortTitleText
    {
        get => Params.ShortTitle ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.ShortTitle ?? "") == v) return;
            Params.ShortTitle = v.Length == 0 ? null : v;
            OnPropertyChanged();
            RefreshSummary();
        }
    }

    /// <summary>扩展链接 —— <c>objectDesc.extReading.link</c>（留空 = 不挂链接卡片）</summary>
    public string WechatExternalLinkText
    {
        get => Params.ExternalLink ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.ExternalLink ?? "") == v) return;
            Params.ExternalLink = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>链接卡片标题 —— <c>objectDesc.extReading.title</c>（选填）</summary>
    public string WechatExtReadingTitleText
    {
        get => Params.WechatVideo?.ExtReadingTitle ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.WechatVideo?.ExtReadingTitle ?? "") == v) return;
            WechatGroup.ExtReadingTitle = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>链接类型下拉序号 —— 0 = 公众号文章（wire 1）/ 1 = 红包封面（wire 2）。
    /// 2026-11-11 B2 抓包：页面只有这两项，落点 <c>extReading.urlType</c>。</summary>
    public int WechatExtReadingTypeIndex
    {
        get => Params.WechatVideo?.ExtReadingType == 2 ? 1 : 0;
        set
        {
            var v = value == 1 ? 2 : 1;
            if ((Params.WechatVideo?.ExtReadingType ?? 1) == v) return;
            WechatGroup.ExtReadingType = v;
            OnPropertyChanged();
        }
    }

    /// <summary>媒体卡片样式下拉（0=默认 / 1=平铺 / 2=居中；对应 <c>cardShowStyle</c>）</summary>
    public int WechatCardShowStyleIndex
    {
        get => Params.WechatVideo?.CardShowStyle switch { 1 => 1, 2 => 2, _ => 0 };
        set
        {
            int? v = value switch { 1 => 1, 2 => 2, _ => null };
            if ((Params.WechatVideo?.CardShowStyle) == v) return;
            WechatGroup.CardShowStyle = v;
            OnPropertyChanged();
        }
    }

    /// <summary>WechatVideoExtras 是懒建的：老预设没有整组时，用户一动字段就补一组</summary>
    private WechatVideoExtras WechatGroup => Params.WechatVideo ??= new WechatVideoExtras();

    // ═══ 视频号 @好友 选择器（2026-11-11 B2：helper_search_finder_account）═══
    // 提交体只认昵称：objectDesc.mentionedUser=[{nickname}] + 正文末尾补「@昵称 」。
    // 选择器只写 WechatVideoExtras.MentionUsers；正文补写与提交体由
    // PublishPayloadBuilder → WechatVideoContentService.BuildPostBody 统一做。

    [ObservableProperty] private bool _isWechatMentionPanelOpen;
    [ObservableProperty] private bool _isWechatMentionBusy;
    [ObservableProperty] private string _wechatMentionQuery = "";
    [ObservableProperty] private string _wechatMentionStatusText = "";
    [ObservableProperty] private bool _wechatMentionStatusIsError;

    public ObservableCollection<WechatMentionPickViewModel> WechatMentionCandidates { get; } = [];
    public ObservableCollection<WechatMentionPickViewModel> WechatMentions { get; } = [];

    public bool HasWechatMentionCandidates => WechatMentionCandidates.Count > 0;
    public bool HasWechatMentionStatus => WechatMentionStatusText.Length > 0;
    public bool HasWechatMentions => WechatMentions.Count > 0;

    partial void OnWechatMentionStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasWechatMentionStatus));

    /// <summary>@好友行展示：未选择 / 「已选 @A、@B」。</summary>
    public string WechatMentionDisplay => WechatMentions.Count == 0
        ? "未选择"
        : "已选 " + string.Join("、", WechatMentions.Select(m => "@" + m.Nickname));

    /// <summary>从存档重新种子化已选列表（草稿重建 / 套用预设 / 换账号时调用）。</summary>
    public void RefreshWechatMentions()
    {
        WechatMentions.Clear();
        foreach (var stored in Params.WechatVideo?.MentionUsers ?? [])
        {
            if (string.IsNullOrWhiteSpace(stored.Nickname)) continue;
            WechatMentions.Add(new WechatMentionPickViewModel
            {
                User = new WechatVideoMentionUser(
                    stored.Nickname, stored.Username, stored.AvatarUrl),
                IsPicked = true,
            });
        }
        SyncWechatMentionPickState();
        OnPropertyChanged(nameof(HasWechatMentions));
        OnPropertyChanged(nameof(WechatMentionDisplay));
    }

    /// <summary>替换候选列表（搜索是替换式；空结果同样清空旧候选）。</summary>
    public void SetWechatMentionCandidates(IReadOnlyList<WechatVideoMentionUser> users)
    {
        WechatMentionCandidates.Clear();
        foreach (var user in users)
            WechatMentionCandidates.Add(new WechatMentionPickViewModel { User = user });
        SyncWechatMentionPickState();
        OnPropertyChanged(nameof(HasWechatMentionCandidates));
    }

    /// <summary>加入一个 @好友；已选过同一昵称则忽略。返回是否新增。</summary>
    public bool AddWechatMention(WechatVideoMentionUser user)
    {
        if (WechatMentions.Any(m => m.Nickname == user.Nickname)) return false;
        WechatMentions.Add(new WechatMentionPickViewModel { User = user, IsPicked = true });
        SyncWechatMentionPickState();
        RebuildWechatMentions();
        OnPropertyChanged(nameof(HasWechatMentions));
        OnPropertyChanged(nameof(WechatMentionDisplay));
        return true;
    }

    /// <summary>移除一个已选 @好友。</summary>
    public void RemoveWechatMention(WechatMentionPickViewModel pick)
    {
        if (!WechatMentions.Remove(pick)) return;
        SyncWechatMentionPickState();
        RebuildWechatMentions();
        OnPropertyChanged(nameof(HasWechatMentions));
        OnPropertyChanged(nameof(WechatMentionDisplay));
    }

    /// <summary>清空已选 @好友（发布体不再下发 mentionedUser、正文不再补 @）。</summary>
    public void ClearWechatMentions()
    {
        if (WechatMentions.Count == 0) return;
        WechatMentions.Clear();
        SyncWechatMentionPickState();
        RebuildWechatMentions();
        OnPropertyChanged(nameof(HasWechatMentions));
        OnPropertyChanged(nameof(WechatMentionDisplay));
    }

    /// <summary>候选行的「选 / 已选」态同步（平台只认昵称，按昵称匹配）。</summary>
    private void SyncWechatMentionPickState()
    {
        foreach (var c in WechatMentionCandidates)
            c.IsPicked = WechatMentions.Any(m => m.Nickname == c.Nickname);
    }

    /// <summary>已选列表 → <see cref="WechatVideoExtras.MentionUsers"/>（空 = 空表，序列化不下发）。</summary>
    private void RebuildWechatMentions()
    {
        WechatGroup.MentionUsers = [.. WechatMentions.Select(m => new WechatVideoMentionRef(
            m.User.Nickname, m.User.Username, m.User.AvatarUrl))];
        RefreshSummary();
    }

    // ═══ B站长尾（阶段 4.6）═══
    //
    //  B站投稿的专有字段全部在这里编辑；分区是只读展示（params.txt 可覆盖）。
    //  懒建整组与其它平台同口径：老预设没有 Bilibili 组时，用户一动字段就补一组。

    private BilibiliExtras BiliGroup => Params.Bilibili ??= new BilibiliExtras();

    /// <summary>标签（<c>add/v3.tag</c>，逗号分隔，1~10 个）—— 与 B站话题是两回事</summary>
    public string BilibiliTagsText
    {
        get => string.Join(", ", Params.Tags);
        set
        {
            var tags = value
                .Split([',', '，', '、', ';', '；'], StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (Params.Tags.SequenceEqual(tags)) return;
            Params.Tags = tags;
            OnPropertyChanged();
        }
    }

    /// <summary>动态文案 —— <c>dynamic</c></summary>
    public string BilibiliDynamicText
    {
        get => BiliGroup.Dynamic ?? "";
        set
        {
            var v = value.Trim();
            if ((BiliGroup.Dynamic ?? "") == v) return;
            BiliGroup.Dynamic = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>水印 —— <c>watermark.state</c>（默认开，与页面一致）</summary>
    public bool BilibiliWatermark
    {
        get => BiliGroup.Watermark ?? true;
        set
        {
            if ((BiliGroup.Watermark ?? true) == value) return;
            BiliGroup.Watermark = value;
            OnPropertyChanged();
        }
    }

    /// <summary>开启字幕 —— <c>subtitle.open</c></summary>
    public bool BilibiliSubtitleOpen
    {
        get => BiliGroup.SubtitleOpen ?? false;
        set
        {
            if ((BiliGroup.SubtitleOpen ?? false) == value) return;
            BiliGroup.SubtitleOpen = value;
            OnPropertyChanged();
        }
    }

    /// <summary>字幕语言 —— <c>subtitle.lan</c>（页面占位；真正生效靠上传的字幕语言）</summary>
    public string BilibiliSubtitleLanText
    {
        get => BiliGroup.SubtitleLan ?? "";
        set
        {
            var v = value.Trim();
            if ((BiliGroup.SubtitleLan ?? "") == v) return;
            BiliGroup.SubtitleLan = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    // ── 字幕上传（B5，2026-11-15）──
    //  与图片封面不同：这里存的是本地 .srt 路径，发布时才上传。

    /// <summary>是否已选字幕文件</summary>
    public bool HasBilibiliSubtitleFile => !string.IsNullOrWhiteSpace(BiliGroup.SubtitleFile);

    /// <summary>字幕行展示（只显示文件名）</summary>
    public string BilibiliSubtitleFileDisplay => HasBilibiliSubtitleFile
        ? Path.GetFileName(BiliGroup.SubtitleFile)
        : "未选择（不传字幕）";

    /// <summary>选中字幕文件（文件选择由页面 VM 的 SubtitlePicker 提供）</summary>
    public void ApplyBilibiliSubtitleFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        BiliGroup.SubtitleFile = path.Trim();
        OnPropertyChanged(nameof(HasBilibiliSubtitleFile));
        OnPropertyChanged(nameof(BilibiliSubtitleFileDisplay));
    }

    public void ClearBilibiliSubtitleFile()
    {
        if (Params.Bilibili is null) return;
        Params.Bilibili.SubtitleFile = null;
        OnPropertyChanged(nameof(HasBilibiliSubtitleFile));
        OnPropertyChanged(nameof(BilibiliSubtitleFileDisplay));
    }

    /// <summary>字幕语言（preSave <c>files[].lan</c>；留空 = zh）</summary>
    public string BilibiliSubtitleFileLanText
    {
        get => BiliGroup.SubtitleFileLan ?? "";
        set
        {
            var v = value.Trim();
            if ((BiliGroup.SubtitleFileLan ?? "") == v) return;
            BiliGroup.SubtitleFileLan = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    // ── 互动引导卡片（B5，2026-11-15；v1 只做互动引导）──

    /// <summary>是否创建互动引导卡片</summary>
    public bool BilibiliGuideCardEnabled
    {
        get => BiliGroup.GuideCardEnabled ?? false;
        set
        {
            if ((BiliGroup.GuideCardEnabled ?? false) == value) return;
            BiliGroup.GuideCardEnabled = value;
            if (value && BiliGroup.GuideCardFollowType is null)
                BiliGroup.GuideCardFollowType = 2;   // 页面默认「三连+关注」
            SyncBilibiliGuideCardSelection();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowBilibiliGuideCardOptions));
        }
    }

    /// <summary>开启后才显示样式 / 时间</summary>
    public bool ShowBilibiliGuideCardOptions => BilibiliGuideCardEnabled;

    /// <summary>三种样式（2 三连+关注 / 1 仅三连 / 0 仅关注）</summary>
    public ObservableCollection<BilibiliGuideCardOptionViewModel> BilibiliGuideCardOptions { get; } =
    [
        new(2, "三连+关注"),
        new(1, "仅三连"),
        new(0, "仅关注"),
    ];

    [RelayCommand]
    private void SelectBilibiliGuideCard(BilibiliGuideCardOptionViewModel? option)
    {
        if (option is null) return;
        BiliGroup.GuideCardEnabled = true;
        BiliGroup.GuideCardFollowType = option.Value;
        SyncBilibiliGuideCardSelection();
        OnPropertyChanged(nameof(BilibiliGuideCardEnabled));
        OnPropertyChanged(nameof(ShowBilibiliGuideCardOptions));
    }

    /// <summary>把已存样式刷到 UI（预设整份替换 / 换条目后调用）</summary>
    public void SyncBilibiliGuideCardSelection()
    {
        var selected = BiliGroup.GuideCardEnabled == true
            ? BiliGroup.GuideCardFollowType ?? 2
            : (int?)null;
        foreach (var o in BilibiliGuideCardOptions)
            o.IsSelected = selected == o.Value;
    }

    /// <summary>卡片起始时间（秒；界面写 <c>mm:ss</c> 或纯秒数）</summary>
    public string BilibiliGuideCardStartText
    {
        get
        {
            var seconds = Math.Max(0, BiliGroup.GuideCardStartSeconds ?? 0);
            return TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
        }
        set
        {
            var seconds = PublishFieldText.TryParseTime(value?.Trim(), out var ms)
                ? (int)(ms / 1000)
                : 0;
            if ((BiliGroup.GuideCardStartSeconds ?? 0) == seconds) return;
            BiliGroup.GuideCardStartSeconds = seconds;
            OnPropertyChanged();
        }
    }

    // ── 卡片 v2：关联视频 / 投票弹幕（2026-11-15 B5.2 补抓）──

    /// <summary>关联视频卡片开关（发布前在 cid 上建 type=2 卡片）</summary>
    public bool BilibiliRelatedVideoEnabled
    {
        get => BiliGroup.RelatedVideoEnabled ?? false;
        set
        {
            if ((BiliGroup.RelatedVideoEnabled ?? false) == value) return;
            BiliGroup.RelatedVideoEnabled = value;
            if (value && BiliGroup.RelatedVideoStartSeconds is null)
                BiliGroup.RelatedVideoStartSeconds = 0;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowBilibiliRelatedVideoOptions));
        }
    }

    /// <summary>开启后才显示目标 / 描述 / 时间</summary>
    public bool ShowBilibiliRelatedVideoOptions => BilibiliRelatedVideoEnabled;

    /// <summary>关联视频目标 BVID（wire 字段名是 <c>oid</c>）</summary>
    public string BilibiliRelatedVideoBvidText
    {
        get => BiliGroup.RelatedVideoBvid ?? "";
        set
        {
            var v = value.Trim();
            if ((BiliGroup.RelatedVideoBvid ?? "") == v) return;
            BiliGroup.RelatedVideoBvid = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>关联视频描述（<c>msg</c>）</summary>
    public string BilibiliRelatedVideoMsgText
    {
        get => BiliGroup.RelatedVideoMsg ?? "";
        set
        {
            var v = value.Trim();
            if ((BiliGroup.RelatedVideoMsg ?? "") == v) return;
            BiliGroup.RelatedVideoMsg = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>关联视频卡片起始时间（秒；界面写 <c>mm:ss</c> 或纯秒数）</summary>
    public string BilibiliRelatedVideoStartText
    {
        get
        {
            var seconds = Math.Max(0, BiliGroup.RelatedVideoStartSeconds ?? 0);
            return TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
        }
        set
        {
            var seconds = PublishFieldText.TryParseTime(value?.Trim(), out var ms)
                ? (int)(ms / 1000)
                : 0;
            if ((BiliGroup.RelatedVideoStartSeconds ?? 0) == seconds) return;
            BiliGroup.RelatedVideoStartSeconds = seconds;
            OnPropertyChanged();
        }
    }

    /// <summary>投票弹幕开关（发布前在 cid 上建 type=9 卡片）</summary>
    public bool BilibiliVoteEnabled
    {
        get => BiliGroup.VoteEnabled ?? false;
        set
        {
            if ((BiliGroup.VoteEnabled ?? false) == value) return;
            BiliGroup.VoteEnabled = value;
            if (value)
            {
                if (BiliGroup.VoteStartSeconds is null) BiliGroup.VoteStartSeconds = 0;
                if (BiliGroup.VoteDurationSeconds is null) BiliGroup.VoteDurationSeconds = 7;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowBilibiliVoteOptions));
        }
    }

    /// <summary>开启后才显示问题 / 选项 / 时间</summary>
    public bool ShowBilibiliVoteOptions => BilibiliVoteEnabled;

    /// <summary>投票问题（≤12 字）</summary>
    public string BilibiliVoteQuestionText
    {
        get => BiliGroup.VoteQuestion ?? "";
        set
        {
            var v = value.Trim();
            if ((BiliGroup.VoteQuestion ?? "") == v) return;
            BiliGroup.VoteQuestion = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    public string BilibiliVoteOptionAText
    {
        get => BiliGroup.VoteOptionA ?? "";
        set
        {
            var v = value.Trim();
            if ((BiliGroup.VoteOptionA ?? "") == v) return;
            BiliGroup.VoteOptionA = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    public string BilibiliVoteOptionBText
    {
        get => BiliGroup.VoteOptionB ?? "";
        set
        {
            var v = value.Trim();
            if ((BiliGroup.VoteOptionB ?? "") == v) return;
            BiliGroup.VoteOptionB = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    public string BilibiliVoteOptionCText
    {
        get => BiliGroup.VoteOptionC ?? "";
        set
        {
            var v = value.Trim();
            if ((BiliGroup.VoteOptionC ?? "") == v) return;
            BiliGroup.VoteOptionC = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    public string BilibiliVoteOptionDText
    {
        get => BiliGroup.VoteOptionD ?? "";
        set
        {
            var v = value.Trim();
            if ((BiliGroup.VoteOptionD ?? "") == v) return;
            BiliGroup.VoteOptionD = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>投票卡片起始时间（秒；界面写 <c>mm:ss</c> 或纯秒数）</summary>
    public string BilibiliVoteStartText
    {
        get
        {
            var seconds = Math.Max(0, BiliGroup.VoteStartSeconds ?? 0);
            return TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
        }
        set
        {
            var seconds = PublishFieldText.TryParseTime(value?.Trim(), out var ms)
                ? (int)(ms / 1000)
                : 0;
            if ((BiliGroup.VoteStartSeconds ?? 0) == seconds) return;
            BiliGroup.VoteStartSeconds = seconds;
            OnPropertyChanged();
        }
    }

    /// <summary>投票持续时间（秒 = 时间轴选区长度，默认 7）</summary>
    public string BilibiliVoteDurationText
    {
        get => Math.Max(0, BiliGroup.VoteDurationSeconds ?? 7).ToString();
        set
        {
            var seconds = int.TryParse(value?.Trim(), out var parsed)
                ? Math.Clamp(parsed, 0, 3600)
                : 7;
            if ((BiliGroup.VoteDurationSeconds ?? 7) == seconds) return;
            BiliGroup.VoteDurationSeconds = seconds;
            OnPropertyChanged();
        }
    }

    /// <summary>允许评论（null = 页面默认「允许」；执行端取反成 <c>up_close_reply</c>）</summary>
    public bool BilibiliAllowComment
    {
        get => Params.AllowComment ?? true;
        set
        {
            if ((Params.AllowComment ?? true) == value) return;
            Params.AllowComment = value;
            OnPropertyChanged();
        }
    }

    /// <summary>允许弹幕（同上，<c>up_close_danmu</c>）</summary>
    public bool BilibiliAllowDanmaku
    {
        get => Params.AllowDanmaku ?? true;
        set
        {
            if ((Params.AllowDanmaku ?? true) == value) return;
            Params.AllowDanmaku = value;
            OnPropertyChanged();
        }
    }

    /// <summary>精选评论 —— <c>up_selection_reply</c></summary>
    public bool BilibiliUpSelectionReply
    {
        get => BiliGroup.UpSelectionReply ?? false;
        set
        {
            if ((BiliGroup.UpSelectionReply ?? false) == value) return;
            BiliGroup.UpSelectionReply = value;
            OnPropertyChanged();
        }
    }

    /// <summary>禁止转载 —— <c>no_reprint</c></summary>
    public bool BilibiliNoReprint
    {
        get => BiliGroup.NoReprint ?? false;
        set
        {
            if ((BiliGroup.NoReprint ?? false) == value) return;
            BiliGroup.NoReprint = value;
            OnPropertyChanged();
        }
    }

    /// <summary>预约 —— <c>act_reserve_create</c></summary>
    public bool BilibiliActReserve
    {
        get => BiliGroup.ActReserve ?? false;
        set
        {
            if ((BiliGroup.ActReserve ?? false) == value) return;
            BiliGroup.ActReserve = value;
            OnPropertyChanged();
        }
    }

    /// <summary>预约标题 —— <c>act_reserve_create_title</c></summary>
    public string BilibiliActReserveTitleText
    {
        get => BiliGroup.ActReserveTitle ?? "";
        set
        {
            var v = value.Trim();
            if ((BiliGroup.ActReserveTitle ?? "") == v) return;
            BiliGroup.ActReserveTitle = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>AI 封面 —— <c>ai_cover</c>（留空 = 0）</summary>
    public string BilibiliAiCoverText
    {
        get => BiliGroup.AiCover?.ToString() ?? "";
        set
        {
            int? v = int.TryParse(value.Trim(), out var n) ? n : null;
            if (BiliGroup.AiCover == v) return;
            BiliGroup.AiCover = v;
            OnPropertyChanged();
        }
    }

    /// <summary>空间隐藏 —— <c>space_hidden</c>（留空 = 2，抓包默认）</summary>
    public string BilibiliSpaceHiddenText
    {
        get => (BiliGroup.SpaceHidden ?? 2).ToString();
        set
        {
            int? v = int.TryParse(value.Trim(), out var n) ? n : null;
            if ((BiliGroup.SpaceHidden ?? 2) == (v ?? 2)) return;
            BiliGroup.SpaceHidden = v;
            OnPropertyChanged();
        }
    }

    /// <summary>互动视频 —— <c>interactive</c></summary>
    public bool BilibiliInteractive
    {
        get => BiliGroup.Interactive ?? false;
        set
        {
            if ((BiliGroup.Interactive ?? false) == value) return;
            BiliGroup.Interactive = value;
            OnPropertyChanged();
        }
    }

    /// <summary>杜比音效 —— <c>dolby</c></summary>
    public bool BilibiliDolby
    {
        get => BiliGroup.Dolby ?? false;
        set
        {
            if ((BiliGroup.Dolby ?? false) == value) return;
            BiliGroup.Dolby = value;
            OnPropertyChanged();
        }
    }

    /// <summary>无损音乐 —— <c>lossless_music</c></summary>
    public bool BilibiliLosslessMusic
    {
        get => BiliGroup.LosslessMusic ?? false;
        set
        {
            if ((BiliGroup.LosslessMusic ?? false) == value) return;
            BiliGroup.LosslessMusic = value;
            OnPropertyChanged();
        }
    }

    // ═══ 头条长尾（阶段 5.6 / A1 / A2）═══
    //
    //  赞赏 / 原创 / 合集是公共概念：原创走全局开关、合集走全局下拉；
    //  这里只放头条专有字段（模板 / 滤镜 / 作品声明 / 视频生成图文）。
    //  商品 / 懂车帝 / 长尾属性 / 埋点的模型字段仍保留（旧档 / params 透传），
    //  但 2026-11-11 起不再有 UI 入口。

    public bool IsToutiao => Target.PlatformKey == PlatformNames.Toutiao;

    private ToutiaoExtras ToutiaoGroup => Params.Toutiao ??= new ToutiaoExtras();

    // ── 作品声明（A1，2026-11-10 抓包）──
    //  头条页面上的 7 个复选实际是单选组；这里做成「跟随全局 + 平台覆盖」：
    //    · null = 跟随全局 DeclarationKind（发起人的全局「内容声明」）；
    //    · 0    = 平台卡显式「不声明」；
    //    · 1/2/3/5/6/7/8 = 平台专有 ComplianceType（投资观点 / 健康医疗只在这里选）。

    /// <summary>全局内容声明语义（用于「跟随全局」预览；由编辑器在装配/切换时同步）</summary>
    public DeclarationKind GlobalDeclarationKind { get; set; }

    public ObservableCollection<ToutiaoDeclarationOptionViewModel> ToutiaoDeclarationOptions { get; } =
    [
        new(null, "跟随全局"),
        new(ToutiaoComplianceOptions.NoneValue, "不声明"),
        .. ToutiaoComplianceOptions.All.Select(o => new ToutiaoDeclarationOptionViewModel(o.Value, o.Label)),
    ];

    /// <summary>「跟随全局」这一格下方的说明：当前全局语义会映射成什么。</summary>
    public string ToutiaoDeclarationFollowHint
    {
        get
        {
            var label = DeclarationMapping.DeclarationLabel(GlobalDeclarationKind);
            return GlobalDeclarationKind switch
            {
                DeclarationKind.None => "跟随全局（当前：不声明）",
                DeclarationKind.Marketing => "跟随全局（当前：含营销推广 —— 头条没有对应声明，不会下发）",
                _ => $"跟随全局（当前：{label} → ComplianceType "
                     + $"{DeclarationMapping.ToutiaoComplianceType(GlobalDeclarationKind, null)}）",
            };
        }
    }

    /// <summary>选中「自行拍摄」（含跟随全局时的全局自行拍摄）才展开城市 + 时间。</summary>
    public bool ShowToutiaoComplianceCreativeInfo =>
        DeclarationMapping.ToutiaoComplianceType(
            GlobalDeclarationKind, Params.Toutiao?.DeclarationOverride) == 1;

    /// <summary>自行拍摄 · 城市 —— <c>CreativeInfo.City</c></summary>
    public string ToutiaoComplianceCityText
    {
        get => Params.Toutiao?.ComplianceCity ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Toutiao?.ComplianceCity ?? "") == v) return;
            if (v.Length == 0)
            {
                if (Params.Toutiao is not null) Params.Toutiao.ComplianceCity = null;
            }
            else
            {
                ToutiaoGroup.ComplianceCity = v;
            }
            OnPropertyChanged();
        }
    }

    /// <summary>自行拍摄 · 日期（与时间合存 <see cref="ToutiaoExtras.ComplianceTime"/>）</summary>
    public DateTimeOffset? ToutiaoComplianceDate
    {
        get => Params.Toutiao?.ComplianceTime is { } t ? new DateTimeOffset(t) : null;
        set
        {
            var existing = Params.Toutiao?.ComplianceTime;
            if (value is null)
            {
                if (Params.Toutiao is not null) Params.Toutiao.ComplianceTime = null;
            }
            else
            {
                var time = existing?.TimeOfDay ?? TimeSpan.Zero;
                ToutiaoGroup.ComplianceTime = value.Value.Date.Add(time);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToutiaoComplianceClock));
        }
    }

    /// <summary>自行拍摄 · 时刻（与日期合存 <see cref="ToutiaoExtras.ComplianceTime"/>）</summary>
    public TimeSpan? ToutiaoComplianceClock
    {
        get => Params.Toutiao?.ComplianceTime?.TimeOfDay;
        set
        {
            var existing = Params.Toutiao?.ComplianceTime;
            if (value is null)
            {
                if (Params.Toutiao is not null) Params.Toutiao.ComplianceTime = null;
            }
            else
            {
                ToutiaoGroup.ComplianceTime = (existing?.Date ?? DateTime.Today).Add(value.Value);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToutiaoComplianceDate));
        }
    }

    [RelayCommand]
    private void SelectToutiaoDeclaration(ToutiaoDeclarationOptionViewModel? option)
    {
        if (option is null) return;

        if (option.Value is { } value)
        {
            if (value != ToutiaoComplianceOptions.NoneValue
                && !ToutiaoComplianceOptions.IsKnownType(value)) return;
            ToutiaoGroup.DeclarationOverride = value;
        }
        else if (Params.Toutiao is not null)
        {
            // 选「跟随全局」不创建空的 ToutiaoExtras（免得把「无覆盖」误判成「有覆盖」）
            Params.Toutiao.DeclarationOverride = null;
        }

        SyncToutiaoDeclarationSelection();
    }

    /// <summary>把 <see cref="ToutiaoExtras.DeclarationOverride"/> 的单选态刷到 UI。</summary>
    public void SyncToutiaoDeclarationSelection()
    {
        var selected = Params.Toutiao?.DeclarationOverride;
        foreach (var o in ToutiaoDeclarationOptions)
            o.IsSelected = o.Value == selected;
        OnPropertyChanged(nameof(ToutiaoDeclarationFollowHint));
        OnPropertyChanged(nameof(ShowToutiaoComplianceCreativeInfo));
    }

    /// <summary>全局声明变了 → 「跟随全局」预览与「自行拍摄」二级显隐要跟着变。</summary>
    public void SetGlobalDeclaration(DeclarationKind kind)
    {
        if (GlobalDeclarationKind == kind) return;
        GlobalDeclarationKind = kind;
        OnPropertyChanged(nameof(ToutiaoDeclarationFollowHint));
        OnPropertyChanged(nameof(ShowToutiaoComplianceCreativeInfo));
    }

    // ── 视频生成图文（A2，2026-11-10 抓包）──
    //  0 / 未选 = 不下发；1 = 与视频同时发布；2 = 仅保存草稿。
    //  页面偏好接口只记 UI 偏好，不下发；这里只维护发布体字段。

    public ObservableCollection<ToutiaoTransArticleOptionViewModel> ToutiaoTransArticleOptions { get; } =
    [
        new(0, "不生成"),
        new(1, "与视频同时发布"),
        // 2026-11-11 真账号定论：=2 只在视频本身存草稿（PublishType=0）时生效；
        // 当前发布流程恒发 PublishType=1，平台会静默忽略 =2、图文随视频公开。
        // 「存草稿发布方式」另立阶段，先置灰，别让用户以为点了会存草稿。
        new(2, "仅保存草稿（暂不可用）", isEnabled: false),
    ];

    [RelayCommand]
    private void SelectToutiaoTransArticle(ToutiaoTransArticleOptionViewModel? option)
    {
        if (option is null) return;

        // 置灰项在命令层再挡一道（冒烟/键盘/脚本都不该把它写进发布体）。
        if (!option.IsEnabled) return;

        if (option.Value is 1)
        {
            ToutiaoGroup.VideoTransArticleFlag = option.Value;
        }
        else if (Params.Toutiao is not null)
        {
            // 「不生成」= 清掉字段；不创建空的 ToutiaoExtras
            Params.Toutiao.VideoTransArticleFlag = null;
        }

        SyncToutiaoTransArticleSelection();
    }

    /// <summary>把 <see cref="ToutiaoExtras.VideoTransArticleFlag"/> 的单选态刷到 UI。</summary>
    public void SyncToutiaoTransArticleSelection()
    {
        var selected = Params.Toutiao?.VideoTransArticleFlag ?? 0;
        foreach (var o in ToutiaoTransArticleOptions)
            o.IsSelected = o.Value == selected;
    }

    /// <summary>标签 —— <c>Label</c>（数组）；与 B站标签同源（<see cref="PlatformParams.Tags"/>）</summary>
    public string ToutiaoTagsText
    {
        get => string.Join(", ", Params.Tags);
        set
        {
            var tags = value
                .Split([',', '，', '、', ';', '；'], StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (Params.Tags.SequenceEqual(tags)) return;
            Params.Tags = tags;
            OnPropertyChanged();
        }
    }

    /// <summary>扩展链接 —— <c>ExternalLink</c></summary>
    public string ToutiaoExternalLinkText
    {
        get => Params.ExternalLink ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.ExternalLink ?? "") == v) return;
            Params.ExternalLink = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>封面标题 —— <c>CoverTitles</c>（执行端包一层数组）</summary>
    public string ToutiaoCoverTitleText
    {
        get => Params.CoverTitle ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.CoverTitle ?? "") == v) return;
            Params.CoverTitle = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>赞赏 —— <c>Praise</c>（仅头条）</summary>
    public bool ToutiaoPraise
    {
        get => Params.Praise ?? false;
        set
        {
            if ((Params.Praise ?? false) == value) return;
            Params.Praise = value;
            OnPropertyChanged();
        }
    }

    // ── 互动贴纸（A3，2026-11-10 抓包）—— v1 只做「关注引导」──
    //  发布前调 /ixigua/api/int/sticker/create 现建，ID 并入 AttrsValueMap.InteractStickers。
    //  页面限制「横版且 ≥10 秒」，预检只 Warning；创建失败不拦发布。

    /// <summary>关注引导贴纸开关</summary>
    public bool ToutiaoFollowSticker
    {
        get => Params.Toutiao?.FollowSticker ?? false;
        set
        {
            if ((Params.Toutiao?.FollowSticker ?? false) == value) return;
            if (value)
            {
                ToutiaoGroup.FollowSticker = true;
            }
            else if (Params.Toutiao is not null)
            {
                // 关掉只清开关；开始秒/时长保留，方便用户再打开时不用重填
                Params.Toutiao.FollowSticker = false;
            }
            OnPropertyChanged();
        }
    }

    /// <summary>贴纸开始秒 —— 空字符串 = 默认 0</summary>
    public string ToutiaoStickerStartText
    {
        get => (Params.Toutiao?.StickerStartSec ?? 0).ToString();
        set
        {
            var raw = value.Trim();
            var parsed = raw.Length == 0
                ? (int?)null
                : int.TryParse(raw, out var v) ? Math.Max(0, v) : null;
            if (Params.Toutiao?.StickerStartSec == parsed) return;
            if (parsed is null)
            {
                if (Params.Toutiao is not null) Params.Toutiao.StickerStartSec = null;
            }
            else
            {
                ToutiaoGroup.StickerStartSec = parsed;
            }
            OnPropertyChanged();
        }
    }

    /// <summary>贴纸持续秒 —— 空字符串 = 默认 5</summary>
    public string ToutiaoStickerDurationText
    {
        get => (Params.Toutiao?.StickerDurationSec
                ?? ToutiaoStickerService.DefaultDurationSec).ToString();
        set
        {
            var raw = value.Trim();
            var parsed = raw.Length == 0
                ? (int?)null
                : int.TryParse(raw, out var v) ? Math.Max(1, v) : null;
            if (Params.Toutiao?.StickerDurationSec == parsed) return;
            if (parsed is null)
            {
                if (Params.Toutiao is not null) Params.Toutiao.StickerDurationSec = null;
            }
            else
            {
                ToutiaoGroup.StickerDurationSec = parsed;
            }
            OnPropertyChanged();
        }
    }

    // ── 头条发布权限（GetPublishAuth，按需检查；2026-11-10）──
    //   「赞赏」能不能用由账号权限决定（HasPraisePermission=false 时页面不显示开关、
    //   提交体里的 Praise=true 被静默忽略），所以把结果显式摆给用户，不让他猜。

    [ObservableProperty] private bool _isToutiaoAuthBusy;
    [ObservableProperty] private string _toutiaoAuthStatusText = "";
    [ObservableProperty] private bool _toutiaoAuthStatusIsError;

    /// <summary>赞赏权限（<c>null</c> = 还没查 / 响应里没有该字段）</summary>
    [ObservableProperty] private bool? _toutiaoPraiseAllowed;

    public bool HasToutiaoAuthStatus => ToutiaoAuthStatusText.Length > 0;

    partial void OnToutiaoAuthStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasToutiaoAuthStatus));

    /// <summary>模板名 —— <c>TemplateName</c>（平台封面编辑器元数据，见 UI 提示）</summary>
    public string ToutiaoTemplateText
    {
        get => ToutiaoGroup.TemplateName ?? "";
        set
        {
            var v = value.Trim();
            if ((ToutiaoGroup.TemplateName ?? "") == v) return;
            ToutiaoGroup.TemplateName = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>滤镜名 —— <c>FilterName</c>（平台封面编辑器元数据，见 UI 提示）</summary>
    public string ToutiaoFilterText
    {
        get => ToutiaoGroup.FilterName ?? "";
        set
        {
            var v = value.Trim();
            if ((ToutiaoGroup.FilterName ?? "") == v) return;
            ToutiaoGroup.FilterName = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>商品（原始 JSON 数组串）—— <c>Commodity</c></summary>
    public string ToutiaoCommodityJson
    {
        get => ToutiaoGroup.Commodity ?? "";
        set
        {
            var v = value.Trim();
            if ((ToutiaoGroup.Commodity ?? "") == v) return;
            ToutiaoGroup.Commodity = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>懂车帝视频结构（原始 JSON 对象串）—— <c>CompassVideo</c></summary>
    public string ToutiaoCompassJson
    {
        get => ToutiaoGroup.CompassVideo ?? "";
        set
        {
            var v = value.Trim();
            if ((ToutiaoGroup.CompassVideo ?? "") == v) return;
            ToutiaoGroup.CompassVideo = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>长尾属性（原始 JSON 对象串）—— <c>AttrsValueMap</c></summary>
    public string ToutiaoAttrsJson
    {
        get => ToutiaoGroup.AttrsValueMap ?? "";
        set
        {
            var v = value.Trim();
            if ((ToutiaoGroup.AttrsValueMap ?? "") == v) return;
            ToutiaoGroup.AttrsValueMap = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>埋点扩展（原始 JSON 对象串）—— <c>etData</c></summary>
    public string ToutiaoEtDataJson
    {
        get => ToutiaoGroup.EtData ?? "";
        set
        {
            var v = value.Trim();
            if ((ToutiaoGroup.EtData ?? "") == v) return;
            ToutiaoGroup.EtData = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    // ═══ 声明联动（老版 HomeViewModel.cs:2295 / :2288 的两条规则）═══

    /// <summary>小红书开启原创声明 → 「内容为转载」不可选（且自动切「自行拍摄」）</summary>
    public bool IsRepostDisabledByOriginal => IsXhs && IsOriginal;

    /// <summary>视频号的内容声明来自账号动态标签列表（不是本地固定枚举）</summary>
    public bool ShowWechatTagPicker => IsWechatVideo;

    /// <summary>小红书「视频需 ≥10 秒」提示 —— 老版只有软提示、从不强制（缺陷 L12）</summary>
    public bool ShowXhsDurationHint => IsXhs;

    /// <summary>
    /// 条目级（全局）可见性 —— 该目标没覆盖时摘要要显示它。
    /// **不能直接拿 Params.Visibility ?? Public**：那会让「跟随全局」的目标显示成「公开」，
    /// 与真实会发出去的可见性不符（全局选了「只给谁看」，卡片却写「公开」）。
    /// </summary>
    public PublishVisibility GlobalVisibility { get; set; } = PublishVisibility.Public;

    /// <summary>重算摘要行 —— 可见性 · 合集 · 位置（各自缺省时不占位）</summary>
    public void RefreshSummary()
    {
        var parts = new List<string>();

        // 覆盖值优先，否则跟随全局；再做一次平台回退（不支持的态在 UI 上就写成实际会发的）
        var vis = VisibilityMapping.Coerce(Target.PlatformKey, Params.Visibility ?? GlobalVisibility);
        parts.Add(VisibilityText(vis));

        if (!string.IsNullOrWhiteSpace(Params.CollectionName)) parts.Add($"合集「{Params.CollectionName}」");
        // 抖音「添加标签」与位置共用一个 anchor（单选）：有标签时摘要不写位置，避免显示两个都生效
        if (Params.Location is { } poi && Params.Douyin?.Anchor is null) parts.Add(poi.Name);
        if (Params.Douyin?.Anchor is { } anchor) parts.Add($"标签「{anchor.Title}」");
        // 话题（2.6.3）/ 官方活动（2.6.4）：只给个数 —— 名字在平台卡的芯片区，摘要行不抢空间
        if (Params.TopicRefs.Count > 0) parts.Add($"话题 {Params.TopicRefs.Count} 个");
        if (Params.Activities.Count > 0) parts.Add($"活动 {Params.Activities.Count} 个");
        // 原创是支持平台才显示（不算「平台摘要」的必选项）
        if (OriginalMapping.Supports(Target.PlatformKey) && IsOriginal) parts.Add("原创");

        SummaryText = string.Join(" · ", parts);
        IsOverridden = Preset is not null && !SameAsPreset();
    }

    private static string VisibilityText(PublishVisibility v) => v switch
    {
        PublishVisibility.Private => "仅自己",
        PublishVisibility.MutualFriends => "仅互关好友",
        PublishVisibility.OnlyUsers => "只给谁看",
        PublishVisibility.ExcludeUsers => "不给谁看",
        _ => "公开",
    };

    /// <summary>与账号预设逐字段比对（只比用户可改的那些）</summary>
    private bool SameAsPreset()
    {
        if (Preset is null) return true;
        return Params.CollectionId == Preset.CollectionId
            && Params.CollectionOrder == Preset.CollectionOrder
            && Params.Location == Preset.Location
            && Params.Visibility == Preset.Visibility
            && Params.AllowDownload == Preset.AllowDownload
            && Params.ShowInNearby == Preset.ShowInNearby
            && Params.AllowSameFrame == Preset.AllowSameFrame
            && Params.IsOriginal == Preset.IsOriginal
            && Params.HotspotSentence == Preset.HotspotSentence
            // 抖音长尾（2.1）
            && Params.MusicId == Preset.MusicId
            && Params.Chapters.SequenceEqual(Preset.Chapters)
            && Params.ActivityIds.SequenceEqual(Preset.ActivityIds)
            // 结构化话题 / 官方活动（2.6）—— 只比「选中了哪些」，不比快照里的
            // 截止时间/封面（那是拉取时刷新的，不该让整条显示成「已覆盖预设」）
            && SameTopicRefs(Params.TopicRefs, Preset.TopicRefs)
            && SameActivities(Params.Activities, Preset.Activities)
            && Params.SyncTargets.SequenceEqual(Preset.SyncTargets)
            && SameDouyinExtras(Params.Douyin, Preset.Douyin)
            // 快手长尾（2.2）
            && Params.NotifyFollowers == Preset.NotifyFollowers
            && Params.CoverTitle == Preset.CoverTitle
            && SameKuaishouExtras(Params.Kuaishou, Preset.Kuaishou)
            // 小红书长尾（2.3）
            && Params.AllowComment == Preset.AllowComment
            && Params.Summary == Preset.Summary
            && SameXhsExtras(Params.Xhs, Preset.Xhs)
            // 视频号长尾（2.4）
            && Params.ShortTitle == Preset.ShortTitle
            && Params.ExternalLink == Preset.ExternalLink
            && SameWechatVideoExtras(Params.WechatVideo, Preset.WechatVideo)
            // B站长尾（阶段 4.6）
            && Params.Tags.SequenceEqual(Preset.Tags)
            && Params.AllowDanmaku == Preset.AllowDanmaku
            && SameBilibiliExtras(Params.Bilibili, Preset.Bilibili)
            // 头条长尾（阶段 5.6）
            && Params.Praise == Preset.Praise
            && SameToutiaoExtras(Params.Toutiao, Preset.Toutiao);
    }

    /// <summary>话题「选中了哪些」= 名字 + 平台 ID 序列（同名换 ID 也算改过）</summary>
    private static bool SameTopicRefs(IReadOnlyList<TopicRef> a, IReadOnlyList<TopicRef> b) =>
        a.Count == b.Count
        && a.Zip(b).All(x => x.First.Name == x.Second.Name && x.First.Id == x.Second.Id);

    /// <summary>活动只比 ID 序列 —— 名称/截止时间可能因平台刷新而变，不是用户改动</summary>
    private static bool SameActivities(IReadOnlyList<ActivityRef> a, IReadOnlyList<ActivityRef> b) =>
        a.Count == b.Count && a.Zip(b).All(x => x.First.Id == x.Second.Id);

    private static bool SameDouyinExtras(DouyinExtras? a, DouyinExtras? b) =>
        a?.Anchor == b?.Anchor
        && a?.CoverCropWidth == b?.CoverCropWidth
        && a?.CoverCropHeight == b?.CoverCropHeight
        && a?.CoverToolsInfo == b?.CoverToolsInfo
        && a?.CoverToolsExtendInfo == b?.CoverToolsExtendInfo
        && a?.Mentions == b?.Mentions
        && a?.InteractionStickers == b?.InteractionStickers;

    private static bool SameKuaishouExtras(KuaishouExtras? a, KuaishouExtras? b) =>
        a?.CoverTitleStyle == b?.CoverTitleStyle
        && a?.PkCoverKey == b?.PkCoverKey
        && a?.PkCoverTimeStamp == b?.PkCoverTimeStamp
        && a?.PkCoverType == b?.PkCoverType
        && a?.PkCoverSize == b?.PkCoverSize
        && a?.ProfileCoverKey == b?.ProfileCoverKey
        && a?.RiseQuality == b?.RiseQuality
        && a?.UseAiCaption == b?.UseAiCaption
        && a?.UseAiCaptionCover == b?.UseAiCaptionCover
        && a?.UseAiCover == b?.UseAiCover
        && a?.UseIdealTime == b?.UseIdealTime
        && a?.DisallowRecreation == b?.DisallowRecreation
        && a?.Domain == b?.Domain
        && a?.SecondDomain == b?.SecondDomain
        && a?.MovieId == b?.MovieId
        && a?.ProjectId == b?.ProjectId
        && a?.HotspotId == b?.HotspotId
        && a?.HotspotContent == b?.HotspotContent
        && a?.AssociateTask == b?.AssociateTask   // record 值相等（五字段 + 展示快照）
        && (a?.CoPublishUsers ?? []).SequenceEqual(b?.CoPublishUsers ?? [])
        && a?.CoPublishUsersJson == b?.CoPublishUsersJson
        && a?.CoPublishRole == b?.CoPublishRole;

    private static bool SameXhsExtras(XhsExtras? a, XhsExtras? b) =>
        a?.Ats == b?.Ats
        && a?.GoodsInfo == b?.GoodsInfo
        && a?.BizRelations == b?.BizRelations
        && a?.SketchCollectionId == b?.SketchCollectionId
        && a?.OrderBind == b?.OrderBind
        && a?.ChapterSyncText == b?.ChapterSyncText
        && a?.PkCoverBizRelations == b?.PkCoverBizRelations
        && a?.GroupBind == b?.GroupBind   // record 值相等（四字段）
        && a?.PkCoverEnabled == b?.PkCoverEnabled
        && SameStringList(a?.PkCoverImages, b?.PkCoverImages)
        && a?.Route == b?.Route   // record 值相等（travelId/名称/类型/段数）
        // 视频标记：按类型+ID+名称+起止秒逐项比对（顺序=发布顺序）
        && (a?.Markers ?? []).Select(m => (m.Type, m.Id, m.Name, m.StartSeconds, m.EndSeconds))
            .SequenceEqual((b?.Markers ?? [])
                .Select(m => (m.Type, m.Id, m.Name, m.StartSeconds, m.EndSeconds)))
        && a?.PollQuestion == b?.PollQuestion
        && SameStringList(a?.PollOptions, b?.PollOptions)
        && a?.ReferencedNote?.Id == b?.ReferencedNote?.Id;

    /// <summary>两个字符串列表逐项相等（null 与空列表视为不同 —— 存档语义要忠实地反映）</summary>
    private static bool SameStringList(List<string>? a, List<string>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);

    private static bool SameWechatVideoExtras(WechatVideoExtras? a, WechatVideoExtras? b) =>
        a?.CardShowStyle == b?.CardShowStyle
        && a?.ExtReadingTitle == b?.ExtReadingTitle
        && a?.ExtReadingType == b?.ExtReadingType
        // @好友：平台只认昵称，按昵称序列比对（头像换了不算改动）
        && (a?.MentionUsers ?? []).Select(m => m.Nickname)
            .SequenceEqual((b?.MentionUsers ?? []).Select(m => m.Nickname));

    private static bool SameBilibiliExtras(BilibiliExtras? a, BilibiliExtras? b) =>
        a?.Tid == b?.Tid
        && a?.HumanType2 == b?.HumanType2
        && a?.NoReprint == b?.NoReprint
        && a?.Dynamic == b?.Dynamic
        && a?.Watermark == b?.Watermark
        && a?.SubtitleOpen == b?.SubtitleOpen
        && a?.SubtitleLan == b?.SubtitleLan
        && a?.SubtitleFile == b?.SubtitleFile
        && a?.SubtitleFileLan == b?.SubtitleFileLan
        && a?.GuideCardEnabled == b?.GuideCardEnabled
        && a?.GuideCardFollowType == b?.GuideCardFollowType
        && a?.GuideCardStartSeconds == b?.GuideCardStartSeconds
        && a?.UpSelectionReply == b?.UpSelectionReply
        && a?.ActReserve == b?.ActReserve
        && a?.ActReserveTitle == b?.ActReserveTitle
        && a?.AiCover == b?.AiCover
        && a?.IsAbCover == b?.IsAbCover
        && a?.AbCoverInfo == b?.AbCoverInfo
        && a?.Interactive == b?.Interactive
        && a?.SpaceHidden == b?.SpaceHidden
        && a?.Dolby == b?.Dolby
        && a?.LosslessMusic == b?.LosslessMusic;

    private static bool SameToutiaoExtras(ToutiaoExtras? a, ToutiaoExtras? b) =>
        a?.TemplateName == b?.TemplateName
        && a?.FilterName == b?.FilterName
        && a?.Commodity == b?.Commodity
        && a?.CompassVideo == b?.CompassVideo
        && a?.AttrsValueMap == b?.AttrsValueMap
        && a?.EtData == b?.EtData
        && a?.FollowSticker == b?.FollowSticker
        && a?.StickerStartSec == b?.StickerStartSec
        && a?.StickerDurationSec == b?.StickerDurationSec;

    /// <summary>把该行退回账号预设</summary>
    public void ResetToPreset()
    {
        if (Preset is null) return;
        Params.CollectionId = Preset.CollectionId;
        Params.CollectionName = Preset.CollectionName;
        Params.CollectionOrder = Preset.CollectionOrder;
        Params.Location = Preset.Location;
        Params.Visibility = Preset.Visibility;
        Params.AllowDownload = Preset.AllowDownload;
        Params.ShowInNearby = Preset.ShowInNearby;
        Params.AllowSameFrame = Preset.AllowSameFrame;
        Params.IsOriginal = Preset.IsOriginal;
        Params.HotspotSentence = Preset.HotspotSentence;
        // 抖音长尾 + 整组专有参数（预设里有什么就恢复什么）
        Params.MusicId = Preset.MusicId;
        Params.Chapters = [.. Preset.Chapters];
        Params.ActivityIds = [.. Preset.ActivityIds];
        // 结构化话题 / 官方活动（2.6.3：平台卡能直接改话题，「恢复账号默认」必须一并还原，
        // 否则只恢复了别的字段、话题还留着 —— 界面上看不出来，发布时按旧话题发出去）
        Params.TopicRefs = [.. Preset.TopicRefs];
        Params.Topics = [.. Preset.Topics];
        Params.Activities = [.. Preset.Activities];
        Params.SyncTargets = [.. Preset.SyncTargets];
        Params.Douyin = Preset.Douyin?.Clone();
        // 快手长尾（2.2）
        Params.NotifyFollowers = Preset.NotifyFollowers;
        Params.CoverTitle = Preset.CoverTitle;
        Params.Kuaishou = Preset.Kuaishou?.Clone();
        // 小红书长尾（2.3）
        Params.AllowComment = Preset.AllowComment;
        Params.Summary = Preset.Summary;
        Params.Xhs = Preset.Xhs?.Clone();
        // 视频号长尾（2.4）
        Params.ShortTitle = Preset.ShortTitle;
        Params.ExternalLink = Preset.ExternalLink;
        Params.WechatVideo = Preset.WechatVideo?.Clone();
        // B站长尾（阶段 4.6）
        Params.Tags = [.. Preset.Tags];
        Params.AllowDanmaku = Preset.AllowDanmaku;
        Params.Bilibili = Preset.Bilibili?.Clone();
        // 头条长尾（阶段 5.6）
        Params.Praise = Preset.Praise;
        Params.Toutiao = Preset.Toutiao?.Clone();
        RaiseAll();
    }

    /// <summary>参数被别处（全局区）改动后刷新全部绑定</summary>
    public void RaiseAll()
    {
        // Params.Xhs 可能被预设整体替换 —— 已选 @用户 从存档的 common.ats 重新种子化，
        // 投票输入（问题 + 选项）同样按存档重建（A4）
        RefreshXhsMentions();
        RefreshXhsPoll();
        RefreshXhsReferencedNote();
        OnPropertyChanged(nameof(ShowInNearby));
        OnPropertyChanged(nameof(AllowSameFrame));
        OnPropertyChanged(nameof(IsOriginal));
        OnPropertyChanged(nameof(HotspotSentence));
        // 计算属性必须手动播报（多视图常驻可视树）：预设/params 改了底层字段，
        // 不通知的话「未选择 / 已选」和摘要行停在旧值（2026-11-10 音乐选择器同批补）
        OnPropertyChanged(nameof(HasHotspot));
        OnPropertyChanged(nameof(HotspotDisplay));
        OnPropertyChanged(nameof(IsRepostDisabledByOriginal));
        OnPropertyChanged(nameof(MusicIdText));
        OnPropertyChanged(nameof(HasMusic));
        OnPropertyChanged(nameof(MusicDisplay));
        OnPropertyChanged(nameof(ChaptersText));
        OnPropertyChanged(nameof(SyncToToutiao));
        // 添加标签（B1）：预设/params 整体换掉 Params.Douyin 后，卡片上的「已选」与摘要要跟着变
        OnPropertyChanged(nameof(HasAnchor));
        OnPropertyChanged(nameof(AnchorDisplay));
        OnPropertyChanged(nameof(CoverCropWidthText));
        OnPropertyChanged(nameof(CoverCropHeightText));
        OnPropertyChanged(nameof(CollectionOrderText));
        OnPropertyChanged(nameof(HasCollection));
        OnPropertyChanged(nameof(BilibiliPartitionText));
        OnPropertyChanged(nameof(BilibiliPartitionDisplay));
        OnPropertyChanged(nameof(BilibiliTidHint));
        // 快手长尾（2.2）
        OnPropertyChanged(nameof(NotifyFollowers));
        OnPropertyChanged(nameof(KuaishouCoverTitleText));
        OnPropertyChanged(nameof(PkCoverKeyText));
        OnPropertyChanged(nameof(PkCoverTimeStampText));
        OnPropertyChanged(nameof(PkCoverTypeText));
        OnPropertyChanged(nameof(PkCoverSizeText));
        OnPropertyChanged(nameof(ProfileCoverKeyText));
        OnPropertyChanged(nameof(RiseQuality));
        OnPropertyChanged(nameof(UseAiCaption));
        OnPropertyChanged(nameof(UseAiCaptionCover));
        OnPropertyChanged(nameof(UseAiCover));
        OnPropertyChanged(nameof(UseIdealTime));
        OnPropertyChanged(nameof(DisallowRecreation));
        OnPropertyChanged(nameof(DomainText));
        OnPropertyChanged(nameof(SecondDomainText));
        OnPropertyChanged(nameof(HasKsDomain));
        OnPropertyChanged(nameof(KsDomainDisplay));
        OnPropertyChanged(nameof(HasKsSecondDomain));
        OnPropertyChanged(nameof(KsSecondDomainDisplay));
        OnPropertyChanged(nameof(HasKsDomainOrSecond));
        OnPropertyChanged(nameof(KsSecondDomainCaption));
        OnPropertyChanged(nameof(HasKsHotspot));
        OnPropertyChanged(nameof(KsHotspotDisplay));
        // 关联小程序（2026-11-15）：预设整份替换 / params 导入后，行展示与链接框要跟着重建
        OnPropertyChanged(nameof(HasKsAssociate));
        OnPropertyChanged(nameof(KsAssociateDisplay));
        KsAssociateLink = Params.Kuaishou?.AssociateTask?.Link ?? "";
        // 共创（2026-11-15）：底层列表换了要重新种子化，否则展示停在旧名单
        RefreshKsCoPublishUsers();
        OnPropertyChanged(nameof(KuaishouMovieIdText));
        OnPropertyChanged(nameof(KuaishouProjectIdText));
        OnPropertyChanged(nameof(CoPublishUsersJson));
        // 小红书长尾（2.3）
        OnPropertyChanged(nameof(AllowComment));
        OnPropertyChanged(nameof(XhsSummaryText));
        OnPropertyChanged(nameof(ChapterSyncText));
        OnPropertyChanged(nameof(XhsSketchCollectionIdText));
        OnPropertyChanged(nameof(HasSketch));
        OnPropertyChanged(nameof(SketchDisplay));
        OnPropertyChanged(nameof(HasXhsMentions));
        OnPropertyChanged(nameof(XhsMentionDisplay));
        OnPropertyChanged(nameof(XhsAtsJson));
        // 选择群聊（2026-11-15）：预设整份替换 / 换条目后已选态与展示文本要重建
        OnPropertyChanged(nameof(HasXhsGroup));
        OnPropertyChanged(nameof(XhsGroupDisplay));
        // PK 封面（2026-11-15）：已选图列表从存档重挂
        OnPropertyChanged(nameof(XhsPkCoverEnabled));
        RefreshXhsPkCovers();
        // 视频标记（2026-11-15）：已选标记从存档重挂
        RefreshXhsMarkers();
        // 添加路线（2026-11-15）：已建路线从存档重挂；未保存的候选/草稿清掉
        ClearXhsRouteDraft();
        OnPropertyChanged(nameof(HasXhsRoute));
        OnPropertyChanged(nameof(IsXhsRouteBuilderVisible));
        OnPropertyChanged(nameof(XhsRouteDisplay));
        // 视频号长尾（2.4）
        OnPropertyChanged(nameof(WechatShortTitleText));
        OnPropertyChanged(nameof(WechatExternalLinkText));
        OnPropertyChanged(nameof(WechatExtReadingTitleText));
        OnPropertyChanged(nameof(WechatExtReadingTypeIndex));
        OnPropertyChanged(nameof(WechatCardShowStyleIndex));
        // B站长尾（阶段 4.6）
        OnPropertyChanged(nameof(BilibiliTagsText));
        OnPropertyChanged(nameof(BilibiliDynamicText));
        OnPropertyChanged(nameof(BilibiliWatermark));
        OnPropertyChanged(nameof(BilibiliSubtitleOpen));
        OnPropertyChanged(nameof(BilibiliSubtitleLanText));
        // 字幕上传 / 互动引导卡片（B5）：预设整份替换 / params 导入后行展示要跟着重建
        OnPropertyChanged(nameof(HasBilibiliSubtitleFile));
        OnPropertyChanged(nameof(BilibiliSubtitleFileDisplay));
        OnPropertyChanged(nameof(BilibiliSubtitleFileLanText));
        OnPropertyChanged(nameof(BilibiliGuideCardEnabled));
        OnPropertyChanged(nameof(ShowBilibiliGuideCardOptions));
        OnPropertyChanged(nameof(BilibiliGuideCardStartText));
        SyncBilibiliGuideCardSelection();
        // 卡片 v2（B5.2）：关联视频 / 投票弹幕 —— 预设整份替换 / params 导入后同样要重建
        OnPropertyChanged(nameof(BilibiliRelatedVideoEnabled));
        OnPropertyChanged(nameof(ShowBilibiliRelatedVideoOptions));
        OnPropertyChanged(nameof(BilibiliRelatedVideoBvidText));
        OnPropertyChanged(nameof(BilibiliRelatedVideoMsgText));
        OnPropertyChanged(nameof(BilibiliRelatedVideoStartText));
        OnPropertyChanged(nameof(BilibiliVoteEnabled));
        OnPropertyChanged(nameof(ShowBilibiliVoteOptions));
        OnPropertyChanged(nameof(BilibiliVoteQuestionText));
        OnPropertyChanged(nameof(BilibiliVoteOptionAText));
        OnPropertyChanged(nameof(BilibiliVoteOptionBText));
        OnPropertyChanged(nameof(BilibiliVoteOptionCText));
        OnPropertyChanged(nameof(BilibiliVoteOptionDText));
        OnPropertyChanged(nameof(BilibiliVoteStartText));
        OnPropertyChanged(nameof(BilibiliVoteDurationText));
        OnPropertyChanged(nameof(BilibiliAllowComment));
        OnPropertyChanged(nameof(BilibiliAllowDanmaku));
        OnPropertyChanged(nameof(BilibiliUpSelectionReply));
        OnPropertyChanged(nameof(BilibiliNoReprint));
        OnPropertyChanged(nameof(BilibiliActReserve));
        OnPropertyChanged(nameof(BilibiliActReserveTitleText));
        OnPropertyChanged(nameof(BilibiliAiCoverText));
        OnPropertyChanged(nameof(BilibiliSpaceHiddenText));
        OnPropertyChanged(nameof(BilibiliInteractive));
        OnPropertyChanged(nameof(BilibiliDolby));
        OnPropertyChanged(nameof(BilibiliLosslessMusic));
        // 头条长尾（阶段 5.6）
        OnPropertyChanged(nameof(ToutiaoTagsText));
        OnPropertyChanged(nameof(ToutiaoExternalLinkText));
        OnPropertyChanged(nameof(ToutiaoCoverTitleText));
        OnPropertyChanged(nameof(ToutiaoPraise));
        OnPropertyChanged(nameof(ToutiaoTemplateText));
        OnPropertyChanged(nameof(ToutiaoFilterText));
        OnPropertyChanged(nameof(ToutiaoCommodityJson));
        OnPropertyChanged(nameof(ToutiaoCompassJson));
        OnPropertyChanged(nameof(ToutiaoAttrsJson));
        OnPropertyChanged(nameof(ToutiaoEtDataJson));
        OnPropertyChanged(nameof(HasToutiaoAuthStatus));
        // 作品声明（A1）：预设整份替换 / 换条目后单选态与「跟随全局」预览要重建
        SyncToutiaoDeclarationSelection();
        // 视频生成图文（A2）：同上
        SyncToutiaoTransArticleSelection();
        OnPropertyChanged(nameof(ToutiaoComplianceCityText));
        OnPropertyChanged(nameof(ToutiaoComplianceDate));
        OnPropertyChanged(nameof(ToutiaoComplianceClock));
        // 互动贴纸（A3）：预设整份替换后开关/时间要跟着重建
        OnPropertyChanged(nameof(ToutiaoFollowSticker));
        OnPropertyChanged(nameof(ToutiaoStickerStartText));
        OnPropertyChanged(nameof(ToutiaoStickerDurationText));
        // 话题（2.6.3）：恢复预设 / 别处写了 TopicRefs（选活动带回话题）后芯片要跟着重建
        RefreshTopicChips();
        // 官方活动（2.6.4）：同上 —— 恢复预设 / params 导入后已选活动芯片要重建
        RefreshActivityChips();
        RefreshSummary();
    }
}

/// <summary>
/// 头条「作品声明」单选格（A1）。
/// <see cref="Value"/> = null（跟随全局）/ 0（不声明）/ ComplianceType（1/2/3/5/6/7/8）。
/// </summary>
public sealed partial class ToutiaoDeclarationOptionViewModel : ObservableObject
{
    public ToutiaoDeclarationOptionViewModel(int? value, string label)
    {
        Value = value;
        Label = label;
    }

    public int? Value { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 头条「视频生成图文」单选格（A2）。
/// <see cref="Value"/> = 0（不生成）/ 1（与视频同时发布）/ 2（仅保存草稿）。
/// <see cref="IsEnabled"/> = false 时是「暂不可用」的置灰项（=2，存草稿发布方式未做）。
/// </summary>
public sealed partial class ToutiaoTransArticleOptionViewModel : ObservableObject
{
    public ToutiaoTransArticleOptionViewModel(int value, string label, bool isEnabled = true)
    {
        Value = value;
        Label = label;
        IsEnabled = isEnabled;
    }

    public int Value { get; }
    public string Label { get; }
    public bool IsEnabled { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// B站「互动引导」样式单选格（B5，2026-11-15）。
/// <see cref="Value"/>：2 = 三连+关注 / 1 = 仅三连 / 0 = 仅关注
/// （3/4 是充电相关，仅充电权限账号可见，v1 不做）。
/// </summary>
public sealed partial class BilibiliGuideCardOptionViewModel : ObservableObject
{
    public BilibiliGuideCardOptionViewModel(int value, string label)
    {
        Value = value;
        Label = label;
    }

    public int Value { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 平台卡「话题」块里的一个已选芯片（2.6.3）。
/// 只读地包住 <see cref="TopicRef"/> —— 增删都走 VM 命令，芯片不自己改模型。
/// </summary>
public sealed class TopicChipViewModel
{
    public required TopicRef Topic { get; init; }

    public string DisplayName => "#" + Topic.Name;

    /// <summary>讨论量 / 播放量（平台原样文案或中文计数法；空 = 不占位）</summary>
    public string MetricText => Topic.Metric ?? "";
    public bool HasMetric => MetricText.Length > 0;

    /// <summary>带官方活动的话题（B站 mission_id &gt; 0）—— 芯片上加「有奖活动」小标</summary>
    public bool IsActivity => Topic.IsActivityTopic;

    /// <summary>没有平台话题 ID → 发布时按纯文本降级（芯片上加「仅文本」小标，提前说清）</summary>
    public bool IsPlainText => !Topic.HasPlatformId;

    public string ToolTipText => Topic.HasPlatformId
        ? $"平台话题 ID：{Topic.Id}"
        : "该平台没有给出话题 ID，发布时按纯文本处理";
}

/// <summary>
/// 平台话题候选行（搜索 / 推荐结果，2.6.3）—— 点「添加」写进目标的 <c>TopicRefs</c>。
/// </summary>
public sealed partial class TopicPickViewModel : ObservableObject
{
    public required TopicSuggestion Suggestion { get; init; }
    /// <summary>来自「获取平台推荐」还是关键词搜索（决定写进 TopicRef.Source）</summary>
    public required bool FromRecommend { get; init; }

    public string DisplayName => "#" + Suggestion.Name;

    public string MetricText => TopicSelection.MetricText(Suggestion) ?? "";
    public bool HasMetric => MetricText.Length > 0;
    public bool IsActivity => Suggestion.IsActivity;
    public bool IsPlainText => !Suggestion.HasPlatformId;

    /// <summary>已经在话题列表里（按钮换成「已添加」）</summary>
    [ObservableProperty] private bool _isSelected;

    public string ToolTipText => Suggestion.HasPlatformId
        ? $"平台话题 ID：{Suggestion.PlatformId}"
        : "该平台没有给出话题 ID，添加后按纯文本处理";
}

/// <summary>
/// 抖音热点词候选（2026-11-10）：一个热词 + 热度文案。
/// 来源 <see cref="HotspotItem"/>（平台 `hotspot/search`），选中即写 <c>common.hot_sentence</c>。
/// </summary>
public sealed class HotspotPickViewModel
{
    public required HotspotItem Item { get; init; }

    public string Word => Item.Word;
    public string HeatText => Item.HotValue > 0 ? $"{Item.HotValueDisplay} 热度" : "";
    public bool HasHeat => HeatText.Length > 0;
}

/// <summary>
/// 抖音背景音乐候选（2026-11-10）：一首歌 + 作者 + 时长。
/// 来源 <see cref="MusicItem"/>（平台 <c>music/list</c> 与 tsearch <c>music/search</c>），
/// 选中即写 <c>common.music_id</c>（<see cref="PlatformParams.MusicId"/>）。
/// </summary>
public sealed class MusicPickViewModel
{
    public required MusicItem Item { get; init; }

    public string Title => Item.Title;
    public string Author => Item.Author;
    public bool HasAuthor => Author.Length > 0;
    public string DurationText => Item.DurationDisplay;
    public bool HasDuration => DurationText.Length > 0;
}

/// <summary>
/// 抖音音乐分类页签（2026-11-10）：发布页音乐面板的二级页签（「推荐」「热歌榜」…）。
/// 来源 <see cref="DouyinMusicService.GetCategoriesAsync"/>，切换只写选中态并重拉列表。
/// </summary>
public sealed partial class MusicCategoryPickViewModel : ObservableObject
{
    public required MusicCategory Category { get; init; }

    /// <summary>页签显示名（平台没给名字时退回分类 ID，不留空白 chip）</summary>
    public string Name => string.IsNullOrWhiteSpace(Category.Name) ? Category.CategoryId : Category.Name;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 抖音「添加标签」类型 chip（B1 抓包 2026-11-11）：影视演艺 / 游戏手柄 / 标记万物。
/// 能力位（<c>anchor/list</c>）说不可用的类型禁用，并把平台给的 reason 挂在提示上。
/// </summary>
public sealed partial class AnchorTypePickViewModel : ObservableObject
{
    public required int Type { get; init; }
    public required string Name { get; init; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _reason = "";

    public bool HasReason => Reason.Length > 0;

    partial void OnReasonChanged(string value) => OnPropertyChanged(nameof(HasReason));
}

/// <summary>抖音「添加标签」候选：标题 + 副标题（影视类型 / 游戏标签 / SPU 分类）。</summary>
public sealed class AnchorPickViewModel
{
    public required DouyinAnchorCandidate Item { get; init; }

    public string Title => Item.Title;
    public string Subtitle => Item.Subtitle ?? "";
    public bool HasSubtitle => Subtitle.Length > 0;
}

/// <summary>
/// 小红书速览合集候选（2026-11-10）：合集名 + 集数。
/// 来源 <see cref="XhsSketchCollection"/>（平台 <c>collection/pc/list?type=1</c>），
/// 选中即写 <c>noteSketchCollectionBind.id</c>（<see cref="XhsExtras.SketchCollectionId"/>）。
/// </summary>
public sealed class SketchPickViewModel
{
    public required XhsSketchCollection Item { get; init; }

    /// <summary>合集名（平台没给名字时退回 ID，不留空白行）</summary>
    public string Name => string.IsNullOrWhiteSpace(Item.Name) ? Item.Id : Item.Name;

    /// <summary>集数文案（如「3/10 集」）；平台没给总集数时只显示已收录数</summary>
    public string EpisodesText => Item.TotalEpisodes > 0
        ? $"{Item.NoteNum}/{Item.TotalEpisodes} 集"
        : Item.NoteNum > 0 ? $"已收录 {Item.NoteNum}" : "";

    public bool HasEpisodes => EpisodesText.Length > 0;

    /// <summary>已满（页面同款：noteNum ≥ totalEpisodes）—— 选中按钮禁用</summary>
    public bool IsFull => Item.IsFull;
}

/// <summary>
/// 小红书 @用户候选 / 已选行（2026-11-10）：来源 <see cref="XhsMentionUser"/>
/// （平台 <c>search/user_info</c>）。选中写 <c>common.ats</c>（<see cref="XhsExtras.Ats"/>）。
/// </summary>
public sealed partial class XhsMentionPickViewModel : ObservableObject
{
    public required XhsMentionUser User { get; init; }

    /// <summary>是否已选 —— 候选行按钮文案「选 / 已选」与禁用态都看它</summary>
    [ObservableProperty] private bool _isPicked;

    public string Nickname => User.Nickname;

    /// <summary>副标题：小红书号 · 简介首段（只供辨认，不进 wire）</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(User.RedId)) parts.Add("小红书号 " + User.RedId);
            if (!string.IsNullOrWhiteSpace(User.Desc))
                parts.Add(User.Desc.ReplaceLineEndings(" ").Trim());
            return string.Join(" · ", parts);
        }
    }

    public bool HasSubtitle => Subtitle.Length > 0;

    public string PickLabel => IsPicked ? "已选" : "选";

    partial void OnIsPickedChanged(bool value) => OnPropertyChanged(nameof(PickLabel));
}

/// <summary>
/// 抖音 @好友候选 / 已选行（2026-11-11 补抓 discover/search）：来源
/// <see cref="DouyinMentionUser"/>。选中写 <see cref="DouyinExtras.MentionUsers"/>，
/// 发布体由 <see cref="Services.DouyinMentionRules"/> 拼成 mentions + 正文 @昵称 + text_extra。
/// </summary>
public sealed partial class DouyinMentionPickViewModel : ObservableObject
{
    public required DouyinMentionUser User { get; init; }

    /// <summary>是否已选 —— 候选行按钮文案「选 / 已选」与禁用态都看它</summary>
    [ObservableProperty] private bool _isPicked;

    public string Nickname => User.Nickname;

    /// <summary>副标题：抖音号 · 简介首段（只供辨认，不进 wire）</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(User.UniqueId)) parts.Add("抖音号 " + User.UniqueId);
            if (!string.IsNullOrWhiteSpace(User.Signature))
            {
                var sig = User.Signature.ReplaceLineEndings(" ").Trim();
                if (sig.Length > 30) sig = sig[..30] + "…";
                if (sig.Length > 0) parts.Add(sig);
            }
            return string.Join(" · ", parts);
        }
    }

    public bool HasSubtitle => Subtitle.Length > 0;

    public string PickLabel => IsPicked ? "已选" : "选";

    partial void OnIsPickedChanged(bool value) => OnPropertyChanged(nameof(PickLabel));
}

/// <summary>
/// 视频号 @好友候选 / 已选行（2026-11-11 B2：helper_search_finder_account）：来源
/// <see cref="WechatVideoMentionUser"/>。选中写 <see cref="WechatVideoExtras.MentionUsers"/>，
/// 发布体 = <c>mentionedUser:[{nickname}]</c> + 正文末尾「@昵称 」（平台只认昵称）。
/// </summary>
public sealed partial class WechatMentionPickViewModel : ObservableObject
{
    public required WechatVideoMentionUser User { get; init; }

    /// <summary>是否已选 —— 候选行按钮文案「选 / 已选」与禁用态都看它</summary>
    [ObservableProperty] private bool _isPicked;

    public string Nickname => User.Nickname;

    /// <summary>副标题：认证公司名（只供同名账号辨认，不进 wire）</summary>
    public string Subtitle => User.AuthCompanyName ?? "";

    public bool HasSubtitle => Subtitle.Length > 0;

    public string PickLabel => IsPicked ? "已选" : "选";

    partial void OnIsPickedChanged(bool value) => OnPropertyChanged(nameof(PickLabel));
}

/// <summary>
/// 快手 @好友候选 / 已选行（B3 抓包 2026-11-11：at/search）：来源
/// <see cref="KuaishouMentionUser"/>。选中写 <see cref="KuaishouExtras.MentionUsers"/>，
/// 发布执行端把它换成 caption 里的 <c>at-user-item</c> HTML 片段（并对每人先调 at/choose）。
/// </summary>
public sealed partial class KsMentionPickViewModel : ObservableObject
{
    public required KuaishouMentionUser User { get; init; }

    /// <summary>是否已选 —— 候选行按钮文案「选 / 已选」与禁用态都看它</summary>
    [ObservableProperty] private bool _isPicked;

    public long UserId => User.UserId;

    public string UserName => User.UserName;

    /// <summary>副标题：粉丝数（仅展示，不进 wire）</summary>
    public string Subtitle => User.FansCount > 0
        ? User.FansCount.ToString("N0") + " 粉丝"
        : "";

    public bool HasSubtitle => Subtitle.Length > 0;

    public string PickLabel => IsPicked ? "已选" : "选";

    partial void OnIsPickedChanged(bool value) => OnPropertyChanged(nameof(PickLabel));
}

/// <summary>
/// 小红书 PK 封面附加图行（2026-11-15）：一个本地图片路径。
/// 发布时逐张上传，组进 <c>video_info.pk_cover_biz_relations</c>。
/// </summary>
/// <summary>小红书添加路线的 POI 候选（2026-11-15）：平台 <c>query/poi/by/keyword</c>。</summary>
public sealed class XhsRoutePickViewModel
{
    public required XhsRoutePoi Item { get; init; }

    public string Name => Item.Name;

    public string Subtitle => Item.Address;

    public bool HasSubtitle => Subtitle.Length > 0;
}

/// <summary>小红书添加路线已选地点（2026-11-15）：保存路线前的草稿行。</summary>
public sealed class XhsRoutePointViewModel
{
    public required XhsRoutePoint Point { get; init; }

    public string Name => Point.Name;

    public string CoordinateText => $"{Point.Lon:0.#####},{Point.Lat:0.#####}";
}

public sealed class XhsPkCoverItemViewModel
{
    public required string Path { get; init; }

    public string Name => System.IO.Path.GetFileName(Path);

    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>
/// 小红书视频标记候选行（2026-11-15）：一个可标记的朋友 / 地点。
/// 选中写 <see cref="XhsExtras.Markers"/>，发布体落 <c>video_info.markers</c>。
/// </summary>
public sealed class XhsMarkerPickViewModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary><c>user</c> / <c>location</c>（页面 bundle 的小写枚举）</summary>
    public required string Type { get; init; }
    public string? Subtitle { get; init; }
    public string? Avatar { get; init; }

    public string TypeText => Type == "location" ? "地点" : "朋友";
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
}

/// <summary>
/// 小红书已选视频标记行（2026-11-15）：起止秒可编辑，改即回写模型。
/// 发布体 wire 由 <see cref="XhsPublishService.BuildMarkers"/> 组装。
/// </summary>
public sealed partial class XhsMarkerRowViewModel : ObservableObject
{
    private readonly Action<XhsMarkerRowViewModel> _changed;

    public XhsMarkerRowViewModel(XhsVideoMarker marker, Action<XhsMarkerRowViewModel> changed)
    {
        Marker = marker;
        _changed = changed;
    }

    public XhsVideoMarker Marker { get; }

    public string Name => Marker.Name;

    public string TypeText => Marker.Type == "location" ? "地点" : "朋友";

    [ObservableProperty] private string _startText = "";
    [ObservableProperty] private string _endText = "";

    public long StartSeconds => long.TryParse(StartText, out var s) ? Math.Max(0, s) : 0;

    public long EndSeconds =>
        long.TryParse(EndText, out var e) ? Math.Max(StartSeconds, e) : StartSeconds;

    public string DurationText => $"{StartSeconds}–{EndSeconds} 秒";

    partial void OnStartTextChanged(string value)
    {
        _changed(this);
        OnPropertyChanged(nameof(DurationText));
    }

    partial void OnEndTextChanged(string value)
    {
        _changed(this);
        OnPropertyChanged(nameof(DurationText));
    }
}

/// <summary>
/// 小红书群聊候选行（2026-11-15）：来源 <see cref="XhsGroupItem"/>（平台 <c>group_list</c>）。
/// 选中写 <see cref="XhsExtras.GroupBind"/>，发布体落 <c>business_binds.groupBind</c>。
/// </summary>
public sealed class XhsGroupPickViewModel
{
    public required XhsGroupItem Item { get; init; }

    public string Name => Item.GroupName;

    /// <summary>副标题：群描述（平台没给就空）</summary>
    public string Subtitle => Item.Desc ?? "";

    public bool HasSubtitle => Subtitle.Length > 0;

    /// <summary>平台标记不可绑定（页面同款「该群状态异常，暂不支持绑定」）</summary>
    public bool IsLinkable => Item.Linkable;

    public string PickLabel => IsLinkable ? "选" : "不可选";
}

/// <summary>
/// 小红书引用笔记候选 / 已选行（A5，2026-11-11）：来源 <see cref="XhsNoteItem"/>
/// （平台 <c>note/user/posted</c>）。选中写 <see cref="XhsExtras.ReferencedNote"/>，
/// 发布体由 <c>XhsRelationBinds</c> 拼成 <c>REF_POST</c>。
/// </summary>
public sealed partial class XhsNotePickViewModel : ObservableObject
{
    public required XhsNoteItem Item { get; init; }

    /// <summary>是否已选 —— 候选行按钮文案「选 / 已选」与禁用态都看它</summary>
    [ObservableProperty] private bool _isPicked;

    /// <summary>显示名（平台没给标题时退回 ID，不留空白行）</summary>
    public string Name => string.IsNullOrWhiteSpace(Item.Title) ? Item.Id : Item.Title;

    /// <summary>副标题：类型 · 发布时间（平台原样字符串，只供辨认，不进 wire）</summary>
    public string Subtitle => string.Join(" · ",
        new[] { TypeText, Item.TimeText }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public bool HasSubtitle => Subtitle.Length > 0;

    private string? TypeText => Item.Type switch
    {
        "video" => "视频",
        "normal" => "图文",
        null => null,
        _ => Item.Type,
    };

    public string PickLabel => IsPicked ? "已选" : "选";

    partial void OnIsPickedChanged(bool value) => OnPropertyChanged(nameof(PickLabel));
}

/// <summary>
/// 小红书投票选项行（A4，2026-11-11）：一个输入框 = 一条选项，文字变化即回写
/// <see cref="XhsExtras.PollOptions"/>。只是 UI 行，无网络、无平台 ID ——
/// 选项顺序就是发布顺序（create_interact 的 <c>option_id</c> 从 1 起连续编号）。
/// </summary>
public sealed partial class XhsPollOptionViewModel : ObservableObject
{
    private readonly Action _edited;

    public XhsPollOptionViewModel(Action edited)
    {
        _edited = edited;
    }

    [ObservableProperty] private string _text = "";

    partial void OnTextChanged(string value) => _edited();
}

/// <summary>
/// B站分区候选（2026-11-10）：<c>human_type2</c> 的一个领域。
/// 来源 <see cref="BilibiliTypeOption"/>（平台 <c>archive/human/type2/list</c>），
/// 选中即写 <c>add_v3.human_type2</c>（<see cref="BilibiliExtras.HumanType2"/>）。
/// </summary>
public sealed class BiliTypePickViewModel
{
    public required BilibiliTypeOption Type { get; init; }

    public string Name => Type.Name;
    public int Id => Type.Id;
}

/// <summary>
/// 快手专属领域候选（2026-11-10）：<c>upload/domain/list</c> 的 <c>firstDomain</c>。
/// 选中即写 <c>submit.domain</c>（<see cref="KuaishouExtras.Domain"/>），并摊开其二级领域。
/// </summary>
public sealed class KsDomainPickViewModel
{
    public required KuaishouDomain Domain { get; init; }

    public string Name => Domain.Name;
    public bool HasSecond => Domain.SecondCount > 0;
    public string SecondCountText => HasSecond ? $"二级 {Domain.SecondCount} 个" : "无二级";
}

/// <summary>快手二级领域候选（<c>upload/domain/list</c> 的 <c>secondDomain[]</c> 元素）。</summary>
public sealed class KsSecondDomainPickViewModel
{
    public required string Name { get; init; }
}

/// <summary>
/// 快手「关联热点」候选（B3 抓包 2026-11-11）：热点文案 + 热度。
/// 来源 <see cref="KuaishouHotspotResult"/>（平台 <c>hotspot/list</c>），
/// 选中即写 <c>submit.hotspotId</c>（<see cref="KuaishouExtras.HotspotId"/>）。
/// </summary>
public sealed class KsHotspotPickViewModel
{
    public required KuaishouHotspotResult Item { get; init; }

    public string Content => Item.Content;

    /// <summary>热度文案：「1175万 热度 · 1 个视频」（平台给什么用什么，缺项不占位）</summary>
    public string HeatText
    {
        get
        {
            var parts = new List<string>();
            if (Item.HotValue.Length > 0) parts.Add($"{Item.HotValue} 热度");
            if (Item.FeedCount > 0) parts.Add($"{Item.FeedCount} 个视频");
            return string.Join(" · ", parts);
        }
    }

    public bool HasHeat => HeatText.Length > 0;
    public bool Rising => Item.Rising;
}

/// <summary>
/// 快手「作者服务 · 关联小程序」候选（2026-11-15 真页面抓包）：
/// 来源 <see cref="KuaishouRelationItem"/>（平台 <c>relation/list</c>），
/// 选中即写整个 <c>associateTaskId</c>（<see cref="KuaishouExtras.AssociateTask"/>）。
/// </summary>
public sealed class KsRelationPickViewModel
{
    public required KuaishouRelationItem Item { get; init; }

    public string Title => Item.Title;

    /// <summary>平台给的小程序描述（可空，缺项不占位）。</summary>
    public string Desc => Item.Desc ?? "";
    public bool HasDesc => Desc.Length > 0;

    /// <summary>平台校验提示（未观测到非空值；有就亮出来，不编解释）。</summary>
    public string ValidText => Item.ValidMessage ?? "";
    public bool HasValidMessage => ValidText.Length > 0;
}

/// <summary>
/// 快手「共同创作」已选的一行（2026-11-15 发布页 bundle 定案）。
/// 角色下标 0~3 对应 wire <c>coPublishRole</c> -1 默认 / 0 作者 / 1 共创 / 2 出镜。
/// 角色改动通过 <see cref="RoleChanged"/> 让行 VM 回写 <see cref="KuaishouExtras.CoPublishUsers"/>。
/// </summary>
public sealed partial class KsCoPublishPickViewModel : ObservableObject
{
    public required long UserId { get; init; }
    public required string UserName { get; init; }
    public string? UserHead { get; init; }

    [ObservableProperty] private int _roleIndex;

    /// <summary>下拉选项（下标与 <see cref="RoleIndex"/> 对应）。</summary>
    public IReadOnlyList<string> RoleOptions => KuaishouCoPublishRef.RoleNames;

    public string RoleText => RoleOptions[Math.Clamp(RoleIndex, 0, RoleOptions.Count - 1)];

    /// <summary>wire 角色值（-1 / 0 / 1 / 2）。</summary>
    public int Role => RoleIndex switch
    {
        1 => 0,
        2 => 1,
        3 => 2,
        _ => -1,
    };

    /// <summary>角色变了 —— 行 VM 回写 Params。</summary>
    public event Action<KsCoPublishPickViewModel>? RoleChanged;

    partial void OnRoleIndexChanged(int value)
    {
        OnPropertyChanged(nameof(RoleText));
        RoleChanged?.Invoke(this);
    }
}

/// <summary>
/// 平台卡「官方活动」块里的一个已选活动芯片（2.6.4）。
/// 只读地包住 <see cref="ActivityRef"/> —— 清除走 VM 命令，芯片不自己改模型。
/// </summary>
public sealed class ActivityChipViewModel
{
    public required ActivityRef Activity { get; init; }

    /// <summary>活动名（旧档迁移来的只有 ID —— 直接显示 ID，不留空白）</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Activity.Name)
        ? Activity.Id
        : Activity.Name;

    /// <summary>奖励文案（快手 topGain / 抖音「有奖活动」/ 小红书活动奖励）</summary>
    public string RewardText => Activity.Reward ?? "";
    public bool HasReward => RewardText.Length > 0;

    /// <summary>截止文案（「12-31 23:59 截止」/「已结束」/ 空 = 平台没给）</summary>
    public string DeadlineText =>
        TopicSelection.ActivityDeadlineText(Activity.EndTime, DateTimeOffset.Now) ?? "";
    public bool HasDeadline => DeadlineText.Length > 0;

    public bool IsEnded => Activity.IsEnded;

    /// <summary>「自动带 N 个话题」—— 让用户知道话题芯片为什么突然多了</summary>
    public string TopicCountText => Activity.Topics.Count > 0
        ? $"自动带 {Activity.Topics.Count} 个话题"
        : "";
    public bool HasTopics => TopicCountText.Length > 0;

    public string ToolTipText => $"活动 ID：{Activity.Id}";
}

/// <summary>
/// 官方活动候选卡（2.6.4）—— 封面 / 名称 / 奖励 / 截止 / 热度，单选。
/// 已结束的活动由 <see cref="PlatformActivity.IsEnded"/> 判定：灰显、不可点。
/// </summary>
public sealed partial class ActivityCardViewModel : ObservableObject
{
    public required PlatformActivity Activity { get; init; }

    public string Name => string.IsNullOrWhiteSpace(Activity.Name) ? Activity.Id : Activity.Name;

    public bool HasCoverUrl => !string.IsNullOrWhiteSpace(Activity.CoverUrl);

    /// <summary>远程封面（后台加载；null = 还没加载好/加载失败 → 显示首字占位）</summary>
    [ObservableProperty] private Bitmap? _cover;
    public bool HasCover => Cover is not null;

    partial void OnCoverChanged(Bitmap? value) => OnPropertyChanged(nameof(HasCover));

    /// <summary>首字占位（封面未加载/失败时显示）</summary>
    public string Initial => Name.Length > 0 ? Name[..1] : "活";

    /// <summary>已结束：灰显（<see cref="CardOpacity"/>）且不能选</summary>
    public bool IsEnded => Activity.IsEnded;
    public bool CanSelect => !IsEnded;
    public double CardOpacity => IsEnded ? 0.55 : 1;

    public bool HasReward => !string.IsNullOrWhiteSpace(Activity.Reward);
    public string RewardText => Activity.Reward ?? "";

    /// <summary>平台自定义标签（快手 customTag，如「官方」「热点」）</summary>
    public bool HasCustomTag => !string.IsNullOrWhiteSpace(Activity.CustomTag);
    public string CustomTagText => Activity.CustomTag ?? "";

    public string DeadlineText =>
        TopicSelection.ActivityDeadlineText(Activity.EndTime, DateTimeOffset.Now) ?? "";
    public bool HasDeadline => DeadlineText.Length > 0;

    public string MetricText => TopicSelection.ActivityMetricText(Activity) ?? "";
    public bool HasMetric => MetricText.Length > 0;

    public string TopicCountText => Activity.Topics.Count > 0
        ? $"带 {Activity.Topics.Count} 个话题"
        : "";
    public bool HasTopics => TopicCountText.Length > 0;

    /// <summary>已经在已选列表里（按钮换成「已选择」）</summary>
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) =>
        OnPropertyChanged(nameof(ShowSelectButton));

    /// <summary>「选择」按钮的可见性（未选中 + 未结束）—— 用属性而不是 XAML 里的 &amp;&amp;：
    /// 绑定表达式里单个 &amp; 的解析行为各版本不一致，组合态放 VM 最稳</summary>
    public bool ShowSelectButton => CanSelect && !IsSelected;

    public string ToolTipText => $"活动 ID：{Activity.Id}";
}

/// <summary>
/// 全局「官方活动」区的一行 = 一个平台（2.6.9，2026-11-06 用户要求「活动合起来」）。
///
/// 活动值仍落在各账号的 <see cref="PlatformParams.Activities"/> 上（平台侧对象，各平台独立）；
/// 这一行只是把同平台账号的界面合并成一处：芯片 = 各账号活动的并集，
/// 选择 / 清除对该平台**所有账号**一起生效。
/// </summary>
public sealed partial class PlatformActivityRowViewModel : ObservableObject
{
    public required string PlatformKey { get; init; }
    public required string PlatformName { get; init; }
    public required string BrandHex { get; init; }

    public string Initial => PlatformName.Length > 0 ? PlatformName[..1] : "?";

    /// <summary>该平台下的账号行（选择 / 清除作用到全部；拉取用第一个可用账号的凭证）</summary>
    public List<TargetRowViewModel> Targets { get; } = [];

    public string AccountScopeText => Targets.Count > 1
        ? $"该平台 {Targets.Count} 个账号一起生效"
        : "";

    public bool HasAccountScope => AccountScopeText.Length > 0;

    /// <summary>已选活动（各账号活动按 ID 并集；params.txt 可能导入过多个）</summary>
    public ObservableCollection<ActivityChipViewModel> ActivityChips { get; } = [];
    public bool HasActivityChips => ActivityChips.Count > 0;

    /// <summary>拉取回来的活动候选（会话级，不落盘）</summary>
    public ObservableCollection<ActivityCardViewModel> ActivityOptions { get; } = [];
    public bool HasActivityOptions => ActivityOptions.Count > 0;

    [ObservableProperty] private bool _isActivityPanelOpen;
    [ObservableProperty] private string _activityStatusText = "";
    [ObservableProperty] private bool _activityStatusIsError;
    [ObservableProperty] private bool _isActivityBusy;

    /// <summary>活动块的同款记账（2.6.7）：同键再点一次 = 强制刷新。</summary>
    public string? CachedActivityKey { get; set; }

    public string ActivityPanelButtonText => IsActivityPanelOpen ? "收起" : "选择活动";

    /// <summary>空态提示只在面板展开时出现（折叠行不为「还没选活动」占行）</summary>
    public bool ShowActivityEmptyHint => !HasActivityChips && IsActivityPanelOpen;

    public bool HasActivityStatus => ActivityStatusText.Length > 0;
    public string ActivityStatusColorHex => ActivityStatusIsError ? "#CB2634" : "#86909C";

    partial void OnIsActivityPanelOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ActivityPanelButtonText));
        OnPropertyChanged(nameof(ShowActivityEmptyHint));
    }

    partial void OnActivityStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(HasActivityStatus));

    partial void OnActivityStatusIsErrorChanged(bool value) =>
        OnPropertyChanged(nameof(ActivityStatusColorHex));

    /// <summary>按各账号 <c>Params.Activities</c> 重建已选芯片（按 ID 去重）</summary>
    public void RefreshActivityChips()
    {
        ActivityChips.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in Targets)
        foreach (var a in target.Params.Activities)
        {
            if (!seen.Add(a.Id)) continue;
            ActivityChips.Add(new ActivityChipViewModel { Activity = a });
        }
        OnPropertyChanged(nameof(HasActivityChips));
        OnPropertyChanged(nameof(ShowActivityEmptyHint));
        RefreshActivityPickStates();
    }

    /// <summary>
    /// 装上一批活动候选（按 ID 去重；已结束的排在最后 —— 用户要找的是还能参加的活动）。
    /// 不碰网络：封面加载由命令在后台单独发起（冒烟造景因此零网络）。
    /// </summary>
    public void SetActivityOptions(IEnumerable<PlatformActivity> items)
    {
        ActivityOptions.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = items
            .Where(a => !string.IsNullOrWhiteSpace(a.Id) && seen.Add(a.Id))
            .OrderBy(a => a.IsEnded)   // false(进行中) 在前
            .ToList();
        foreach (var a in ordered)
            ActivityOptions.Add(new ActivityCardViewModel { Activity = a });
        OnPropertyChanged(nameof(HasActivityOptions));
        RefreshActivityPickStates();
    }

    /// <summary>候选卡上的「选择 / 已选择」跟着已选活动走（按 ID 匹配，活动名可能被平台改过）</summary>
    public void RefreshActivityPickStates()
    {
        foreach (var card in ActivityOptions)
            card.IsSelected = Targets.Any(t => t.Params.Activities.Any(a => a.Id == card.Activity.Id));
    }

    /// <summary>
    /// 后台加载候选封面（封面失败只显示首字占位，**永不抛**）。
    /// 命令在拿到活动列表后 fire-and-forget 调用；冒烟造景不带封面 URL，不触网。
    /// </summary>
    public async Task LoadActivityCoversAsync()
    {
        var referer = Services.ActivityCoverLoader.RefererFor(PlatformKey);
        var pending = ActivityOptions
            .Where(c => c.HasCoverUrl)
            .Select(async card =>
            {
                try
                {
                    card.Cover = await Services.ActivityCoverLoader.LoadAsync(
                        card.Activity.CoverUrl, referer);
                }
                catch (Exception ex)
                {
                    // 加载器本身不抛；这里只兜「线程被取消」之类的意外，绝不让封面拖垮列表
                    Log.Debug(ex, "[Publish] 活动封面加载异常");
                }
            })
            .ToList();
        if (pending.Count > 0) await Task.WhenAll(pending);
    }
}

/// <summary>内容声明的单选项（**互斥单选** —— 设计文档 §4.3 硬规则一）</summary>
public sealed partial class DeclarationOptionViewModel : ObservableObject
{
    public required DeclarationKind Kind { get; init; }
    public required string Label { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>平台不支持该语义时的原因（null = 全支持）</summary>
    [ObservableProperty]
    private string? _unsupportedHint;

    public bool HasHint => !string.IsNullOrEmpty(UnsupportedHint);
}

/// <summary>统一可见性的单选项</summary>
public sealed partial class VisibilityOptionViewModel : ObservableObject
{
    public required PublishVisibility Kind { get; init; }
    public required string Label { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>该态需要用户白名单（只给谁看 / 不给谁看）</summary>
    public bool NeedsUserList => Kind is PublishVisibility.OnlyUsers or PublishVisibility.ExcludeUsers;
}

/// <summary>合集下拉项 —— 一个**逻辑合集**（不是平台侧合集 ID）。</summary>
public sealed partial class CollectionOptionViewModel : ObservableObject
{
    public required Guid CollectionId { get; init; }
    public required string Name { get; init; }
    /// <summary>已同步的平台数 / 总平台数（「3/4 已同步」）</summary>
    public required int SyncedCount { get; init; }
    public required int TotalCount { get; init; }

    /// <summary>合集类型：0=视频 1=文章（文章合集只用于图文发布，视频发布页不显示）</summary>
    public int ContentType { get; init; }
    public bool IsArticle => ContentType == 1;

    [ObservableProperty]
    private bool _isSelected;

    public string SyncText => $"{SyncedCount}/{TotalCount} 已同步";
}

/// <summary>
/// 合集映射行 —— 编辑器里每个目标一行，说明该账号有没有同步过这个逻辑合集。
///
/// 顶层类而非嵌套类：XAML 的 <c>x:DataType</c> 解析嵌套类型要写 <c>Outer+Inner</c>，
/// 编译器与设计器行为不一致，容易踩坑。
/// </summary>
public sealed class CollectionMappingRow
{
    public required string PlatformName { get; init; }
    public required string AccountLabel { get; init; }
    public required string BrandHex { get; init; }
    public required string ValueText { get; init; }
    public required string StatusText { get; init; }
    public required string StatusColorHex { get; init; }
    /// <summary>该账号从未同步过这个合集（要显式提示，不能读成「这平台没合集」）</summary>
    public bool IsMissing { get; init; }
    public string Initial => PlatformName.Length > 0 ? PlatformName[..1] : "?";
}

/// <summary>账号异常横幅（队列页 / 工作台共用）</summary>
public sealed partial class AccountAlertViewModel : ObservableObject
{
    public required Guid SessionId { get; init; }
    public required string PlatformKey { get; init; }
    public required string Text { get; init; }
}
