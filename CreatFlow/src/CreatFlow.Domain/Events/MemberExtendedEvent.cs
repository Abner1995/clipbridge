namespace CreatFlow.Domain.Events;

public record MemberExtendedEvent(MemberId MemberId, DateTime NewExpireDate) : IDomainEvent;
