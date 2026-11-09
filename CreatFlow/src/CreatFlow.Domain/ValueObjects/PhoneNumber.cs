namespace CreatFlow.Domain.ValueObjects;

public sealed record PhoneNumber
{
    public string Number { get; }

    private PhoneNumber(string number) { Number = number; }

    public static PhoneNumber Of(string number)
    {
        ArgumentNullException.ThrowIfNull(number, nameof(number));

        // Remove whitespace so "138 1234 5678" works
        var cleaned = System.Text.RegularExpressions.Regex.Replace(number, @"\s+", "");

        if (cleaned.Length != 11 || !System.Text.RegularExpressions.Regex.IsMatch(cleaned, @"^1[3-9]\d{9}$"))
        {
            throw new DomainException($"手机号格式错误: {number}");
        }

        return new PhoneNumber(cleaned);
    }

    public static implicit operator string(PhoneNumber p) => p.Number;

    public override string ToString() => Number;
}
