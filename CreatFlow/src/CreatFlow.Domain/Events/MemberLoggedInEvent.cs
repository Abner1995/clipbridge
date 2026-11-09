namespace CreatFlow.Domain.Events;

/// <summary>
/// 会员登录时触发的领域事件（用于记录登录IP和时间）。
/// </summary>
public sealed record MemberLoggedInEvent(MemberId MemberId, string Username, string? IpAddress, DateTime LoginTime) : IDomainEvent;
