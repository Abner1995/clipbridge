namespace CreatFlow.Domain.ValueObjects;

public record SystemConfigId
{
    public Guid Value { get; }
    private SystemConfigId(Guid value) => Value = value;
    public static SystemConfigId Of(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException("SystemConfigId cannot be empty.");
        }

        return new SystemConfigId(value);
    }
}
