namespace CreatFlow.Domain.ValueObjects;

public record AsyncTaskItemId
{
    public Guid Value { get; }
    private AsyncTaskItemId(Guid value) => Value = value;
    public static AsyncTaskItemId Of(Guid value)
    {
        if (value == Guid.Empty)
            throw new DomainException("AsyncTaskItemId cannot be empty.");
        return new AsyncTaskItemId(value);
    }
    public static AsyncTaskItemId New() => new(Guid.NewGuid());
}
