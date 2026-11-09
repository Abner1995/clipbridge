namespace CreatFlow.Domain.Events;

public sealed record DingTalkConfigCreatedEvent(DingTalkConfigId ConfigId, MemberId MemberId) : IDomainEvent;
