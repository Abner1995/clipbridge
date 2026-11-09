namespace CreatFlow.Domain.Events;

public record AnnouncementUpdatedEvent(AnnouncementId AnnouncementId, string Title) : IDomainEvent;
