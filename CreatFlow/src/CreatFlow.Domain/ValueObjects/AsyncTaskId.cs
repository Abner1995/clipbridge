namespace CreatFlow.Domain.ValueObjects;

public record AsyncTaskId
{
    public Guid Value { get; }
    private AsyncTaskId(Guid value) => Value = value;
    public static AsyncTaskId Of(Guid value)
    {
        if (value == Guid.Empty)
            throw new DomainException("AsyncTaskId cannot be empty.");
        return new AsyncTaskId(value);
    }
    public static AsyncTaskId New() => new(Guid.NewGuid());
}
