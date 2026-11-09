using Avalonia.Media.Imaging;
using CreatFlow.Platforms.Topics;

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
    /// params.txt 的「B站分区 / B站二级分区」可覆盖 —— UI 只读展示，不做分区树下拉。
    /// </summary>
    public string BilibiliPartitionText
        => $"{Params.Bilibili?.Tid ?? 21} / {Params.Bilibili?.HumanType2 ?? 1015}（家装房产）";

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
    //  只有抖音 / 快手 / 小红书 / B站 出现（能力来自 TopicCapability.SupportsActivities，
    //  头条/视频号没有活动接口 → 整块不渲染，不误导用户）。
    //  列表是**单选**：点一张卡 = 替换 <see cref="Params"/> 里的活动列表（1 个）；
    //  已结束的活动灰显不可选；选中经 UpsertActivity 自动带活动关联话题（2.6.4 核心）。

    /// <summary>该平台有没有官方活动接口（头条 / 视频号 = false，整块隐藏）</summary>
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
        set { if (Params.HotspotSentence == value) return; Params.HotspotSentence = value; OnPropertyChanged(); }
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
            OnPropertyChanged();
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

    /// <summary>@ 用户（原始 JSON 数组串，高级透传）</summary>
    public string MentionsJson
    {
        get => Params.Douyin?.Mentions ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Douyin?.Mentions ?? "") == v) return;
            DouyinGroup.Mentions = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>互动贴纸（原始 JSON 数组串，高级透传）</summary>
    public string InteractionStickersJson
    {
        get => Params.Douyin?.InteractionStickers ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Douyin?.InteractionStickers ?? "") == v) return;
            DouyinGroup.InteractionStickers = v.Length == 0 ? null : v;
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

    /// <summary>章节（原始 JSON 数组串 —— 元素结构未抓到，先透传）</summary>
    public string KuaishouChaptersJson
    {
        get => Params.Kuaishou?.ChaptersJson ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Kuaishou?.ChaptersJson ?? "") == v) return;
            KuaishouGroup.ChaptersJson = v.Length == 0 ? null : v;
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

    /// <summary>专属领域</summary>
    public string DomainText
    {
        get => Params.Kuaishou?.Domain ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Kuaishou?.Domain ?? "") == v) return;
            KuaishouGroup.Domain = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>二级领域</summary>
    public string SecondDomainText
    {
        get => Params.Kuaishou?.SecondDomain ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Kuaishou?.SecondDomain ?? "") == v) return;
            KuaishouGroup.SecondDomain = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
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

    /// <summary>共创角色（<c>coPublishRole</c>）</summary>
    public string CoPublishRoleText
    {
        get => PublishFieldText.FormatInt(Params.Kuaishou?.CoPublishRole);
        set
        {
            var v = PublishFieldText.ParseInt(value);
            if (Params.Kuaishou?.CoPublishRole == v) return;
            KuaishouGroup.CoPublishRole = v;
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
            OnPropertyChanged();
        }
    }

    /// <summary>订单/品牌绑定（原始 JSON 对象串）—— <c>noteOrderBind</c></summary>
    public string XhsOrderBindJson
    {
        get => Params.Xhs?.OrderBind ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Xhs?.OrderBind ?? "") == v) return;
            XhsGroup.OrderBind = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>带货商品信息（原始 JSON 对象串）—— <c>goods_info</c>（元素结构未抓到）</summary>
    public string XhsGoodsInfoJson
    {
        get => Params.Xhs?.GoodsInfo ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Xhs?.GoodsInfo ?? "") == v) return;
            XhsGroup.GoodsInfo = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>@ 用户（原始 JSON 数组串）—— <c>common.ats</c></summary>
    public string XhsAtsJson
    {
        get => Params.Xhs?.Ats ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Xhs?.Ats ?? "") == v) return;
            XhsGroup.Ats = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>业务关系（原始 JSON 数组串）—— <c>common.biz_relations</c></summary>
    public string XhsBizRelationsJson
    {
        get => Params.Xhs?.BizRelations ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Xhs?.BizRelations ?? "") == v) return;
            XhsGroup.BizRelations = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
    }

    /// <summary>PK 封面业务关联（原始 JSON 数组串）—— <c>pk_cover_biz_relations</c></summary>
    public string XhsPkCoverBizRelationsJson
    {
        get => Params.Xhs?.PkCoverBizRelations ?? "";
        set
        {
            var v = value.Trim();
            if ((Params.Xhs?.PkCoverBizRelations ?? "") == v) return;
            XhsGroup.PkCoverBizRelations = v.Length == 0 ? null : v;
            OnPropertyChanged();
        }
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

    /// <summary>字幕语言 —— <c>subtitle.lan</c></summary>
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

    // ═══ 头条长尾（阶段 5.6）═══
    //
    //  赞赏 / 原创 / 合集是公共概念：原创走全局开关、合集走全局下拉；
    //  这里只放头条专有的长尾（模板 / 滤镜 / 商品 / 懂车帝 / 长尾属性 / 埋点）。

    public bool IsToutiao => Target.PlatformKey == PlatformNames.Toutiao;

    private ToutiaoExtras ToutiaoGroup => Params.Toutiao ??= new ToutiaoExtras();

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

    /// <summary>模板名 —— <c>TemplateName</c></summary>
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

    /// <summary>滤镜名 —— <c>FilterName</c></summary>
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
        if (Params.Location is { } poi) parts.Add(poi.Name);
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
        a?.CoverCropWidth == b?.CoverCropWidth
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
        && a?.ChaptersJson == b?.ChaptersJson
        && a?.CoPublishUsersJson == b?.CoPublishUsersJson
        && a?.CoPublishRole == b?.CoPublishRole;

    private static bool SameXhsExtras(XhsExtras? a, XhsExtras? b) =>
        a?.Ats == b?.Ats
        && a?.GoodsInfo == b?.GoodsInfo
        && a?.BizRelations == b?.BizRelations
        && a?.SketchCollectionId == b?.SketchCollectionId
        && a?.OrderBind == b?.OrderBind
        && a?.ChapterSyncText == b?.ChapterSyncText
        && a?.PkCoverBizRelations == b?.PkCoverBizRelations;

    private static bool SameWechatVideoExtras(WechatVideoExtras? a, WechatVideoExtras? b) =>
        a?.CardShowStyle == b?.CardShowStyle
        && a?.ExtReadingTitle == b?.ExtReadingTitle
        && a?.ExtReadingType == b?.ExtReadingType;

    private static bool SameBilibiliExtras(BilibiliExtras? a, BilibiliExtras? b) =>
        a?.Tid == b?.Tid
        && a?.HumanType2 == b?.HumanType2
        && a?.NoReprint == b?.NoReprint
        && a?.Dynamic == b?.Dynamic
        && a?.Watermark == b?.Watermark
        && a?.SubtitleOpen == b?.SubtitleOpen
        && a?.SubtitleLan == b?.SubtitleLan
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
        && a?.EtData == b?.EtData;

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
        OnPropertyChanged(nameof(ShowInNearby));
        OnPropertyChanged(nameof(AllowSameFrame));
        OnPropertyChanged(nameof(IsOriginal));
        OnPropertyChanged(nameof(HotspotSentence));
        OnPropertyChanged(nameof(IsRepostDisabledByOriginal));
        OnPropertyChanged(nameof(MusicIdText));
        OnPropertyChanged(nameof(ChaptersText));
        OnPropertyChanged(nameof(SyncToToutiao));
        OnPropertyChanged(nameof(MentionsJson));
        OnPropertyChanged(nameof(InteractionStickersJson));
        OnPropertyChanged(nameof(CoverCropWidthText));
        OnPropertyChanged(nameof(CoverCropHeightText));
        OnPropertyChanged(nameof(CollectionOrderText));
        OnPropertyChanged(nameof(HasCollection));
        OnPropertyChanged(nameof(BilibiliPartitionText));
        // 快手长尾（2.2）
        OnPropertyChanged(nameof(NotifyFollowers));
        OnPropertyChanged(nameof(KuaishouCoverTitleText));
        OnPropertyChanged(nameof(KuaishouChaptersJson));
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
        OnPropertyChanged(nameof(KuaishouMovieIdText));
        OnPropertyChanged(nameof(KuaishouProjectIdText));
        OnPropertyChanged(nameof(CoPublishUsersJson));
        OnPropertyChanged(nameof(CoPublishRoleText));
        // 小红书长尾（2.3）
        OnPropertyChanged(nameof(AllowComment));
        OnPropertyChanged(nameof(XhsSummaryText));
        OnPropertyChanged(nameof(ChapterSyncText));
        OnPropertyChanged(nameof(XhsSketchCollectionIdText));
        OnPropertyChanged(nameof(XhsOrderBindJson));
        OnPropertyChanged(nameof(XhsGoodsInfoJson));
        OnPropertyChanged(nameof(XhsAtsJson));
        OnPropertyChanged(nameof(XhsBizRelationsJson));
        OnPropertyChanged(nameof(XhsPkCoverBizRelationsJson));
        // 视频号长尾（2.4）
        OnPropertyChanged(nameof(WechatShortTitleText));
        OnPropertyChanged(nameof(WechatExternalLinkText));
        OnPropertyChanged(nameof(WechatExtReadingTitleText));
        OnPropertyChanged(nameof(WechatCardShowStyleIndex));
        // B站长尾（阶段 4.6）
        OnPropertyChanged(nameof(BilibiliTagsText));
        OnPropertyChanged(nameof(BilibiliDynamicText));
        OnPropertyChanged(nameof(BilibiliWatermark));
        OnPropertyChanged(nameof(BilibiliSubtitleOpen));
        OnPropertyChanged(nameof(BilibiliSubtitleLanText));
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
        // 话题（2.6.3）：恢复预设 / 别处写了 TopicRefs（选活动带回话题）后芯片要跟着重建
        RefreshTopicChips();
        // 官方活动（2.6.4）：同上 —— 恢复预设 / params 导入后已选活动芯片要重建
        RefreshActivityChips();
        RefreshSummary();
    }
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
