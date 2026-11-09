namespace CreatFlow.Application.DTOs;

/// <summary>
/// Read model for the admin dashboard.
/// </summary>
public sealed record DashboardStatsDto(
    int TotalMembers,
    int ActiveMembers,
    int TodayNew,
    int ExpiringSoon,
    List<ExpiringMemberDto> ExpiringMembers);

public sealed record ExpiringMemberDto(
    string Username,
    string PhoneNumber,
    DateTime ExpireDate,
    int RemainingDays);
