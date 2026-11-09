namespace CreatFlow.Domain.ValueObjects;

public record MemberId
{
    public Guid Value { get; }
    private MemberId(Guid value) => Value = value;
    public static MemberId Of(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException("MemberId cannot be empty.");
        }

        return new MemberId(value);
    }
}
