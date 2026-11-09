namespace CreatFlow.Domain.ValueObjects;

public record PublishPlatformId
{
    public Guid Value { get; }
    private PublishPlatformId(Guid value) => Value = value;
    public static PublishPlatformId Of(Guid value)
    {
        if (value == Guid.Empty)
            throw new DomainException("PublishPlatformId cannot be empty.");
        return new PublishPlatformId(value);
    }
    public static PublishPlatformId New() => new(Guid.NewGuid());
}
