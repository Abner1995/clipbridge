namespace CreatFlow.Domain.Events;

public record MemberDisabledEvent(MemberId MemberId) : IDomainEvent;
