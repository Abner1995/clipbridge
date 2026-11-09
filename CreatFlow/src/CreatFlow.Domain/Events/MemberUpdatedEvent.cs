namespace CreatFlow.Domain.Events;

public record MemberUpdatedEvent(MemberId MemberId, string Username) : IDomainEvent;
