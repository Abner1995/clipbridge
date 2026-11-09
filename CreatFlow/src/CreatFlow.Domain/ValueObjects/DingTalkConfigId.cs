namespace CreatFlow.Domain.ValueObjects;

/// <summary>
/// Strongly-typed ID for DingTalkConfig.
/// </summary>
public sealed record DingTalkConfigId
{
    public Guid Value { get; }

    private DingTalkConfigId(Guid value) => Value = value;

    public static DingTalkConfigId Of(Guid value) =>
        value == Guid.Empty
            ? throw new DomainException("DingTalkConfigId 不能为空。")
            : new DingTalkConfigId(value);

    public static DingTalkConfigId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
