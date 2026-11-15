using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.BuildingBlocks.Application;

namespace OrderProcessing.BuildingBlocks.Infrastructure;

/// <summary>
/// The consuming side of ADR-0012 for a message-triggered function: hands a received event to the
/// handlers of this service, each at most once per message id (the inbox). If a handler fails, the
/// exception leaves the function, the broker delivers the message again, and the handlers that already
/// succeeded are skipped.
/// </summary>
public sealed class IntegrationEventConsumer
{
    private readonly IServiceProvider _services;
    private readonly InMemoryInbox _inbox;

    public IntegrationEventConsumer(IServiceProvider services)
    {
        _services = services;
        _inbox = services.GetRequiredService<InMemoryInbox>();
    }

    public async Task ConsumeAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        foreach (var handler in _services.GetServices<IIntegrationEventHandler<TEvent>>())
        {
            var consumer = handler.GetType().FullName!;

            if (!_inbox.TryRecord(consumer, integrationEvent.EventId))
            {
                continue;
            }

            try
            {
                await handler.HandleAsync(integrationEvent, cancellationToken);
            }
            catch
            {
                _inbox.Forget(consumer, integrationEvent.EventId);
                throw;
            }
        }
    }
}
