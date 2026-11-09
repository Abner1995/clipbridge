namespace CreatFlow.Domain.ValueObjects;

public sealed record Username
{
    public string Value { get; }

    public Username(string value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(value));

        var trimmed = value.Trim();
        if (trimmed.Length < 3 || trimmed.Length > 50)
        {
            throw new DomainException($"Username must be between 3 and 50 characters. Got: {trimmed.Length}");
        }

        Value = trimmed.ToLowerInvariant();
    }

    public static implicit operator string(Username username) => username.Value;
    public override string ToString() => Value;
}
