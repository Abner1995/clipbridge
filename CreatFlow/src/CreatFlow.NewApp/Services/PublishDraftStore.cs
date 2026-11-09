namespace CreatFlow.NewApp.Services;

/// <summary>
/// 发布页本地持久化 —— 账号级预设 + 未提交草稿（设计文档 §3.2）。
///
/// **会员级路径**（2026-10-15 批次 7）：accounts/{memberId:N}/publish-defaults.json +
/// publish-drafts.json —— 预设 key 与草稿 Targets 都引用会员自己的会话 ID，机器级共享时
/// 另一会员进页剪除遗骸会把自己这份**静默删掉**（与账号分组修过的同一类 bug）。
/// 旧机器级档由升级后第一个登录的会员一次性采纳。
///
/// 为什么落本地而不是服务端：这些是客户端体验偏好，不含敏感信息，换机器丢失的代价可接受。
/// 若后续要跨机器同步，<see cref="PlatformParams"/> 的序列化形状可直接搬到服务端加一列 JSON。
///
/// 两件事**必须**分开存：预设是长期资产（账号解绑才清），草稿是临时状态（提交成功即移除）。
/// 混一个文件会互相拖累 —— 草稿写坏会把预设一起带走。
/// </summary>
public sealed class PublishDraftStore
{
    private const string DefaultsFileName = "publish-defaults.json";
    private const string DraftsFileName = "publish-drafts.json";

    private readonly LocalJsonFile _io;
    private readonly Func<Guid> _memberId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="root">存储根（默认 %LocalAppData%\CreatFlow；冒烟传 TEMP 根）</param>
    /// <param name="memberId">当前会员提供者（DI 传 SessionContext；默认 Guid.Empty = no-member）</param>
    public PublishDraftStore(string? root = null, Func<Guid>? memberId = null)
    {
        _io = new LocalJsonFile(root ?? LocalJsonFile.DefaultRoot, "Publish");
        _memberId = memberId ?? (() => Guid.Empty);
    }

    /// <summary>当前会员 ID（服务层换号检测用）</summary>
    public Guid CurrentMemberId => _memberId();

    /// <summary>当前会员的预设路径（冒烟 TEMP 根守卫 / 测试用）</summary>
    public string GetConfigPath() => DefaultsPath(CurrentMemberId);

    /// <summary>旧版机器级预设路径（一次性采纳的来源；冒烟清档要一并清）</summary>
    public string GetLegacyPath() => _io.PathOf(DefaultsFileName);

    /// <summary>当前会员的草稿路径（冒烟守卫用）</summary>
    public string GetDraftsPath() => DraftsPath(CurrentMemberId);

    /// <summary>旧版机器级草稿路径（冒烟清档要一并清）</summary>
    public string GetLegacyDraftsPath() => _io.PathOf(DraftsFileName);

    private string DefaultsPath(Guid memberId) => _io.PathOf(LocalJsonFile.MemberRelative(memberId, DefaultsFileName));

    private string DraftsPath(Guid memberId) => _io.PathOf(LocalJsonFile.MemberRelative(memberId, DraftsFileName));

    // ═══════════════════════════════════════════════════════════════
    //  账号级预设（key = {platformKey}:{sessionId}）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>读全部预设。文件缺失/损坏一律返回空字典（不阻断进页面）。</summary>
    public Task<Dictionary<string, PlatformParams>> LoadDefaultsAsync(CancellationToken ct = default)
        => LoadDefaultsForAsync(_memberId(), ct);

    /// <summary>整份覆盖写。调用方负责先做账号存活裁剪（见 <see cref="PruneAsync"/>）。</summary>
    public Task SaveDefaultsAsync(IReadOnlyDictionary<string, PlatformParams> defaults,
        Guid? memberId = null, CancellationToken ct = default)
        => SaveDefaultsForAsync(memberId ?? _memberId(), defaults, ct);

    /// <summary>
    /// 清掉已解绑账号的预设条目（账号解绑时调）。
    /// 留着不删的后果：用户重新绑一个**新**账号拿到同一 Guid（服务端 recreate 同 id）时
    /// 会套上别人的预设 —— 合集/位置全错，且看不出原因。
    ///
    /// 入口锁定 member：load + save 必须是同一会员的档（两个 await 之间可能换号
    /// —— 换号后把 B 的在册集合去剪 A 的档，就是本次修复的那种误删）。
    /// </summary>
    public async Task<int> PruneAsync(IReadOnlyCollection<Guid> activeSessionIds,
        Guid? memberId = null, CancellationToken ct = default)
    {
        var member = memberId ?? _memberId();
        var defaults = await LoadDefaultsForAsync(member, ct);
        var alive = activeSessionIds.Select(id => id.ToString("D")).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dead = defaults.Keys
            .Where(k => k.Split(':') is [_, var sid] && !alive.Contains(sid))
            .ToList();
        if (dead.Count == 0) return 0;

        foreach (var k in dead) defaults.Remove(k);
        await SaveDefaultsForAsync(member, defaults, ct);
        Log.Debug("[Publish] 清理已解绑账号的发布预设 {Count} 条", dead.Count);
        return dead.Count;
    }

