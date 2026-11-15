using Microsoft.Extensions.Options;
using OrderProcessing.Inventory;
using OrderProcessing.Inventory.Contracts;

namespace OrderProcessing.Modules.UnitTests.Inventory;

/// <summary>Inventory as a service: the caller never names a source; the resolver and the rules decide.</summary>
public class InventoryApiTests
{
    private readonly RecordingPublisher _events = new();

    [Fact]
    public async Task The_first_source_in_priority_order_supplies_the_order_when_it_has_everything()
    {
        await Api().RequestReservationAsync(Request("SKU-1"), CancellationToken.None);

        Assert.Equal(ExampleSources.ThirdPartyLogistics, Reserved().Source);
    }

    [Fact]
    public async Task The_next_source_is_tried_when_the_first_one_is_short()
    {
        await Api().RequestReservationAsync(Request("INH-1"), CancellationToken.None);

        Assert.Equal(ExampleSources.InHouse, Reserved().Source);
    }

    [Fact]
    public async Task One_source_must_supply_the_whole_order()
    {
        // "A-" items may only come from the 3PL, "B-" items only from our own warehouse: no single
        // source is allowed for both, and the launch strategy does not split an order.
        var options = new InventoryOptions
        {
            Routes =
            {
                ["A-"] = [ExampleSources.ThirdPartyLogistics],
                ["B-"] = [ExampleSources.InHouse],
            },
        };

        await Api(options).RequestReservationAsync(Request("A-1", "B-1"), CancellationToken.None);

        Assert.Equal(InventoryApi.Unavailable, Failed().Code);
    }

    [Fact]
    public async Task Shortages_are_reported_when_no_source_has_the_stock()
    {
        await Api().RequestReservationAsync(Request("OUT-1"), CancellationToken.None);

        Assert.Equal(InventoryApi.InsufficientStock, Failed().Code);
        Assert.Equal("OUT-1", Assert.Single(Failed().Shortages).Sku);
    }

    [Fact]
    public async Task A_source_that_does_not_answer_is_skipped()
    {
        var api = Api(sources: [new DownSource(ExampleSources.ThirdPartyLogistics), ExampleSources.CreateInHouse()]);

        await api.RequestReservationAsync(Request("SKU-1"), CancellationToken.None);

        Assert.Equal(ExampleSources.InHouse, Reserved().Source);
    }

    [Fact]
    public async Task The_reservation_fails_closed_when_no_source_answers()
    {
        var api = Api(sources: [new DownSource(ExampleSources.ThirdPartyLogistics), new DownSource(ExampleSources.InHouse)]);

        await api.RequestReservationAsync(Request("SKU-1"), CancellationToken.None);

        Assert.Equal(InventoryApi.Unavailable, Failed().Code);
    }

    [Fact]
    public async Task Asked_twice_for_the_same_order_it_reserves_once_and_answers_with_the_same_fact()
    {
        // One piece in stock. A command delivered twice must not take it twice, and must answer again,
        // because the first answer may never have been published.
        var api = Api(sources: [new FakeInventorySource(ExampleSources.InHouse, _ => 1)]);
        var request = Request("SKU-1");

        await api.RequestReservationAsync(request, CancellationToken.None);
        await api.RequestReservationAsync(request, CancellationToken.None);

        Assert.Equal(2, _events.Events.OfType<StockReserved>().Count());
        Assert.Single(_events.Events.Select(integrationEvent => integrationEvent.EventId).Distinct());
    }

    [Fact]
    public async Task A_release_goes_to_the_source_that_holds_the_reservation()
    {
        // The in-house warehouse has exactly one piece; releasing the first order makes it available again.
        var api = Api(sources: [new FakeInventorySource(ExampleSources.InHouse, _ => 1)]);
        var first = Request("SKU-1");

        await api.RequestReservationAsync(first, CancellationToken.None);
        await api.RequestReservationAsync(Request("SKU-1"), CancellationToken.None);
        Assert.IsType<StockReservationFailed>(_events.Events[^1]);

        await api.RequestReleaseAsync(first.OrderId, CancellationToken.None);
        await api.RequestReservationAsync(Request("SKU-1"), CancellationToken.None);

        Assert.IsType<StockReserved>(_events.Events[^1]);
    }

    private StockReserved Reserved() => Assert.IsType<StockReserved>(Assert.Single(_events.Events));

    private StockReservationFailed Failed() => Assert.IsType<StockReservationFailed>(Assert.Single(_events.Events));

    private InventoryApi Api(InventoryOptions? options = null, IInventorySource[]? sources = null) => new(
        new InventorySourceResolver(
            sources ?? [ExampleSources.CreateThirdPartyLogistics(), ExampleSources.CreateInHouse()],
            Options.Create(options ?? new InventoryOptions())),
        new InMemoryReservationStore(),
        _events);

    private static ReserveStockRequest Request(params string[] skus) =>
        new(Guid.NewGuid(), [.. skus.Select(sku => new StockItem(sku, 1))]);

    private sealed class DownSource(string name) : IInventorySource
    {
        public string Name => name;

        public Task<IReadOnlyList<StockShortage>> TryReserveAsync(
            string reservationKey, IReadOnlyList<StockItem> items, CancellationToken cancellationToken) =>
            throw new HttpRequestException("source is down");

        public Task ReleaseAsync(string reservationKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
