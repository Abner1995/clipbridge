namespace CreatFlow.Domain.ValueObjects;

/// <summary>
/// Strongly-typed ID for ExceptionLog.
/// </summary>
public sealed record ExceptionLogId
{
    public Guid Value { get; }

    private ExceptionLogId(Guid value) => Value = value;

    public static ExceptionLogId Of(Guid value) =>
        value == Guid.Empty
            ? throw new DomainException("ExceptionLogId 不能为空。")
            : new ExceptionLogId(value);

    public static ExceptionLogId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
