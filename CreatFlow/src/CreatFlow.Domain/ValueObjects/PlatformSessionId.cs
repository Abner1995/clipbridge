namespace CreatFlow.Domain.ValueObjects;

public record PlatformSessionId
{
    public Guid Value { get; }
    private PlatformSessionId(Guid value) => Value = value;
    public static PlatformSessionId Of(Guid value)
    {
        if (value == Guid.Empty)
            throw new DomainException("PlatformSessionId cannot be empty.");
        return new PlatformSessionId(value);
    }
    public static PlatformSessionId New() => new(Guid.NewGuid());
}
