namespace OrderProcessing.BuildingBlocks.Application;

/// <summary>
/// A fact published by one module for others to react to. Integration events are public contracts:
/// they live in a module's Contracts project and evolve additively (see docs/api/integration-events.md).
/// </summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}

public abstract record IntegrationEvent : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Delivery is at-least-once, so every handler must be idempotent.
/// </summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes an event to the message broker, now. There is no outbox (ADR-0012): every handler is
/// started by a message, so if publishing fails the handler fails, the broker delivers its message
/// again and the handler publishes again. That only works if handlers follow two rules:
/// they decide what to publish from the state they find, not from whether they just changed it,
/// and every event has a deterministic id (<see cref="IntegrationEventIds"/>) so the repeat is recognised.
/// </summary>
public interface IEventPublisher
{
    /// <summary>Returns when the broker has accepted the event. Throws if it has not.</summary>
    Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken);
}

public static class IntegrationEventIds
{
    /// <summary>
    /// Gives the event an id derived from what it says: the order and, where an order can have several
    /// events of this type, what tells them apart (a version, a carrier status). The same fact published
    /// twice then has the same id, so duplicate detection on the broker and the consumer's inbox remove
    /// the repeat. The time is not part of the id.
    /// </summary>
    public static TEvent WithIdFor<TEvent>(this TEvent integrationEvent, Guid orderId, object? discriminator = null)
        where TEvent : IntegrationEvent =>
        integrationEvent with { EventId = For(orderId, typeof(TEvent), discriminator) };

    public static Guid For(Guid orderId, Type eventType, object? discriminator = null)
    {
        var key = $"{orderId:N}|{eventType.FullName}|{discriminator}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }
}