    private async Task<Dictionary<string, PlatformParams>> LoadDefaultsForAsync(Guid memberId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var defaults = await _io.ReadMemberOrAdoptAsync<Dictionary<string, PlatformParams>>(
                DefaultsPath(memberId), GetLegacyPath(), memberId,
                d => d.Count > 0, "Publish", ct) ?? [];

            // 旧预设的话题 ID 只在 PlatformParams.Topics（ResolvedTopic）里 ——
            // 2.6 起 builder 读 TopicRefs，不迁移的话旧账号预设的话题会静默退化成纯文本
            foreach (var p in defaults.Values)
            {
                p?.MigrateLegacyTopics();
                // 2.6.4：旧「官方活动 ID」升成活动快照（界面才看得见、清得掉）
                p?.MigrateLegacyActivities();
            }
            return defaults;
        }
        finally { _gate.Release(); }
    }

    private async Task SaveDefaultsForAsync(Guid memberId,
        IReadOnlyDictionary<string, PlatformParams> defaults, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await _io.WriteAsync(DefaultsPath(memberId), defaults, ct); }
        finally { _gate.Release(); }
    }

    /// <summary>预设 key —— 与 <see cref="PublishTarget.Key"/> 同构（缺了 Guid 格式差异会静默不命中）。</summary>
    public static string KeyOf(string platformKey, Guid sessionId) => $"{platformKey}:{sessionId:D}";

    // ═══════════════════════════════════════════════════════════════
    //  未提交草稿
    // ═══════════════════════════════════════════════════════════════

    /// <summary>恢复上次未提交的草稿（启动/进发布页时调）。文件里的视频若已不存在则丢弃该条。</summary>
    public async Task<List<PublishDraft>> LoadDraftsAsync(CancellationToken ct = default)
    {
        var memberId = _memberId();   // 入口锁定：await 期间换号不影响本次读的归属
        await _gate.WaitAsync(ct);
        try
        {
            var snaps = await _io.ReadMemberOrAdoptAsync<List<DraftSnapshot>>(
                DraftsPath(memberId), GetLegacyDraftsPath(), memberId,
                s => s.Count > 0, "Publish", ct);
            if (snaps is null) return [];

            var drafts = new List<PublishDraft>(snaps.Count);
            var dropped = 0;
            foreach (var s in snaps)
            {
                // 视频被移走/改名 → 这条草稿已经没意义，静默丢弃比留个必然失败的条目好
                if (string.IsNullOrWhiteSpace(s.VideoPath) || !File.Exists(s.VideoPath))
                {
                    dropped++;
                    continue;
                }
                drafts.Add(s.ToDraft());
            }
            if (dropped > 0)
                Log.Debug("[Publish] 恢复草稿：{Alive} 条有效，丢弃 {Dropped} 条（视频不存在）", drafts.Count, dropped);
            return drafts;
        }
        finally { _gate.Release(); }
    }

    /// <summary>整份覆盖写草稿。提交成功后调用方应把它们从集合里移除再存。</summary>
    public async Task SaveDraftsAsync(IEnumerable<PublishDraft> drafts,
        Guid? memberId = null, CancellationToken ct = default)
    {
        var path = DraftsPath(memberId ?? _memberId());
        var snaps = drafts.Select(DraftSnapshot.From).ToList();
        await _gate.WaitAsync(ct);
        try { await _io.WriteAsync(path, snaps, ct); }
        finally { _gate.Release(); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  草稿的序列化形状
    //
    //  PublishDraft 是 ObservableObject + ObservableCollection，不能直接反序列化；
    //  这里给一个纯数据镜像。**不要**图省事给 PublishDraft 加无参构造 + 可写属性 ——
    //  那会让「必填的 VideoPath」变成可选，整个模型失去约束。
    // ═══════════════════════════════════════════════════════════════

    private sealed record DraftSnapshot
    {
        public string VideoPath { get; init; } = "";
        public string? SourceFolder { get; init; }
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public List<string> Topics { get; init; } = [];
        public string? CoverPath { get; init; }
        /// <summary>封面帧（毫秒；1.4 新增）。null = 平台默认/首帧；老档缺这一列按 null 处理。</summary>
        public long? CoverFrameMs { get; init; }
        public bool IsScheduled { get; init; }
        public DateTime? ScheduledAt { get; init; }
        public PublishVisibility Visibility { get; init; }
        public bool AllowDownload { get; init; } = true;
        /// <summary>全局原创（2026-11-06 从账号级上提）。**可空**：null = 老档没有这一列，
        /// 用来与「新档明确存了 false」区分，只在 null 时做一次性回填。</summary>
        public bool? IsOriginal { get; init; }
        public List<string> VisibilityUserIds { get; init; } = [];
        public ContentDeclaration Declaration { get; init; } = new();
        public List<TargetSnapshot> Targets { get; init; } = [];
        public List<string> DisabledTargets { get; init; } = [];
        public Dictionary<string, PlatformParams> Overrides { get; init; } = [];

        public static DraftSnapshot From(PublishDraft d) => new()
        {
            VideoPath = d.VideoPath,
            SourceFolder = d.SourceFolder,
            Title = d.Title,
            Description = d.Description,
            Topics = [.. d.Topics],
            CoverPath = d.CoverPath,
            CoverFrameMs = d.CoverFrameMs,
            IsScheduled = d.IsScheduled,
            ScheduledAt = d.ScheduledAt,
            Visibility = d.Visibility,
            AllowDownload = d.AllowDownload,
            IsOriginal = d.IsOriginal,
            VisibilityUserIds = [.. d.VisibilityUserIds],
            Declaration = d.Declaration,
            Targets = [.. d.Targets.Select(t => new TargetSnapshot
            {
                PlatformKey = t.PlatformKey,
                SessionId = t.SessionId,
                AccountLabel = t.AccountLabel,
                IsPrimary = t.IsPrimary,
            })],
            // 只存被覆盖过的目标 —— 跟随预设的不存，预设改了草稿自动跟着变
            DisabledTargets = [.. d.DisabledTargets],
            Overrides = d.Overrides
                .Where(kv => kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
        };

        public PublishDraft ToDraft()
        {
            // 一次性迁移（2026-11-06）：老档的原创存在各 target 的 PlatformParams.IsOriginal 上，
            // 新版是全局字段。**只在老档（字段缺失 = null）时回填**，新档明确存了 false 的不动。
            var legacyOriginal = IsOriginal is null && Overrides.Values.Any(p => p?.IsOriginal == true);
            if (legacyOriginal)
            {
                foreach (var p in Overrides.Values)
                    if (p is not null) p.IsOriginal = null;
            }

            var d = new PublishDraft(VideoPath, SourceFolder)
            {
                Title = Title,
                Description = Description,
                CoverPath = CoverPath,
                CoverFrameMs = CoverFrameMs,
                IsScheduled = IsScheduled,
                ScheduledAt = ScheduledAt,
                Visibility = Visibility,
                AllowDownload = AllowDownload,
                IsOriginal = IsOriginal ?? legacyOriginal,
                Declaration = Declaration,
            };
            foreach (var t in Topics) d.Topics.Add(t);
            foreach (var u in VisibilityUserIds) d.VisibilityUserIds.Add(u);
            foreach (var t in Targets)
                d.Targets.Add(new PublishTarget(t.PlatformKey, t.SessionId, t.AccountLabel)
                {
                    IsPrimary = t.IsPrimary,
                });
            foreach (var k in DisabledTargets) d.DisabledTargets.Add(k);
            foreach (var (k, v) in Overrides) d.Overrides[k] = v;

            // 话题单一来源（2026-11-06）：旧草稿的账号预设话题只存在于 Overrides[].Topics，
            // 不在 draft.Topics 里 —— 视频号只读后者，会静默漏发。加载时并回全局列表。
            // 结构化话题（2.6）同理：旧档从 Topics 迁移出 TopicRefs，名字也要回填全局芯片区；
            // 活动关联话题（若预设里存了）一并回填。
            foreach (var p in d.Overrides.Values)
            {
                if (p is null) continue;
                p.MigrateLegacyTopics();
                // 2.6.4：旧「官方活动 ID」升成活动快照（与预设路径同口径）
                p.MigrateLegacyActivities();

                foreach (var topic in p.Topics)
                    if (topic.Name.Length > 0 && !d.Topics.Contains(topic.Name)) d.Topics.Add(topic.Name);
                foreach (var topic in p.TopicRefs)
                    if (topic.Name.Length > 0 && !d.Topics.Contains(topic.Name)) d.Topics.Add(topic.Name);
                foreach (var topic in p.Activities.SelectMany(a => a.Topics))
                    if (topic.Name.Length > 0 && !d.Topics.Contains(topic.Name)) d.Topics.Add(topic.Name);
            }
            return d;
        }
    }

    private sealed record TargetSnapshot
    {
        public string PlatformKey { get; init; } = "";
        public Guid SessionId { get; init; }
        public string AccountLabel { get; init; } = "";
        public bool IsPrimary { get; init; }
    }
}
