namespace CreatFlow.Domain.Shared.Interfaces;

/// <summary>
/// Generic repository interface for aggregate roots.
/// TId captures the aggregate's strong-typed ID (MemberId, ActivationCodeId, etc.)
/// Implementation lives in Infrastructure layer.
/// </summary>
public interface IRepository<T, in TId> where T : IAggregate<TId>
{
    Task<T?> GetByIdAsync(TId id, CancellationToken cancellationToken = default);
    Task AddAsync(T aggregate, CancellationToken cancellationToken = default);
    Task UpdateAsync(T aggregate, CancellationToken cancellationToken = default);
    Task DeleteAsync(T aggregate, CancellationToken cancellationToken = default);
}
