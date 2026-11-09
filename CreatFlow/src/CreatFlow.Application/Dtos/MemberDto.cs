namespace CreatFlow.Application.DTOs;

/// <summary>
/// Read-only snapshot of a Member for the outside world.
/// Stripped of password hash and internal state.
/// </summary>
public record MemberDto(Guid Id, string Username, DateTime ExpireDate, bool IsActive, string? LastLoginIp = null, DateTime? LastLoginAt = null);
