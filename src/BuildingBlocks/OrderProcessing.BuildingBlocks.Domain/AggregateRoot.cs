namespace OrderProcessing.BuildingBlocks.Domain;

/// <summary>Something that happened inside an aggregate. Never leaves the owning module.</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

public abstract class Entity<TId>
    where TId : notnull
{
    protected Entity(TId id) => Id = id;

    public TId Id { get; }
}

/// <summary>
/// Consistency boundary: one aggregate is loaded, changed and saved per transaction.
/// </summary>
public abstract class AggregateRoot<TId>(TId id) : Entity<TId>(id)
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>Optimistic concurrency token, incremented by the persistence layer on every save.</summary>
    public long Version { get; protected set; }

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}
