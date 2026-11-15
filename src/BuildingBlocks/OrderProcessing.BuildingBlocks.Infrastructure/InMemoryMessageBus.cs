using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderProcessing.BuildingBlocks.Application;

namespace OrderProcessing.BuildingBlocks.Infrastructure;

/// <summary>
/// SKELETON PLACEHOLDER for the consumer's inbox table (ADR-0012): the ids of the messages each consumer
/// has already handled. The persistent inbox writes the id in the same transaction as the consumer's own
/// state change; in memory the id is recorded before the handler runs and removed again if it fails.
/// </summary>
internal sealed class InMemoryInbox
{
    private readonly ConcurrentDictionary<(string Consumer, Guid MessageId), bool> _seen = new();

    /// <returns>False if this consumer has already handled the message.</returns>
    public bool TryRecord(string consumer, Guid messageId) => _seen.TryAdd((consumer, messageId), true);

    public void Forget(string consumer, Guid messageId) => _seen.TryRemove((consumer, messageId), out _);
}

/// <summary>
/// SKELETON PLACEHOLDER for Azure Service Bus topics and subscriptions, for the local host only.
/// Publishing puts the event on an in-memory queue and returns, like a send to the broker. A background
/// dispatcher delivers it: every handler of the event type is one subscription, a failed delivery is
/// repeated, and after <see cref="MaxDeliveryCount"/> attempts the message is dead-lettered (here: logged).
/// A message id that a subscription has already handled is acknowledged without running the handler.
/// It is NOT durable: messages are lost if the process stops.
/// </summary>
internal sealed partial class InMemoryMessageBus(
    IServiceScopeFactory scopeFactory, InMemoryInbox inbox, ILogger<InMemoryMessageBus> logger)
    : BackgroundService, IEventPublisher
{
    public const int MaxDeliveryCount = 3;

    public static readonly ActivitySource ActivitySource = new(ServiceCollectionExtensions.MessagingActivitySourceName);

    private readonly Channel<IIntegrationEvent> _queue = Channel.CreateUnbounded<IIntegrationEvent>();

    public Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken) =>
        _queue.Writer.WriteAsync(integrationEvent, cancellationToken).AsTask();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var integrationEvent in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await DeliverAsync(integrationEvent, stoppingToken);
        }
    }

    /// <summary>Delivers one message to every subscription. Never throws because of a handler.</summary>
    internal async Task DeliverAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        var eventType = integrationEvent.GetType();
        var handlerType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);
        var handle = handlerType.GetMethod(nameof(IIntegrationEventHandler<IIntegrationEvent>.HandleAsync))!;

        await using var subscriptions = scopeFactory.CreateAsyncScope();
        var consumers = subscriptions.ServiceProvider.GetServices(handlerType).Select(handler => handler!.GetType()).ToList();

        foreach (var consumer in consumers)
        {
            await DeliverAsync(integrationEvent, handlerType, consumer, handle, cancellationToken);
        }
    }

    private async Task DeliverAsync(
        IIntegrationEvent integrationEvent,
        Type handlerType,
        Type consumer,
        System.Reflection.MethodInfo handle,
        CancellationToken cancellationToken)
    {
        var eventName = integrationEvent.GetType().Name;

        for (var delivery = 1; delivery <= MaxDeliveryCount; delivery++)
        {
            if (!inbox.TryRecord(consumer.FullName!, integrationEvent.EventId))
            {
                // Duplicate: the publisher's handler ran twice and published the same fact again.
                LogDuplicate(logger, eventName, integrationEvent.EventId, consumer.Name);
                return;
            }

            // Each delivery runs in its own scope, like one invocation of a Service Bus-triggered function.
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetServices(handlerType).Single(candidate => candidate!.GetType() == consumer);

            using var activity = ActivitySource.StartActivity($"handle {eventName}");
            activity?.SetTag("messaging.message.id", integrationEvent.EventId);
            activity?.SetTag("messaging.destination.subscription.name", consumer.Name);

            try
            {
                await (Task)handle.Invoke(handler, [integrationEvent, cancellationToken])!;
                return;
            }
#pragma warning disable CA1031 // One failing consumer must not stop the bus or the other consumers.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                inbox.Forget(consumer.FullName!, integrationEvent.EventId);

                var actual = exception is System.Reflection.TargetInvocationException { InnerException: { } inner }
                    ? inner
                    : exception;
                activity?.SetStatus(ActivityStatusCode.Error, actual.Message);
                LogHandlerFailed(logger, actual, consumer.Name, eventName, integrationEvent.EventId, delivery, MaxDeliveryCount);
            }
        }

        LogDeadLettered(logger, eventName, integrationEvent.EventId, consumer.Name);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Duplicate {EventType} {EventId} ignored by {Consumer}")]
    private static partial void LogDuplicate(ILogger logger, string eventType, Guid eventId, string consumer);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Handler {Consumer} failed for {EventType} {EventId} (delivery {Delivery} of {MaxDeliveries})")]
    private static partial void LogHandlerFailed(
        ILogger logger, Exception exception, string consumer, string eventType, Guid eventId, int delivery, int maxDeliveries);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "{EventType} {EventId} was dead-lettered for {Consumer}. A person must look at it")]
    private static partial void LogDeadLettered(ILogger logger, string eventType, Guid eventId, string consumer);
}
