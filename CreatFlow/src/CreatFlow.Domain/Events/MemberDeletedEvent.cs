namespace CreatFlow.Domain.Events;

public record MemberDeletedEvent(MemberId MemberId, string Username) : IDomainEvent;
