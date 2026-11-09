namespace CreatFlow.Domain.Events;

public record AdminDeletedEvent(AdminId AdminId, string Username) : IDomainEvent;
