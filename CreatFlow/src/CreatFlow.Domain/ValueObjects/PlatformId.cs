namespace CreatFlow.Domain.ValueObjects;

public record PlatformId
{
    public Guid Value { get; }
    private PlatformId(Guid value) => Value = value;
    public static PlatformId Of(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException("PlatformId cannot be empty.");
        }

        return new PlatformId(value);
    }
}
