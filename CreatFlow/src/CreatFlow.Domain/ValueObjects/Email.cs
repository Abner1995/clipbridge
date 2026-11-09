namespace CreatFlow.Domain.ValueObjects;

public sealed record Email
{
    public string Address { get; }

    private Email(string address) { Address = address; }

    public static Email Of(string address)
    {
        ArgumentNullException.ThrowIfNull(address, nameof(address));

        var cleaned = address.Trim().ToLowerInvariant();

        if (!System.Text.RegularExpressions.Regex.IsMatch(cleaned, @"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$"))
        {
            throw new DomainException($"邮箱格式错误: {address}");
        }

        return new Email(cleaned);
    }

    public static implicit operator string(Email e) => e.Address;

    public override string ToString() => Address;
}
