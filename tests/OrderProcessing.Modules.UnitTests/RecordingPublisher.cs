using OrderProcessing.BuildingBlocks.Application;

namespace OrderProcessing.Modules.UnitTests;

/// <summary>Stands in for Service Bus on the sending side: keeps what was published, and can be down.</summary>
internal sealed class RecordingPublisher : IEventPublisher
{
    public List<IIntegrationEvent> Events { get; } = [];

    /// <summary>The next publishes fail, as when Service Bus is unreachable.</summary>
    public int FailuresLeft { get; set; }

    public Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        if (FailuresLeft-- > 0)
        {
            throw new TimeoutException("Service Bus is unreachable");
        }

        Events.Add(integrationEvent);
        return Task.CompletedTask;
    }
}
