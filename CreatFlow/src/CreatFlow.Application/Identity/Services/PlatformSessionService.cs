namespace CreatFlow.Application.Identity.Services;

/// <summary>
/// Manages platform sessions — login, logout, rename, primary selection, probe.
/// Called by desktop app via the PlatformSessionController.
/// </summary>
public class PlatformSessionService
{
    private readonly IPlatformSessionRepository _repo;
    private readonly IPlatformRepository _platformRepo;
    private readonly IPublishPlatformRepository _publishRepo;
    private readonly ICollectionPlatformRepository _collectionRepo;
    private readonly IPublishTaskRepository _taskRepo;
    private readonly IPlatformProber? _prober;

    public PlatformSessionService(
        IPlatformSessionRepository repo,
        IPlatformRepository platformRepo,
        IPublishPlatformRepository publishRepo,
        ICollectionPlatformRepository collectionRepo,
        IPublishTaskRepository taskRepo,
        IPlatformProber? prober = null)
    {
        _repo = repo;
        _platformRepo = platformRepo;
        _publishRepo = publishRepo;
        _collectionRepo = collectionRepo;
        _taskRepo = taskRepo;
        _prober = prober;
    }

    // ═══ List ═══

    /// <summary>
    /// Returns all sessions for a member, optionally filtered by platform.
    /// Never returns cookie — use GetById for that when needed.
    /// </summary>
    public async Task<List<PlatformSessionSummaryDto>> GetListAsync(
        Guid memberId, string? platformKey = null, CancellationToken ct = default)
    {
        var sessions = await _repo.GetByMemberAsync(memberId, platformKey, ct);
        return sessions.Select(ToSummaryDto).ToList();
    }

    // ═══ Sync (login / relogin) ═══

    /// <summary>
    /// Creates or updates a session after WebView2 login.
    /// - request.Id == null → new session (new account login)
    /// - request.Id != null → update existing (relogin)
    /// </summary>
    public async Task<PlatformSessionSyncResultDto> SyncAsync(
        Guid memberId, SyncPlatformSessionRequest request, CancellationToken ct = default)
    {
        // Lookup platform config for estimated valid days
        var platform = await _platformRepo.GetByKeyAsync(request.PlatformKey, ct);
        var estimatedValidDays = platform?.EstimatedValidDays ?? 3;

        if (request.Id.HasValue)
        {
            // ── 指定 ID（客户端新号建档 / 重登共用）──
            // 档案目录名 = 会话 ID,由客户端先行生成(WebView2 环境创建早于登录完成)。
            // 行不存在 → 首次登录完成,用该 ID 建档 —— 没有完成登录就不落库,
            // 不再有「预注册空壳 → 中途放弃留脏行」;行已存在(本会员)→ 更新凭据
            // (重登;或服务端记录被管理端删除后的同 ID 补建,档案目录重连)。
            var id = PlatformSessionId.Of(request.Id.Value);
            var existing = await _repo.GetByIdAndMemberAsync(id, memberId, ct);
            if (existing != null)
            {
                existing.SyncCredentials(
                    request.Cookie,
                    request.LocalStorageJson,
                    request.PlatformUid,
                    request.PlatformName,
                    request.PlatformAvatar,
                    request.ExtraDataJson,
                    estimatedValidDays);

                await _repo.SaveChangesAsync(ct);
                return ToSyncResultDto(existing);
            }

            // 同 ID 属他人(随机 Guid 冲突趋零):不越权占用,按不存在报错,客户端重试即换新 ID
            var clash = await _repo.GetByIdAsync(id, ct);
            if (clash != null)
                throw new NotFoundException($"会话 {request.Id} 不存在");

            return ToSyncResultDto(await CreateCoreAsync(
                memberId, request, id, estimatedValidDays, ct));
        }

        // ── 未指定 ID:服务端自生成建档(兼容路径)──
        return ToSyncResultDto(await CreateCoreAsync(
            memberId, request, PlatformSessionId.New(), estimatedValidDays, ct));
    }

