using CreatFlow.NewApp.Models;
using CreatFlow.NewApp.Services;

namespace CreatFlow.NewApp.ViewModels;

/// <summary>
/// 平台元信息（列表卡上色/中文名；来自 /api/config/platforms）。
/// 未知平台 key 兜底：中文名走 PlatformNames.GetDisplayName、品牌灰 —— 配置缺失不让页面崩。
/// </summary>
public sealed record PlatformLookup(string DisplayName, string BrandColorHex)
{
    public static readonly PlatformLookup Fallback = new("", "#86909C");
}

/// <summary>
/// 账号级状态行类别 —— 合集状态按「平台 × 账号」判定：
///   Synced     有同步记录且已拿到平台合集 ID（平台侧创建成功）
///   Incomplete 有记录但尚无平台 ID（Pending 建档后同步失败/未完成 —— 可点卡片重试）
///   Missing    平台已绑定该账号但本合集从未给它建过记录（编辑补建）
/// </summary>
public enum AccountStateKind { Synced, Incomplete, Missing }

/// <summary>平台组内的一条账号状态：账号名 + 状态（展示限定当前绑定账号）。</summary>
public sealed record CollectionAccountRow(
    string PlatformKey,
    string PlatformName,       // 平台中文名
    string AccountName,        // 账号备注/昵称（仅绑定账号参与展示）
    AccountStateKind Kind)
{
    public string StateText => Kind switch
    {
        AccountStateKind.Synced => "已同步",
        AccountStateKind.Incomplete => "未完成",
        _ => "未同步"
    };
    /// <summary>状态文字色（状态胶囊内文字；Missing 用品牌紫表达「待补建的动作」）</summary>
    public string StateTextColor => Kind switch
    {
        AccountStateKind.Synced => "#0A5C28",
        AccountStateKind.Incomplete => "#B45F06",
        _ => "#4B3BC3"
    };
    /// <summary>状态胶囊底色（绿=已同步 / 橙=未完成 / 淡紫=未同步·可补建）</summary>
    public string StateBadgeBg => Kind switch
    {
        AccountStateKind.Synced => "#EBF7EE",
        AccountStateKind.Incomplete => "#FFF7E6",
        _ => "#F4F1FE"
    };
    public string StateDotHex => Kind switch
    {
        AccountStateKind.Synced => "#00B42A",
        AccountStateKind.Incomplete => "#FA8C16",
        _ => "#C9CDD4"
    };
    public string Tip => Kind switch
    {
        AccountStateKind.Synced => "该账号上的合集已创建成功",
        AccountStateKind.Incomplete => "平台侧创建未完成，可点卡片「重试」重新同步",
        _ => "该合集尚未同步到此账号 —— 编辑合集并勾选它即可补建"
    };
}

/// <summary>
/// 平台分组行（卡片主体）—— 默认折叠显示聚合「抖音 2/2」，展开逐账号明细。
/// </summary>
public partial class CollectionAccountGroup : ObservableObject
{
    /// <summary>该组账号行（仅当前绑定账号；孤儿/已解绑历史记录不参与展示与计数）</summary>
    public IReadOnlyList<CollectionAccountRow> Accounts { get; init; } = [];

