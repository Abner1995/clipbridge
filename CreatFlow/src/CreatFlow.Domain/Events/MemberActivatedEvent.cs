namespace CreatFlow.Domain.Events;

public record MemberActivatedEvent(MemberId MemberId, DateTime ExpireDate) : IDomainEvent;