    /// <summary>建档公共路径:该平台首个会话自动设为默认</summary>
    private async Task<PlatformSession> CreateCoreAsync(
        Guid memberId, SyncPlatformSessionRequest request, PlatformSessionId id,
        int estimatedValidDays, CancellationToken ct)
    {
        var existing = await _repo.GetByMemberAsync(memberId, request.PlatformKey, ct);

        var session = PlatformSession.Create(
            id,
            memberId,
            request.PlatformKey,
            request.Cookie,
            request.LocalStorageJson,
            request.PlatformUid,
            request.PlatformName,
            request.PlatformAvatar,
            request.ExtraDataJson,
            estimatedValidDays);

        if (existing.Count == 0)
            session.SetPrimary(true);

        await _repo.AddAsync(session, ct);
        return session;
    }

    // ═══ Rename ═══

    public async Task RenameAsync(
        Guid memberId, RenamePlatformSessionRequest request, CancellationToken ct = default)
    {
        var session = await _repo.GetByIdAndMemberAsync(
            PlatformSessionId.Of(request.Id), memberId, ct)
            ?? throw new NotFoundException($"会话 {request.Id} 不存在");

        session.Rename(request.AccountLabel);
        await _repo.SaveChangesAsync(ct);
    }

    // ═══ Set Primary ═══

    public async Task SetPrimaryAsync(
        Guid memberId, SetPrimaryRequest request, CancellationToken ct = default)
    {
        var session = await _repo.GetByIdAndMemberAsync(
            PlatformSessionId.Of(request.Id), memberId, ct)
            ?? throw new NotFoundException($"会话 {request.Id} 不存在");

        // Clear other primaries on the same platform
        await _repo.ClearPrimaryAsync(memberId, session.PlatformKey, session.Id, ct);

        session.SetPrimary(true);
        await _repo.SaveChangesAsync(ct);
    }

    // ═══ Delete（解绑）═══

    /// <summary>
    /// 解绑 = 硬删该账号在本端的全部遗留（2026-10-10 拍板）。旧实现是「清引用置 NULL + 记录保留」，
    /// 留下一地连不上的孤儿行 —— 队列会去执行它们并报「请重新登录」，重登也救不了（新会话新 ID）。
    ///
    /// 删除范围：该会话的全部发布记录（所有状态，含 hidden_in_queue 软删行）+ 全部合集记录
    /// + 被本次删空的发布任务。**平台侧作品与合集一律不碰** —— 本路径不调任何平台 SDK。
    ///
    /// 顺序纪律：会话行**最后**删 —— 无事务（全库如此），中途失败直接重试：已删的各步幂等
    /// （按会话删不存在的行 = 0 条），会话还在就能接着清。空任务清扫只认「本次删空的任务」
    /// （同任务还有别的账号的记录 → 保留，规则同 PublishService.DeletePlatformAsync）；
    /// 若在「删完记录、清任务前」崩溃，空任务行会残留在库里（无任何界面可见），不追补。
    /// </summary>
    public async Task<PlatformSessionDeleteResultDto> DeleteAsync(
        Guid memberId, DeletePlatformSessionRequest request, CancellationToken ct = default)
    {
        var session = await _repo.GetByIdAndMemberAsync(
            PlatformSessionId.Of(request.Id), memberId, ct)
            ?? throw new NotFoundException($"会话 {request.Id} 不存在");

        var (publishDeleted, affectedTaskIds) =
            await _publishRepo.DeleteBySessionAsync(session.Id.Value, ct);
        var collectionDeleted = await _collectionRepo.DeleteBySessionAsync(session.Id.Value, ct);

        foreach (var taskId in affectedTaskIds)
        {
            var remaining = await _publishRepo.GetByTaskAsync(taskId, ct);
            if (remaining.Count > 0) continue;

            var task = await _taskRepo.GetByIdAndMemberAsync(taskId, memberId, ct);
            if (task is not null)
                await _taskRepo.DeleteAsync(task, ct);   // Remove + SaveChanges 一体
        }

        await _repo.DeleteAsync(session, ct);

        return new PlatformSessionDeleteResultDto(publishDeleted, collectionDeleted);
    }

    // ═══ Get Credentials (for desktop SDK calls) ═══

