namespace CreatFlow.NewApp.Models;

// ═══════════════════════════════════════════════════════════════════
// 合集 API DTO + 编辑表单 UI 模型（自 CreatFlow.App 移植，2026-09-09）。
// 变更纪律：服务端契约 record 保持原样；emoji 状态点全部改为色点 Hex
// （对齐 PlatformSessionModels 语义：0 绿 #00B42A / 1 橙 #FA8C16 /
// 2 红 #F53F3F / 未知灰 #C9CDD4），界面零 emoji。
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// 首字提取（列表卡字母块 / 平台徽标）—— 代理对安全：emoji 标题不劈成半个码元。
/// </summary>
internal static class TextInitial
{
    public static string Of(string? text, string fallback)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return fallback;
        return char.IsHighSurrogate(s[0]) && s.Length > 1 ? s[..2] : s[..1];
    }
}

// ═══ Response DTO ═══

/// <summary>
/// Collection list item returned by GET /api/collections/list
/// </summary>
public record CollectionItem(
    Guid Id,
    string Title,
    string? Description,
    string? CoverImagePath,
    string? CoverImageUrl,
    int PlatformCount,
    int CreatedCount,
    DateTime CreatedAt,
    DateTime? LastModified,
    List<CollectionPlatformSummary>? Platforms,  // ← 发布页用：取 platform_collection_id
    int ContentType = 0                          // 0=视频 1=文章（服务端 CollectionContentType）
);

/// <summary>
/// Lightweight platform summary embedded in CollectionItem.
/// Used by the publish page to map a collection → platform_collection_id (mix_id).
/// </summary>
public record CollectionPlatformSummary(
    string PlatformKey,
    string? PlatformCollectionId,     // Douyin: mix_id
    Guid? PlatformSessionId,
    string? PlatformSessionLabel      // "大号@示例账号"
);

/// <summary>
/// Collection list response wrapper.
/// </summary>
public record CollectionListResponse(
    List<CollectionItem> Collections
);

/// <summary>
/// Full collection detail with platform records.
/// </summary>
public record CollectionDetail(
    Guid Id,
    string Title,
    string? Description,
    string? CoverImagePath,      // 本地路径（重试用）
    string? CoverImageUrl,       // CDN URL（展示用）
    List<CollectionPlatformItem> Platforms,
    DateTime CreatedAt,
    DateTime? LastModified,
    int ContentType = 0          // 0=视频 1=文章（编辑时类型只读）
);

/// <summary>
/// Per-platform sync record.
/// </summary>
public record CollectionPlatformItem(
    Guid Id,
    string PlatformKey,
    string PlatformDisplayName,
    Guid? PlatformSessionId,
    string? PlatformCollectionId,
    string? CollectionType,
    string? Title,
    string? Description,
    string? CoverImage,
    int Status,
    string StatusText,
    string? ErrorMsg,
    string? ExtraDataJson,
    DateTime CreatedAt
);

/// <summary>
/// Sync result returned after syncing to platforms.
/// </summary>
public record CollectionSyncResult(
    Guid CollectionId,
    List<CollectionPlatformItem> Results
);

// ═══ Request DTO ═══

/// <summary>
/// Create a new collection with platform targets.
/// </summary>
public record CreateCollectionRequest(
    string Title,
    string? Description,
    string? CoverImagePath,      // 本地封面图路径
    List<CreateTargetRequest>? Targets,
    int ContentType = 0          // 0=视频 1=文章（文章仅头条）
);

/// <summary>
/// Single create target — platform + session + optional collection type.
/// </summary>
public record CreateTargetRequest(
    string PlatformKey,
    Guid? PlatformSessionId,
    string? CollectionType
);

/// <summary>
/// Update collection metadata.
/// </summary>
public record UpdateCollectionRequest(
    Guid Id,
    string Title,
    string? Description,
    string? CoverImagePath       // 本地封面图路径
);

/// <summary>
/// Delete a collection.
/// </summary>
public record DeleteCollectionRequest(
    Guid Id
);

/// <summary>
/// Sync collection to platforms.
/// </summary>
public record SyncCollectionRequest(
    Guid CollectionId,
    List<SyncTargetRequest> Targets
);

/// <summary>
/// Single sync target.
/// </summary>
public record SyncTargetRequest(
    string PlatformKey,
    Guid? PlatformSessionId,
    string? Title,
    string? Description,
    string? CoverImage,
    string? ExtraDataJson
);

/// <summary>
/// Get collection detail request.
/// </summary>
public record GetCollectionDetailRequest(
    Guid Id
);

// ═══ Session Credentials (for desktop SDK calls) ═══

/// <summary>
/// Response from POST /api/platform-sessions/credentials
/// </summary>
public record PlatformSessionCredentials(
    Guid Id,
    string PlatformKey,
    string? Cookie,
    string? ExtraDataJson,      // JSON: { csrfToken, msToken, a_bogus, ... }
    string? LocalStorageJson,   // 完整 localStorage JSON（含 security-sdk ECDSA 密钥等）
    string? PlatformUid,
    string? PlatformName
);

