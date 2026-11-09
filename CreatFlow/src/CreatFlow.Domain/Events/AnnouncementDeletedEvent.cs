namespace CreatFlow.Domain.Events;

public record AnnouncementDeletedEvent(AnnouncementId AnnouncementId, string Title) : IDomainEvent;
