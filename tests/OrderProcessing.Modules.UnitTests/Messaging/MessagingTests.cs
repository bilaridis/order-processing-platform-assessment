using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Infrastructure;

namespace OrderProcessing.Modules.UnitTests.Messaging;

/// <summary>
/// The guarantees of ADR-0012 without an outbox, as tests: the same fact always has the same message id,
/// a consumer handles a repeated message once, a failed delivery is repeated, and a message that keeps
/// failing is dead-lettered instead of blocking the others.
/// </summary>
public sealed class MessagingTests : IDisposable
{
    private readonly CountingHandler _handler = new();
    private readonly ServiceProvider _services;
    private readonly InMemoryMessageBus _bus;

    public MessagingTests()
    {
        _services = new ServiceCollection()
            .AddSingleton<IIntegrationEventHandler<SomethingHappened>>(_handler)
            .BuildServiceProvider();
        _bus = new InMemoryMessageBus(
            _services.GetRequiredService<IServiceScopeFactory>(), new InMemoryInbox(), NullLogger<InMemoryMessageBus>.Instance);
    }

    [Fact]
    public void The_same_fact_has_the_same_id_whenever_it_is_published()
    {
        var orderId = Guid.NewGuid();

        var first = new SomethingHappened().WithIdFor(orderId, 2);
        var repeat = new SomethingHappened { OccurredAt = DateTimeOffset.UtcNow.AddMinutes(5) }.WithIdFor(orderId, 2);

        Assert.Equal(first.EventId, repeat.EventId);
        Assert.NotEqual(first.EventId, new SomethingHappened().WithIdFor(orderId, 3).EventId);
        Assert.NotEqual(first.EventId, new SomethingHappened().WithIdFor(Guid.NewGuid(), 2).EventId);
        Assert.NotEqual(first.EventId, IntegrationEventIds.For(orderId, typeof(string), 2));
    }

    [Fact]
    public async Task A_fact_published_twice_is_handled_once()
    {
        var orderId = Guid.NewGuid();

        await _bus.DeliverAsync(new SomethingHappened().WithIdFor(orderId), CancellationToken.None);
        await _bus.DeliverAsync(new SomethingHappened().WithIdFor(orderId), CancellationToken.None);

        Assert.Equal(1, _handler.Handled);
    }

    [Fact]
    public async Task A_failed_delivery_is_repeated_until_the_handler_succeeds()
    {
        _handler.FailuresLeft = InMemoryMessageBus.MaxDeliveryCount - 1;

        await _bus.DeliverAsync(new SomethingHappened(), CancellationToken.None);

        Assert.Equal(InMemoryMessageBus.MaxDeliveryCount, _handler.Attempts);
        Assert.Equal(1, _handler.Handled);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_dead_lettered_and_the_next_one_is_still_delivered()
    {
        _handler.FailuresLeft = InMemoryMessageBus.MaxDeliveryCount;

        await _bus.DeliverAsync(new SomethingHappened(), CancellationToken.None);
        await _bus.DeliverAsync(new SomethingHappened(), CancellationToken.None);

        Assert.Equal(InMemoryMessageBus.MaxDeliveryCount + 1, _handler.Attempts);
        Assert.Equal(1, _handler.Handled);
    }

    [Fact]
    public async Task In_a_function_a_failed_handler_fails_the_invocation_and_the_redelivery_runs_it_again()
    {
        var services = new ServiceCollection()
            .AddSingleton<InMemoryInbox>()
            .AddSingleton<IIntegrationEventHandler<SomethingHappened>>(_handler)
            .BuildServiceProvider();
        await using var disposal = services;
        var consumer = new IntegrationEventConsumer(services);
        var message = new SomethingHappened();
        _handler.FailuresLeft = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.ConsumeAsync(message, CancellationToken.None));
        await consumer.ConsumeAsync(message, CancellationToken.None);
        await consumer.ConsumeAsync(message, CancellationToken.None);

        Assert.Equal(1, _handler.Handled);
    }

    public void Dispose()
    {
        _bus.Dispose();
        _services.Dispose();
    }

    private sealed record SomethingHappened : IntegrationEvent;

    private sealed class CountingHandler : IIntegrationEventHandler<SomethingHappened>
    {
        public int Attempts { get; private set; }

        public int Handled { get; private set; }

        public int FailuresLeft { get; set; }

        public Task HandleAsync(SomethingHappened integrationEvent, CancellationToken cancellationToken)
        {
            Attempts++;

            if (FailuresLeft-- > 0)
            {
                throw new InvalidOperationException("The handler failed");
            }

            Handled++;
            return Task.CompletedTask;
        }
    }
}
