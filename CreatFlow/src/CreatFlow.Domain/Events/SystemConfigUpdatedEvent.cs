namespace CreatFlow.Domain.Events;

public record SystemConfigUpdatedEvent(SystemConfigId ConfigId, string Key, string Category) : IDomainEvent;