    public string PlatformKey { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ColorHex { get; init; } = "";

    public int SyncedCount => Accounts.Count(r => r.Kind == AccountStateKind.Synced);
    public int TotalCount => Accounts.Count;

    /// <summary>有未完成的记录（卡级重试入口可见性依据）</summary>
    public bool HasIncomplete => Accounts.Any(r => r.Kind == AccountStateKind.Incomplete);

    /// <summary>平台徽标首字（色块字母样式，替代纯色点 —— 卡片更有识别度）</summary>
    public string Initial => TextInitial.Of(DisplayName, "?");

    /// <summary>折叠行右侧聚合文案：已同步 / x/y 已同步 / 待同步</summary>
    public string SummaryText => TotalCount switch
    {
        0 => "",
        _ when SyncedCount == 0 => "待同步",
        _ when SyncedCount >= TotalCount => "已全部同步",
        _ => $"{SyncedCount}/{TotalCount} 已同步"
    };
    /// <summary>聚合状态 pill 底（绿=完成 / 琥珀=待同步·需动作 / 紫=部分完成）</summary>
    public string SummaryColor => TotalCount switch
    {
        _ when SyncedCount >= TotalCount => "#0A5C28",
        _ when SyncedCount == 0 => "#B45F06",
        _ => "#4B3BC3"
    };
    public string SummaryBadgeBg => TotalCount switch
    {
        _ when SyncedCount >= TotalCount => "#EBF7EE",
        _ when SyncedCount == 0 => "#FFF7E6",
        _ => "#F4F1FE"
    };

    /// <summary>组行 ToolTip（折叠态也说明组成）</summary>
    public string Tip => $"{DisplayName}：{SyncedCount}/{TotalCount} 个账号已同步"
        + (HasIncomplete ? "（含未完成的记录，可重试）" : "");

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        // 展开态变化由 VM 订阅写回（key=合集+平台），刷新列表后保持
        ExpandedChanged?.Invoke(this, value);
    }

    /// <summary>展开状态变化通知（VM 订阅持久化，Key 由 Card.CollectionId + PlatformKey 组合）</summary>
    public event Action<CollectionAccountGroup, bool>? ExpandedChanged;
}

/// <summary>
/// 合集列表卡（包装服务端 CollectionItem）。
///
/// 多账号语义（2026-09）：
///   - 展示/计数只认当前绑定账号（绑定 = 会话镜像中存在的会话）。
///   - 平台内每账号一行：有记录 → 已同步 / 未完成；绑定但无记录 → 未同步（可编辑补建）。
///   - 孤儿记录（记录引用的会话已解绑/已不存在，或服务端清引用后的历史残行）
///     不占账号行、不参与徽章计数 —— 用户绑了几个号就看到几行。
///   - 卡片主体按平台分组折叠：折叠行「抖音 2/2」+ 展开逐账号明细。
/// </summary>
public partial class CollectionCard : ObservableObject
{
    private readonly CollectionItem _item;
    private readonly string _prefsPrefix;
    private readonly IDictionary<string, bool> _expandedPrefs;

