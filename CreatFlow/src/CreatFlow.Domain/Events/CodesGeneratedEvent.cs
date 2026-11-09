namespace CreatFlow.Domain.Events;

public record CodesGeneratedEvent(int Count, int DurationDays, AdminId CreatorId) : IDomainEvent;
