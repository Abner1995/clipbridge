namespace CreatFlow.Application.DTOs;

/// <summary>
/// Read model for exception log list.
/// </summary>
public sealed record ExceptionLogDto(
    Guid Id,
    Guid? MemberId,
    string? Username,
    string? MachineId,
    string Platform,
    string? OsVersion,
    string? AppVersion,
    string ExceptionType,
    string? ExceptionMsg,
    string? StackTrace,
    string? LogContent,
    int ReportType,
    string? UserNote,
    int Status,
    DateTime OccurredAt,
    DateTime CreatedAt);

/// <summary>
/// Request from desktop app to report a crash.
/// </summary>
public sealed record ReportExceptionRequest(
    string? MachineId,
    string Platform,
    string? OsVersion,
    string? AppVersion,
    string ExceptionType,
    string? ExceptionMsg,
    string? StackTrace,
    string? LogContent,
    int ReportType,
    string? UserNote,
    DateTime OccurredAt);

/// <summary>
/// Status filter for exception logs.
/// </summary>
public enum ExceptionLogStatusFilter
{
    All = 0,
    Unread = 1,
    InProgress = 2,
    Resolved = 3,
    Ignored = 4
}
