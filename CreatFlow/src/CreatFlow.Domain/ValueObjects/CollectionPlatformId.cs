namespace CreatFlow.Domain.ValueObjects;

public record CollectionPlatformId
{
    public Guid Value { get; }
    private CollectionPlatformId(Guid value) => Value = value;
    public static CollectionPlatformId Of(Guid value)
    {
        if (value == Guid.Empty)
            throw new DomainException("CollectionPlatformId cannot be empty.");
        return new CollectionPlatformId(value);
    }
    public static CollectionPlatformId New() => new(Guid.NewGuid());
}
