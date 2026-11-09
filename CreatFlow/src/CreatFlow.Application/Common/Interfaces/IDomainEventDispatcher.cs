namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Dispatches domain events to their registered handlers after SaveChanges.
/// Implemented in Infrastructure layer.
/// </summary>
public interface IDomainEventDispatcher
{
    Task DispatchAsync(IReadOnlyList<IDomainEvent> domainEvents, CancellationToken cancellationToken = default);
}
