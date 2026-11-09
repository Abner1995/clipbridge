namespace CreatFlow.Domain.ValueObjects;

public record CollectionId
{
    public Guid Value { get; }
    private CollectionId(Guid value) => Value = value;
    public static CollectionId Of(Guid value)
    {
        if (value == Guid.Empty)
            throw new DomainException("CollectionId cannot be empty.");
        return new CollectionId(value);
    }
    public static CollectionId New() => new(Guid.NewGuid());
}
