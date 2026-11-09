namespace CreatFlow.Domain.Events;

public sealed record DingTalkConfigDeletedEvent(DingTalkConfigId ConfigId, MemberId MemberId) : IDomainEvent;
