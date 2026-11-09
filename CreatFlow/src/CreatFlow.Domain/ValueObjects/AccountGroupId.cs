namespace CreatFlow.Domain.ValueObjects;

public record AccountGroupId
{
    public Guid Value { get; }
    private AccountGroupId(Guid value) => Value = value;
    public static AccountGroupId Of(Guid value)
    {
        if (value == Guid.Empty)
            throw new DomainException("AccountGroupId cannot be empty.");
        return new AccountGroupId(value);
    }
    public static AccountGroupId New() => new(Guid.NewGuid());
}