    public CollectionCard(
        CollectionItem item,
        IReadOnlyDictionary<string, PlatformLookup> meta,
        IReadOnlyDictionary<string, IReadOnlyList<Guid>> boundSessions,   // 平台 → 已绑定会话 ID
        IReadOnlyDictionary<Guid, string> accountLabels,                   // 会话 ID → 显示名
        IDictionary<string, bool> expandedPrefs)                           // 展开态持久（VM 单例字典）
    {
        _item = item;
        _expandedPrefs = expandedPrefs;
        _prefsPrefix = $"{item.Id}:";

        // 未接入合集链路的平台**不显示**（B站 2026-11-05：账号能绑、合集还没对接，
        // 显示出来只会是一行永远「未同步」的死行 —— 用户要求先不要显示）。
        // 记录侧也一起滤：历史残留的 B站 记录同样不该占行/计数。
        var records = (item.Platforms ?? [])
            .Where(r => PlatformNames.SupportsCollections(r.PlatformKey))
            .ToList();

        // 平台顺序 = 记录出现顺序 ∪ 绑定候选（服务端记录已按平台排序；配置缺失兜底名/色）
        var boundKeys = boundSessions.Keys
            .Where(PlatformNames.SupportsCollections)
            .ToList();
        var orderedKeys = records.Select(r => r.PlatformKey)
            .Concat(boundKeys)
            .Distinct()
            .ToList();

        var groups = new List<CollectionAccountGroup>();
        var orphanRecords = 0;

        foreach (var key in orderedKeys)
        {
            var lookup = meta.TryGetValue(key, out var found) ? found : PlatformLookup.Fallback;
            var name = lookup.DisplayName.Length > 0
                ? lookup.DisplayName
                : PlatformNames.GetDisplayName(key);
            // 文章合集只有头条有 —— 卡上的平台组直接标明，避免和视频合集混淆
            if (IsArticle && key == PlatformNames.Toutiao)
                name += "（文章）";

            var keyRecords = records.Where(r => r.PlatformKey == key).ToList();
            var bound = boundSessions.GetValueOrDefault(key) ?? [];

            // ── 该平台当前绑定账号的行 ──
            var rows = new List<CollectionAccountRow>();
            foreach (var sessionId in bound)
            {
                var accountName = accountLabels.TryGetValue(sessionId, out var label)
                    ? label
                    : "未命名账号";
                var record = keyRecords.FirstOrDefault(r => r.PlatformSessionId == sessionId);
                if (record != null)
                {
                    rows.Add(new CollectionAccountRow(key, name, accountName,
                        !string.IsNullOrWhiteSpace(record.PlatformCollectionId)
                            ? AccountStateKind.Synced
                            : AccountStateKind.Incomplete));
                }
                else
                {
                    rows.Add(new CollectionAccountRow(key, name, accountName,
                        AccountStateKind.Missing)); // 绑定但未建记录 → 可补建
                }
            }

            // ── 孤儿记录（会话已解绑/不存在）不占账号行 ──
            foreach (var record in keyRecords)
            {
                var orphan = record.PlatformSessionId == null
                    || !bound.Contains(record.PlatformSessionId.Value);
                if (orphan) orphanRecords++;
            }

            if (rows.Count > 0)
            {
                var group = new CollectionAccountGroup
                {
                    PlatformKey = key,
                    DisplayName = name,
                    ColorHex = lookup.BrandColorHex,
                    Accounts = rows,
                    IsExpanded = expandedPrefs.TryGetValue(_prefsPrefix + key, out var open) && open,
                };
                group.ExpandedChanged += OnGroupExpandedChanged;
                groups.Add(group);
            }
        }

        Groups = groups;
        OrphanRecordCount = orphanRecords;
    }

    /// <summary>分组行（仅当前绑定账号参与；平台顺序：有记录平台 → 绑定账号平台）</summary>
    public IReadOnlyList<CollectionAccountGroup> Groups { get; }

    /// <summary>孤儿记录数（记录引用的会话已解绑 —— 展示剔除、仅提示；删除合集时仍会本地删除）</summary>
    public int OrphanRecordCount { get; }

    public bool HasGroups => Groups.Count > 0;

    private void OnGroupExpandedChanged(CollectionAccountGroup group, bool expanded)
    {
        _expandedPrefs[_prefsPrefix + group.PlatformKey] = expanded;
    }

    public Guid Id => _item.Id;
    public string Title => _item.Title;
    public string? Description => _item.Description;

    /// <summary>合集内容类型：0=视频 1=文章（服务端 CollectionContentType）</summary>
    public int ContentType => _item.ContentType;

    public bool IsArticle => CollectionContentTypeRules.IsArticle(_item.ContentType);

    /// <summary>卡首「文章合集」徽标（视频合集不显示，避免卡片全是标签）</summary>
    public bool HasTypeBadge => IsArticle;
    public string TypeBadgeText => "文章合集";

    /// <summary>卡首字母块文案（标题首字；标题为空兜底「合」）</summary>
    public string Initial => TextInitial.Of(_item.Title, "合");

    /// <summary>同步覆盖率（0-100，ProgressBar 数据源；无目标账号 → 0）</summary>
    public double ProgressValue => TotalCountForSummary == 0
        ? 0
        : Math.Round(SyncedCountForSummary * 100.0 / TotalCountForSummary);

    /// <summary>本地封面路径（编辑回填用；文件可能已被移动 —— 编辑时重选即覆盖）</summary>
    public string? CoverImagePath => _item.CoverImagePath;

    /// <summary>有无「未完成」记录（卡级重试按钮）</summary>
    public bool NeedsRetry => Groups.Any(g => g.HasIncomplete);

    // ── 删除未完成警示（页面在列表加载后灌入；卡是每次加载新建的，不需要防刷新覆盖） ──

