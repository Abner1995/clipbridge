namespace CreatFlow.Domain.Events;

public record AnnouncementCreatedEvent(AnnouncementId AnnouncementId, string Title, AnnouncementType Type) : IDomainEvent;