/// <summary>
/// Request for POST /api/platform-sessions/credentials
/// </summary>
public record GetCredentialsRequest(Guid SessionId);

/// <summary>
/// Request for POST /api/collections/report
/// </summary>
public record ReportCollectionRequest(
    Guid CollectionPlatformId,
    bool Success,
    string? PlatformCollectionId,
    string? ErrorMsg,
    string? Title,
    string? Description,
    string? CoverImageUrl        // TOS key / CDN URL（上传成功后回填）
);

// ═══ UI Model: Platform target item for the editor form ═══

/// <summary>
/// 平台目标项 — 新建/编辑合集表单中"选择平台 → 选择账号"的数据载体。
/// 每个平台一条，包含该平台下所有已登录的会话供用户选择。
/// 状态用色点 Hex（无 emoji）：
///   SummaryDotHex = 平台下有会话 → 正常 #00B42A / 有即将过期 #FA8C16 / 有已过期 #F53F3F；无会话 → 灰 #C9CDD4
/// </summary>
public partial class PlatformTargetItem : ObservableObject
{
    /// <summary>平台标识</summary>
    public string PlatformKey { get; init; } = "";

    /// <summary>平台中文名</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>平台品牌色</summary>
    public string BrandColorHex { get; init; } = "";

    /// <summary>平台徽标首字（品牌色字母块 —— 与列表卡同一视觉语言）</summary>
    public string Initial => TextInitial.Of(DisplayName, "?");

    /// <summary>卡片底色：勾选 = 极淡品牌紫（一眼看出哪些平台已纳入本次发布）</summary>
    public string CardBgHex => IsSelected ? "#FBF9FF" : "#FFFFFF";

    /// <summary>是否勾选该平台</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// 平台行是否可操作。文章合集模式下非头条平台灰显禁用
    /// （2026-11-05：头条文章合集没有跨平台对应物，选了文章就只能发头条）。
    /// </summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>合集类型（历史字段，UI 不再录入，请求恒 null —— 服务端 Pending 记录扩展用）</summary>
    [ObservableProperty]
    private string _collectionType = "";

    /// <summary>该平台下的已登录会话列表（每个会话一个独立勾选项 —— 多账号可一次全选）</summary>
    public ObservableCollection<SessionOption> Sessions { get; } = new();

    // ── 计算属性 ──

    /// <summary>是否有已登录的会话</summary>
    public bool HasSessions => Sessions.Count > 0;

    /// <summary>是否已勾选但没有会话（需引导去登录）</summary>
    public bool NeedsLogin => IsSelected && !HasSessions;

    /// <summary>是否显示会话选择区（勾选了 + 至少有一个会话）</summary>
    public bool ShowSessionPicker => IsSelected && Sessions.Count >= 1;

    /// <summary>已勾选的账号（多账号多选 —— 保存时逐会话建档/同步）</summary>
    public IReadOnlyList<SessionOption> SelectedSessions => Sessions.Where(s => s.Selected).ToList();

    /// <summary>行尾计数文案：无会话 = 「未绑定」；未勾选 = 「N 个账号」；勾选 = 「已选 N 个账号」</summary>
    public string SessionCountText => Sessions.Count == 0
        ? "未绑定"
        : IsSelected ? $"已选 {SelectedSessions.Count} 个账号" : $"{Sessions.Count} 个账号";

    /// <summary>勾选了平台但一个账号都没勾（保存校验拦截用）</summary>
    public bool NeedsAccount => IsSelected && HasSessions && SelectedSessions.Count == 0;

    /// <summary>
    /// 平台创建合集时是否需要上传封面图（2026-11-05 抓包）：
    /// 抖音、今日头条（文章/视频两套）必填；B站用默认素材图，视频号/小红书/快手自动取首作品。
    /// </summary>
    public bool NeedsCover => PlatformKey is PlatformNames.Douyin or PlatformNames.Toutiao;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(NeedsLogin));
        OnPropertyChanged(nameof(ShowSessionPicker));
        OnPropertyChanged(nameof(NeedsAccount));
        OnPropertyChanged(nameof(SessionCountText));
        OnPropertyChanged(nameof(CardBgHex));
        if (!value)
        {
            // 取消勾选平台：清空账号选择（防再勾回时的旧选择错配）
            CollectionType = "";
            foreach (var s in Sessions) s.Selected = false;
        }
        else if (HasSessions && SelectedSessions.Count == 0)
        {
            // 勾选平台：默认全选该平台全部账号（多账号用户最常见的诉求）；
            // 单个账号场景直接选中，无歧义
            foreach (var s in Sessions) s.Selected = true;
        }
    }

    /// <summary>账号勾选状态变化（SessionOption.Selected 变化时由 VM 订阅调用来刷新行尾计数）</summary>
    public void NotifySessionSelectionChanged()
    {
        OnPropertyChanged(nameof(NeedsAccount));
        OnPropertyChanged(nameof(SelectedSessions));
        OnPropertyChanged(nameof(SessionCountText));
    }
}

/// <summary>
/// 会话选项 — 平台下的一个已登录账号（编辑器账号勾选行；多账号可多选）。
/// </summary>
public partial class SessionOption : ObservableObject
{
    /// <summary>会话 ID</summary>
    public Guid SessionId { get; init; }