    /// <summary>
    /// Returns the full session credentials (cookie + extraDataJson) needed
    /// for the desktop app to call platform SDKs directly.
    /// Only returns credentials owned by the requesting member.
    /// </summary>
    public async Task<PlatformSessionCredentialsDto> GetCredentialsAsync(
        Guid memberId, Guid sessionId, CancellationToken ct = default)
    {
        // 空 Guid = 调用方手里那条记录没有会话（老数据行 / 已解绑）——按「会话不存在」收。
        // 不能让它落进 PlatformSessionId.Of 抛 DomainException：那是 422 + 一整句 Domain 黑话，
        // 而客户端各处（视频 / 合集删除）都把 404 当成「账号已解绑」优雅收口 ——
        // 接不上的话整条删除任务会卡死（2026-10-14 用户实测：合集「全选删除」整批失败）。
        if (sessionId == Guid.Empty)
            throw new NotFoundException($"会话 {sessionId} 不存在");

        var session = await _repo.GetByIdAndMemberAsync(
            PlatformSessionId.Of(sessionId), memberId, ct)
            ?? throw new NotFoundException($"会话 {sessionId} 不存在");

        return new PlatformSessionCredentialsDto(
            Id: session.Id.Value,
            PlatformKey: session.PlatformKey,
            Cookie: session.Cookie,
            ExtraDataJson: session.ExtraDataJson,
            LocalStorageJson: session.LocalStorageJson,
            PlatformUid: session.PlatformUid,
            PlatformName: session.PlatformName
        );
    }

    // ═══ Probe ═══

    /// <summary>
    /// Probes all sessions for a member's platform(s).
    /// - Local check: compare ExpiredAt with current time (always runs)
    /// - Remote check: HTTP probe against the actual platform (douyin only for now)
    /// </summary>
    public async Task<List<ProbeResultDto>> ProbeAsync(
        Guid memberId, string? platformKey = null, CancellationToken ct = default)
    {
        var sessions = await _repo.GetByMemberAsync(memberId, platformKey, ct);
        var results = new List<ProbeResultDto>();

        foreach (var session in sessions)
        {
            // ── Local expiry check ──
            if (session.Status == PlatformSessionStatus.Active && session.ExpiredAt.HasValue)
            {
                var remaining = session.ExpiredAt.Value - Clock.Now;
                if (remaining.TotalHours <= 0)
                {
                    session.UpdateProbeResult(PlatformSessionStatus.Expired, "expired");
                }
                else if (remaining.TotalHours < 24)
                {
                    session.UpdateProbeResult(PlatformSessionStatus.ExpiringSoon, "ok");
                }
            }

            // ── Remote HTTP probe (if prober available) ──
            if (_prober != null && session.Status != PlatformSessionStatus.Expired)
            {
                try
                {
                    var (status, result) = await _prober.ProbeAsync(session, ct);
                    session.UpdateProbeResult(status, result);
                }
                catch
                {
                    // HTTP probe failure → keep local check result, don't override
                }
            }

            results.Add(new ProbeResultDto(
                session.Id.Value,
                (int)session.Status,
                session.ProbedResult));
        }

        if (results.Count > 0)
            await _repo.SaveChangesAsync(ct);

        return results;
    }

    // ═══ Mappers ═══

    private static PlatformSessionSummaryDto ToSummaryDto(PlatformSession s) => new(
        Id: s.Id.Value,
        PlatformKey: s.PlatformKey,
        AccountLabel: s.AccountLabel,
        IsPrimary: s.IsPrimary,
        PlatformUid: s.PlatformUid,
        PlatformName: s.PlatformName,
        PlatformAvatar: s.PlatformAvatar,
        Status: (int)s.Status,
        StatusText: StatusToText(s.Status),
        LoggedInAt: s.LoggedInAt,
        ExpiredAt: s.ExpiredAt,
        ProbedAt: s.ProbedAt,
        ProbedResult: s.ProbedResult
    );

    private static PlatformSessionSyncResultDto ToSyncResultDto(PlatformSession s) => new(
        Id: s.Id.Value,
        PlatformKey: s.PlatformKey,
        AccountLabel: s.AccountLabel,
        IsPrimary: s.IsPrimary,
        PlatformUid: s.PlatformUid,
        PlatformName: s.PlatformName,
        PlatformAvatar: s.PlatformAvatar,
        Status: (int)s.Status,
        StatusText: StatusToText(s.Status),
        LoggedInAt: s.LoggedInAt,
        ExpiredAt: s.ExpiredAt
    );

    private static string StatusToText(PlatformSessionStatus status) => status switch
    {
        PlatformSessionStatus.Active => "有效",
        PlatformSessionStatus.ExpiringSoon => "即将过期",
        PlatformSessionStatus.Expired => "已过期",
        _ => "未知"
    };
}
