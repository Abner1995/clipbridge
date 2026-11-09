namespace CreatFlow.Application.Common.Interfaces;

/// <summary>
/// Handles a specific type of domain event.
/// Each handler is resolved from DI by DomainEventDispatcher.
/// </summary>
public interface IDomainEventHandler<in T> where T : IDomainEvent
{
    Task HandleAsync(T domainEvent, CancellationToken cancellationToken = default);
}
