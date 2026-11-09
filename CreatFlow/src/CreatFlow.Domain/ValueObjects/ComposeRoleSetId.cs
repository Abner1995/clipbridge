namespace CreatFlow.Domain.ValueObjects;

public record ComposeRoleSetId
{
    public Guid Value { get; }
    private ComposeRoleSetId(Guid value) => Value = value;
    public static ComposeRoleSetId Of(Guid value)
    {
        if (value == Guid.Empty)
            throw new DomainException("ComposeRoleSetId cannot be empty.");
        return new ComposeRoleSetId(value);
    }
    public static ComposeRoleSetId New() => new(Guid.NewGuid());
}
