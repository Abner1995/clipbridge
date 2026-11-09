namespace CreatFlow.Domain.Events;

public record AdminUpdatedEvent(AdminId AdminId, string Username) : IDomainEvent;
