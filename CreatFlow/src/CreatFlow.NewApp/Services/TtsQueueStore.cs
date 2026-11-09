namespace CreatFlow.NewApp.Services;

/// <summary>配音任务状态（持久化契约：字符串枚举存 tts_queue.json，改枚举项不会错位）</summary>
public enum TtsTaskStatus
{
    Pending,      // 待合成（排队中）
    Running,      // 合成中
    Done,         // 已完成（mp3 已落导出目录）
    Failed,       // 失败（原因在 StatusMessage）
    Cancelled,    // 已取消（可重试）
    Interrupted,  // 上次运行中断（进程退出时还在跑；可重试）
}

/// <summary>一条配音任务的存档（tts_queue.json 的单元）—— 只存提交时的输入快照与结果定位</summary>
public sealed record TtsTaskData
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public string? ProviderName { get; init; }
    public string? VoiceId { get; init; }
    public double Speed { get; init; } = 1.0;
    public double Pitch { get; init; } = 1.0;

    /// <summary>导出目录（提交时快照 —— 之后改「保存目录」不影响已提交的任务）</summary>
    public string ExportDirectory { get; init; } = "";

    /// <summary>建议文件名、不含扩展名（提交时定死；跨批次撞名落盘时兜底加后缀）</summary>
    public string SuggestedName { get; init; } = "";

    public TtsTaskStatus Status { get; init; } = TtsTaskStatus.Pending;
    public string StatusMessage { get; init; } = "";
    public string? OutputPath { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>队列存档整体：任务 + 队列级暂停态（用户暂停后关掉重开，不该又自己跑起来）</summary>
public sealed record TtsQueueState
{
    public List<TtsTaskData> Tasks { get; init; } = [];
    public bool IsPaused { get; init; }
}

/// <summary>
/// 配音队列持久化 —— accounts/{memberId:N}/tts_queue.json（本机任务，与合成队列同构）。
/// **会员级路径**（2026-10-15 批次 7）：任务含会员自己的文案/导出目录，机器级共享时
/// 另一会员登录会看到、甚至跑这份队列。旧机器级档由升级后第一个登录的会员一次性采纳。
/// 重开恢复规则见 <see cref="ApplyRestoreRules"/>；冒烟模式下 App 层换 TEMP 根，不碰用户队列。
/// </summary>
public sealed class TtsQueueStore
{
    private const string FileName = "tts_queue.json";

    private readonly LocalJsonFile _file;
    private readonly Func<Guid> _memberId;

    /// <param name="root">存储根（默认 %LocalAppData%\CreatFlow；冒烟传 TEMP 根）</param>
    /// <param name="memberId">当前会员提供者（DI 传 SessionContext；默认 Guid.Empty = no-member）</param>
    public TtsQueueStore(string? root = null, Func<Guid>? memberId = null)
    {
        _file = new LocalJsonFile(root ?? LocalJsonFile.DefaultRoot, "TtsQueue");
        _memberId = memberId ?? (() => Guid.Empty);
    }

    /// <summary>当前会员 ID（VM 换号检测用）</summary>
    public Guid CurrentMemberId => _memberId();

    /// <summary>当前会员的存档路径（冒烟 TEMP 根守卫 / 测试用）</summary>
    public string GetConfigPath() => MemberPath(CurrentMemberId);

    /// <summary>旧版机器级存档路径（一次性采纳的来源；冒烟清档要一并清）</summary>
    public string GetLegacyPath() => _file.PathOf(FileName);

    private string MemberPath(Guid memberId) => _file.PathOf(LocalJsonFile.MemberRelative(memberId, FileName));

    /// <summary>读某会员的队列（默认当前会员；换号切换显式传目标会员）。
    /// 空任务但 IsPaused=true 的档不算内容（采纳等价于默认，见批次 7 决策）。</summary>
    public async Task<TtsQueueState> LoadAsync(Guid? memberId = null, CancellationToken ct = default)
    {
        var member = memberId ?? _memberId();
        return await _file.ReadMemberOrAdoptAsync<TtsQueueState>(
            MemberPath(member), GetLegacyPath(), member,
            s => s.Tasks.Count > 0, "TtsQueue", ct).ConfigureAwait(false) ?? new TtsQueueState();
    }

    /// <summary>整份覆盖写。memberId 显式锁定归属会员 —— 队列 VM 一律传加载时锁定的属主，
    /// 任务在换号挂起期间跑完也不写错目录。</summary>
    public async Task SaveAsync(TtsQueueState state,
        Guid? memberId = null, CancellationToken ct = default)
        => await _file.WriteAsync(MemberPath(memberId ?? _memberId()), state, ct).ConfigureAwait(false);

    /// <summary>
    /// 重启恢复规则（纯函数，--selftest 直接断言）：排队中的原样续跑；**合成中**的标「已中断」待重试
    /// —— 与合成队列同一套理由（上次进程退出时正在跑的不能假装还活着，半截输出是否可用没人知道）。
    /// </summary>
    public static TtsQueueState ApplyRestoreRules(TtsQueueState state) => state with
    {
        Tasks = [.. state.Tasks.Select(t => t.Status == TtsTaskStatus.Running
            ? t with { Status = TtsTaskStatus.Interrupted, StatusMessage = "上次运行中断，可重试" }
            : t)],
    };
}
