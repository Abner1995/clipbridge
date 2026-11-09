namespace CreatFlow.Domain.ValueObjects;

public record AdminId
{
    public Guid Value { get; }
    private AdminId(Guid value) => Value = value;
    public static AdminId Of(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException("AdminId cannot be empty.");
        }

        return new AdminId(value);
    }
}
