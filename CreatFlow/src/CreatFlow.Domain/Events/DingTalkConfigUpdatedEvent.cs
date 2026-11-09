namespace CreatFlow.Domain.Events;

public sealed record DingTalkConfigUpdatedEvent(DingTalkConfigId ConfigId, MemberId MemberId) : IDomainEvent;
