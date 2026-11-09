namespace CreatFlow.Domain.Events;

public record MemberEnabledEvent(MemberId MemberId) : IDomainEvent;
