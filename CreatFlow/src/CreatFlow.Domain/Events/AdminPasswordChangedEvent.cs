namespace CreatFlow.Domain.Events;

public record AdminPasswordChangedEvent(AdminId AdminId, string Username) : IDomainEvent;
