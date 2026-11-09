namespace CreatFlow.NewApp.Services;

/// <summary>合成任务状态（持久化契约：字符串枚举存 compose_queue.json，改枚举项不会错位）</summary>
public enum ComposeTaskStatus
{
    Pending,      // 待合成（排队中）
    Running,      // 合成中
    Done,         // 已完成
    Failed,       // 失败（原因在 StatusMessage）
    Cancelled,    // 已取消（可重试）
    Interrupted,  // 上次运行中断（进程退出时还在跑；可重试）
}

/// <summary>
/// 「合成完成后自动发布」的状态轴 —— 与 <see cref="ComposeTaskStatus"/> **正交**：
/// 合成已完成是事实（产物可播放），发布是它之后独立的一件事，两轴分开才不会污染合成侧的恢复规则。
/// </summary>
public enum AutoPublishState
{
    None,        // 未启用自动发布（默认；老存档无此字段天然兼容）
    Submitting,  // 已落盘「正在提交」标记，HTTP 在途（崩溃 → 恢复成 Uncertain）
    Submitted,   // 已进上传队列（数量在 AutoPublishText）
    Failed,      // 提交失败（原因在 AutoPublishText）
    Skipped,     // 未提交（许可过期等，原因在 AutoPublishText）
    Uncertain,   // 提交中被中断 —— 服务端是否已建记录无从得知，不自动重发
}

/// <summary>一条合成任务的存档（compose_queue.json 的单元）—— 只存输入快照与结果定位，派生显示走 UI 侧</summary>
public sealed record ComposeTaskData
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public List<string> MaterialPaths { get; init; } = [];
    public string? Text { get; init; }

    /// <summary>角色名（角色库；仅展示 —— 老存档没有这个字段，可空天然兼容）</summary>
    public string? RoleName { get; init; }
    public bool UseShots { get; init; }
    public string? AudioPath { get; init; }
    public string? ProviderName { get; init; }
    public string? VoiceId { get; init; }
    public double Speed { get; init; } = 1.0;
    public double Pitch { get; init; } = 1.0;
    public string? BgmPath { get; init; }
    public double BgmVolume { get; init; } = 0.25;
    public bool IsWide { get; init; }

    /// <summary>水印快照（2026-10-21；老存档缺此字段 → 默认开 + 房淘惠 + 右上）</summary>
    public WatermarkSpec Watermark { get; init; } = WatermarkSpec.Default;

    public string? OverlayTitle { get; init; }
    public string OutputDirectory { get; init; } = "";
    public DateTime? ScheduledAt { get; init; }
    public ComposeTaskStatus Status { get; init; } = ComposeTaskStatus.Pending;
    public string StatusMessage { get; init; } = "";
    public int Progress { get; init; }
    public string? OutputPath { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>自动发布目标账号快照（空 = 未启用；老存档无此字段天然兼容）</summary>
    public List<AutoPublishTarget> AutoPublishTargets { get; init; } = [];
    public AutoPublishState AutoPublishState { get; init; } = AutoPublishState.None;
    public string AutoPublishText { get; init; } = "";
}

/// <summary>
/// 合成队列持久化 —— accounts/{memberId:N}/compose_queue.json（本机任务，不经服务端）。
/// **会员级路径**（2026-10-15 批次 7）：任务快照含会员自己的账号目标（AutoPublishTargets），
/// 机器级共享时另一会员登录会看到、甚至跑这份队列（自动发布还会拿错 token）。旧机器级档由
/// 升级后第一个登录的会员一次性采纳。
/// 重开恢复规则见 <see cref="ApplyRestoreRules"/>；冒烟模式下 App 层换 TEMP 根，不碰用户队列。
/// </summary>
public sealed class ComposeQueueStore
{
    private const string FileName = "compose_queue.json";

    private readonly LocalJsonFile _file;
    private readonly Func<Guid> _memberId;

    /// <param name="root">存储根（默认 %LocalAppData%\CreatFlow；冒烟传 TEMP 根）</param>
    /// <param name="memberId">当前会员提供者（DI 传 SessionContext；默认 Guid.Empty = no-member）</param>
    public ComposeQueueStore(string? root = null, Func<Guid>? memberId = null)
    {
        _file = new LocalJsonFile(root ?? LocalJsonFile.DefaultRoot, "ComposeQueue");
        _memberId = memberId ?? (() => Guid.Empty);
    }

    /// <summary>当前会员 ID（VM 换号检测用）</summary>
    public Guid CurrentMemberId => _memberId();

    /// <summary>当前会员的存档路径（冒烟 TEMP 根守卫 / 测试用）</summary>
    public string GetConfigPath() => MemberPath(CurrentMemberId);

    /// <summary>旧版机器级存档路径（一次性采纳的来源；冒烟清档要一并清）</summary>
    public string GetLegacyPath() => _file.PathOf(FileName);

    private string MemberPath(Guid memberId) => _file.PathOf(LocalJsonFile.MemberRelative(memberId, FileName));

    /// <summary>读某会员的队列（默认当前会员；换号切换显式传目标会员）。</summary>
    public async Task<List<ComposeTaskData>> LoadAsync(Guid? memberId = null, CancellationToken ct = default)
    {
        var member = memberId ?? _memberId();
        return await _file.ReadMemberOrAdoptAsync<List<ComposeTaskData>>(
            MemberPath(member), GetLegacyPath(), member,
            l => l.Count > 0, "ComposeQueue", ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>整份覆盖写。memberId 显式锁定归属会员 —— 队列 VM 一律传加载时锁定的属主，
    /// 任务在换号挂起期间跑完也不写错目录。</summary>
    public async Task SaveAsync(IReadOnlyCollection<ComposeTaskData> tasks,
        Guid? memberId = null, CancellationToken ct = default)
        => await _file.WriteAsync(MemberPath(memberId ?? _memberId()), tasks.ToList(), ct).ConfigureAwait(false);

    /// <summary>
    /// 重启恢复规则（纯函数，--selftest 直接断言）：
    /// 排队中的原样续跑；**合成中**的标「已中断」待重试 —— 上次进程退出时正在跑的不能假装还活着，
    /// 也不自动续跑（半截输出是否可用没人知道），交给用户点重试。
    /// **发布提交中**被中断的：服务端是否已建记录无从得知 —— 标「状态未知」提示人工核对，
    /// 绝不自动重发（重复提交一条视频的代价比"让用户看一眼"大得多）。
    /// </summary>
    public static List<ComposeTaskData> ApplyRestoreRules(IEnumerable<ComposeTaskData> tasks) =>
        [.. tasks.Select(t => t switch
        {
            { Status: ComposeTaskStatus.Running } =>
                t with { Status = ComposeTaskStatus.Interrupted, StatusMessage = "上次运行中断，可重试" },
            { AutoPublishState: AutoPublishState.Submitting } =>
                t with
                {
                    AutoPublishState = AutoPublishState.Uncertain,
                    AutoPublishText = "上次在提交发布时被中断 —— 可能已进上传队列，请到「上传队列」核对（不会自动重发）",
                },
            _ => t,
        })];
}