    /// <summary>显示标签（账号备注或平台昵称）</summary>
    public string Label { get; init; } = "";

    /// <summary>会话状态：0=Active 1=ExpiringSoon 2=Expired</summary>
    public int Status { get; init; }

    /// <summary>是否勾选（保存目标 = 勾选的平台 × 勾选的账号；UI 直接双向绑定）</summary>
    [ObservableProperty]
    private bool _selected;

    /// <summary>状态点色（色块非 emoji）</summary>
    public string StatusDotHex => Status switch
    {
        0 => "#00B42A",
        1 => "#FA8C16",
        2 => "#F53F3F",
        _ => "#C9CDD4"
    };
}

// ═══ Sync Progress (per-platform real-time status) ═══

/// <summary>
/// Per-platform sync progress reported back to the ViewModel in real time.
///
/// 通知纪律：字段由同步编排线程（后台）推进，通知统一由 UI 线程的
/// <see cref="NotifyProgressChanged"/> 发出 —— 不在属性 setter 里发通知，
/// 免绑定更新跨线程（进度 chip 曾因此冻结在「创建中…」，失败/成功都不刷新）。
/// </summary>
public partial class PlatformSyncProgress : ObservableObject
{
    public string PlatformKey { get; init; } = "";

    /// <summary>目标会话（多账号多记录时同一平台可出现多条进度，以 平台+会话 区分合并）</summary>
    public Guid? SessionId { get; init; }

    public string DisplayName { get; init; } = "";
    public string AccountName { get; init; } = "";
    /// <summary>显示标签：平台名(账号名)</summary>
    public string Label => string.IsNullOrEmpty(AccountName)
        ? DisplayName
        : $"{DisplayName}({AccountName})";
    public PlatformSyncStep Step { get; set; } = PlatformSyncStep.Pending;
    public string StepText => Step switch
    {
        PlatformSyncStep.Pending => "等待中",
        PlatformSyncStep.FetchingCredentials => "获取凭证…",
        PlatformSyncStep.RefreshingSts => "刷新 STS…",
        PlatformSyncStep.UploadingCover => "上传封面…",
        PlatformSyncStep.Creating => "创建中…",
        PlatformSyncStep.Created => "已创建",
        // 删除复用同一套进度条，但文案必须分开 —— 早先删除借用 Creating/Created，
        // 用户在删除时看到的是「创建中…/已创建」，等于没说
        PlatformSyncStep.Deleting => "删除中…",
        PlatformSyncStep.Deleted => "已删除",
        PlatformSyncStep.Failed => "失败",
        PlatformSyncStep.Skipped => "已跳过",
        _ => "未知"
    };
    /// <summary>UI 上色：进度文字前景（进度=蓝灰、成功=绿、失败=红、跳过=灰）</summary>
    public string BadgeFg => Step switch
    {
        PlatformSyncStep.Created or PlatformSyncStep.Deleted => "#0A5C28",
        PlatformSyncStep.Failed => "#C62828",
        PlatformSyncStep.Skipped => "#86909C",
        _ => "#4E5969"
    };
    /// <summary>UI 上色：chip 底色（进行中用中灰 —— 白卡上需可辨）</summary>
    public string BadgeBg => Step switch
    {
        PlatformSyncStep.Created or PlatformSyncStep.Deleted => "#EBF7EE",
        PlatformSyncStep.Failed => "#FDECEA",
        PlatformSyncStep.Skipped => "#F2F3F5",
        _ => "#EFF1F4"
    };
    /// <summary>进度行状态点色</summary>
    public string DotHex => Step switch
    {
        PlatformSyncStep.Created or PlatformSyncStep.Deleted => "#00B42A",
        PlatformSyncStep.Failed => "#F53F3F",
        PlatformSyncStep.Skipped => "#C9CDD4",
        _ => "#6C5CE7"   // 进行中 = 品牌紫呼吸点
    };
    public string? PlatformCollectionId { get; set; }
    public string? ErrorMsg { get; set; }
    public bool IsCompleted => Step is PlatformSyncStep.Created or PlatformSyncStep.Deleted
        or PlatformSyncStep.Failed or PlatformSyncStep.Skipped;

    /// <summary>UI 线程刷新 chip（值已由同步线程写好，这里只补发通知）—— 见类型注释「通知纪律」。</summary>
    public void NotifyProgressChanged()
    {
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(BadgeFg));
        OnPropertyChanged(nameof(BadgeBg));
        OnPropertyChanged(nameof(DotHex));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(ErrorMsg));
        OnPropertyChanged(nameof(PlatformCollectionId));
    }
}

/// <summary>
/// Sync step enum — used for real-time UI progress display.
/// </summary>
public enum PlatformSyncStep
{
    Pending,
    FetchingCredentials,
    RefreshingSts,
    UploadingCover,
    Creating,
    Created,
    /// <summary>正在从平台侧删除合集（与 Creating 同一条进度条，只是文案不同）</summary>
    Deleting,
    /// <summary>平台侧已删除</summary>
    Deleted,
    Failed,
    Skipped,
}
