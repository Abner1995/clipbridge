namespace CreatFlow.Domain.ValueObjects;

public record AuditLogId
{
    public Guid Value { get; }
    private AuditLogId(Guid value) => Value = value;
    public static AuditLogId Of(Guid value)
    {
        if (value == Guid.Empty)
            throw new DomainException("AuditLogId cannot be empty.");
        return new AuditLogId(value);
    }
}
