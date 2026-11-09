namespace CreatFlow.Domain.ValueObjects;

public record AnnouncementId
{
    public Guid Value { get; }
    private AnnouncementId(Guid value) => Value = value;
    public static AnnouncementId Of(Guid value)
    {
        if (value == Guid.Empty)
            throw new DomainException("AnnouncementId cannot be empty.");
        return new AnnouncementId(value);
    }
}
