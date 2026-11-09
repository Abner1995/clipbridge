namespace CreatFlow.Domain.ValueObjects;

public record PublishTaskId
{
    public Guid Value { get; }
    private PublishTaskId(Guid value) => Value = value;
    public static PublishTaskId Of(Guid value)
    {
        if (value == Guid.Empty)
            throw new DomainException("PublishTaskId cannot be empty.");
        return new PublishTaskId(value);
    }
    public static PublishTaskId New() => new(Guid.NewGuid());
}