    /// <summary>删除任务里还没删掉的账号数（&gt;0 = 卡上挂「删除未完成」条 + 去处理）</summary>
    [ObservableProperty] private int _failedDeleteCount;

    public bool HasDeleteFailure => FailedDeleteCount > 0;

    /// <summary>警示条文案（纯函数在 AsyncTaskFormat，页面只绑）</summary>
    public string DeleteFailureText => AsyncTaskFormat.CollectionDeleteFailureText(FailedDeleteCount);

    partial void OnFailedDeleteCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasDeleteFailure));
        OnPropertyChanged(nameof(DeleteFailureText));
    }

    /// <summary>底部时间行（修改时间优先，无则创建时间）</summary>
    public string DateText => (_item.LastModified ?? _item.CreatedAt).ToString("yyyy-MM-dd HH:mm");

    // ── 徽章与列表汇总（按当前绑定账号计数；孤儿不计） ──

    public int SyncedCountForSummary => Groups.Sum(g => g.SyncedCount);
    public int TotalCountForSummary => Groups.Sum(g => g.TotalCount);

    public string SyncBadgeText => TotalCountForSummary switch
    {
        0 when OrphanRecordCount > 0 => "仅历史记录",
        0 => "未同步到任何账号",
        _ when SyncedCountForSummary == 0 => "待同步",
        _ when SyncedCountForSummary >= TotalCountForSummary => "已全部同步",
        _ => $"{SyncedCountForSummary}/{TotalCountForSummary} 已同步"
    };
    public string SyncBadgeBg => TotalCountForSummary switch
    {
        0 => "#F2F3F5",
        _ when SyncedCountForSummary == 0 => "#FFF7E6",
        _ when SyncedCountForSummary >= TotalCountForSummary => "#EBF7EE",
        _ => "#F4F1FE"
    };
    public string SyncBadgeFg => TotalCountForSummary switch
    {
        0 => "#86909C",
        _ when SyncedCountForSummary == 0 => "#B45F06",
        _ when SyncedCountForSummary >= TotalCountForSummary => "#0A5C28",
        _ => "#4B3BC3"
    };
    public string SyncBadgeTip => TotalCountForSummary == 0
        ? OrphanRecordCount > 0
            ? "该合集只在已解绑的账号上同步过（本端已无法管理）"
            : "该合集尚未同步到任何账号 —— 点「编辑」添加发布目标"
        : $"当前绑定账号中 {SyncedCountForSummary}/{TotalCountForSummary} 已同步"
            + (OrphanRecordCount > 0 ? $"，另有 {OrphanRecordCount} 条已解绑账号的历史记录" : "");

    // ── 多选（批量删除用） ──

    /// <summary>
    /// 删除确认条文案。**本次语义反转的落点**：以前写的是「平台侧删除失败时仍会删除本地记录」，
    /// 现在是「全部账号删光才删本地记录」—— 一个字的差别，背后是平台侧作品会不会变孤儿。
    /// 账号数取「已同步的绑定账号」（= 真会去平台侧删的那些），孤儿记录不计。
    /// </summary>
    public string DeleteStripText => CollectionDeleteFormat.StripText(SyncedCountForSummary);

    /// <summary>
    /// 勾选态。选中态散在每张卡上，页面级计数由 VM 算出来 —— 卡片变了要能让 VM 播报
    /// （VM 订阅每张卡的 PropertyChanged，见 CollectionViewModel.OnCardPropertyChanged）。
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    // ── 删除两段式确认（参考平台页解绑 strip） ──

    [ObservableProperty]
    private bool _isConfirmingDelete;

    /// <summary>常规操作行可见（确认条展开时隐藏）</summary>
    public bool ShowOperations => !IsConfirmingDelete;

    /// <summary>删除确认条可见</summary>
    public bool ShowDeleteConfirmStrip => IsConfirmingDelete;

    partial void OnIsConfirmingDeleteChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOperations));
        OnPropertyChanged(nameof(ShowDeleteConfirmStrip));
    }
}
