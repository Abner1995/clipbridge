namespace CreatFlow.Domain.Entities;

/// <summary>
/// 客户端异常日志实体。由桌面端自动或手动上报，管理员在后台查看处理。
/// 不是聚合根——它属于 Member 的上下文，但允许 member_id 为空（未登录崩溃）。
/// </summary>
public class ExceptionLog : Entity<ExceptionLogId>
{
    /// <summary>关联会员（可空，未登录崩溃时为空）</summary>
    public MemberId? MemberId { get; private set; }

    /// <summary>冗余用户名，方便后台显示</summary>
    public string? Username { get; private set; }

    /// <summary>设备指纹</summary>
    public string? MachineId { get; private set; }

    /// <summary>操作系统平台</summary>
    public string Platform { get; private set; } = default!;

    /// <summary>操作系统版本</summary>
    public string? OsVersion { get; private set; }

    /// <summary>客户端版本</summary>
    public string? AppVersion { get; private set; }

    /// <summary>异常类型（如 System.NullReferenceException）</summary>
    public string ExceptionType { get; private set; } = default!;

    /// <summary>异常消息</summary>
    public string? ExceptionMsg { get; private set; }

    /// <summary>完整堆栈</summary>
    public string? StackTrace { get; private set; }

    /// <summary>附加上下文日志</summary>
    public string? LogContent { get; private set; }

    /// <summary>上报方式：0=自动, 1=手动</summary>
    public ExceptionReportType ReportType { get; private set; }

    /// <summary>用户手动提交时的描述</summary>
    public string? UserNote { get; private set; }

    /// <summary>处理状态</summary>
    public ExceptionLogStatus Status { get; private set; }

    /// <summary>处理人 admin_id</summary>
    public AdminId? ResolvedBy { get; private set; }

    /// <summary>处理时间</summary>
    public DateTime? ResolvedAt { get; private set; }

    /// <summary>客户端崩溃时间（不是入库时间）</summary>
    public DateTime OccurredAt { get; private set; }

    private ExceptionLog() { }

    public static ExceptionLog Create(
        ExceptionLogId id,
        MemberId? memberId,
        string? username,
        string? machineId,
        string platform,
        string? osVersion,
        string? appVersion,
        string exceptionType,
        string? exceptionMsg,
        string? stackTrace,
        string? logContent,
        ExceptionReportType reportType,
        string? userNote,
        DateTime occurredAt)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(exceptionType);

        var log = new ExceptionLog
        {
            Id = id,
            MemberId = memberId,
            Username = username,
            MachineId = machineId,
            Platform = platform,
            OsVersion = osVersion,
            AppVersion = appVersion,
            ExceptionType = exceptionType,
            ExceptionMsg = exceptionMsg,
            StackTrace = stackTrace,
            LogContent = logContent,
            ReportType = reportType,
            UserNote = userNote,
            Status = ExceptionLogStatus.Unread,
            OccurredAt = occurredAt,
            CreatedAt = Clock.Now,
            LastModified = Clock.Now
        };

        return log;
    }

    /// <summary>标记处理状态</summary>
    public void UpdateStatus(ExceptionLogStatus newStatus, AdminId? resolvedBy = null)
    {
        Status = newStatus;
        LastModified = Clock.Now;

        if (newStatus == ExceptionLogStatus.Resolved || newStatus == ExceptionLogStatus.Ignored)
        {
            ResolvedBy = resolvedBy;
            ResolvedAt = Clock.Now;
        }
    }
}

/// <summary>异常上报方式</summary>
public enum ExceptionReportType
{
    Auto = 0,   // 自动捕获上报
    Manual = 1  // 用户手动提交
}

/// <summary>异常日志处理状态</summary>
public enum ExceptionLogStatus
{
    Unread = 0,     // 未处理
    InProgress = 1, // 处理中
    Resolved = 2,   // 已解决
    Ignored = 3     // 已忽略
}
