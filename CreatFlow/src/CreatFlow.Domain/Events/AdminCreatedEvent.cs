namespace CreatFlow.Domain.Events;

public record AdminCreatedEvent(AdminId AdminId, string Username) : IDomainEvent;
