namespace CreatFlow.Application.Identity.Services;

/// <summary>
/// Application service for exception log management.
/// </summary>
public class ExceptionLogService
{
    private readonly IExceptionLogRepository _repository;
    private readonly IMemberRepository _memberRepository;

    public ExceptionLogService(IExceptionLogRepository repository, IMemberRepository memberRepository)
    {
        _repository = repository;
        _memberRepository = memberRepository;
    }

    /// <summary>
    /// Desktop app reports a crash/exception.
    /// </summary>
    public async Task ReportAsync(ReportExceptionRequest request, Guid? memberId, string? username, CancellationToken ct = default)
    {
        var log = ExceptionLog.Create(
            ExceptionLogId.New(),
            memberId is null || memberId == Guid.Empty ? null : MemberId.Of(memberId.Value),
            username,
            request.MachineId,
            request.Platform,
            request.OsVersion,
            request.AppVersion,
            request.ExceptionType,
            request.ExceptionMsg,
            request.StackTrace,
            request.LogContent,
            (ExceptionReportType)request.ReportType,
            request.UserNote,
            request.OccurredAt == default ? DateTime.UtcNow : request.OccurredAt);

        await _repository.AddAsync(log, ct);
    }

    /// <summary>
    /// Get paginated exception logs for admin panel.
    /// </summary>
    public async Task<(List<ExceptionLog> Items, int TotalCount)> GetPagedAsync(
        ExceptionLogStatusFilter? status,
        string? platform,
        string? search,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        return await _repository.GetPagedAsync(status, platform, search, page, pageSize, ct);
    }

    /// <summary>
    /// Update exception log status (admin action).
    /// </summary>
    public async Task UpdateStatusAsync(Guid logId, ExceptionLogStatus newStatus, Guid? adminId, CancellationToken ct = default)
    {
        var log = await _repository.GetByIdAsync(ExceptionLogId.Of(logId), ct);
        if (log is null)
            throw new NotFoundException("异常日志不存在");

        log.UpdateStatus(newStatus, adminId.HasValue ? AdminId.Of(adminId.Value) : null);
        await _repository.UpdateAsync(log, ct);
    }

    /// <summary>
    /// Get status counts for dashboard badges.
    /// </summary>
    public async Task<Dictionary<ExceptionLogStatus, int>> CountByStatusAsync(CancellationToken ct = default)
    {
        return await _repository.CountByStatusAsync(ct);
    }
}
