namespace CreatFlow.Domain.ValueObjects;

/// <summary>
/// Strongly-typed ID for NotificationLog.
/// </summary>
public sealed record NotificationLogId
{
    public Guid Value { get; }

    private NotificationLogId(Guid value) => Value = value;

    public static NotificationLogId Of(Guid value) =>
        value == Guid.Empty
            ? throw new DomainException("NotificationLogId 不能为空。")
            : new NotificationLogId(value);

    public static NotificationLogId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
