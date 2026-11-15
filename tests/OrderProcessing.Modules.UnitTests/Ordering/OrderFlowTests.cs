using Microsoft.Extensions.Logging.Abstractions;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Ecommerce.Contracts;
using OrderProcessing.Inventory.Contracts;
using OrderProcessing.Ordering.Application;
using OrderProcessing.Ordering.Contracts;
using OrderProcessing.Ordering.Domain;
using OrderProcessing.Pricing.Contracts;

namespace OrderProcessing.Modules.UnitTests.Ordering;

/// <summary>
/// The order flow of ADR-0005 over messages: an order is received, priced, its stock reserved, and only
/// then accepted. Ordering runs against hand-written fakes of the other services' contracts, which record
/// the commands it sends. The events they would answer with are handed to the process manager by the test.
/// </summary>
public class OrderFlowTests
{
    private static readonly Guid Id = Guid.NewGuid();

    private static readonly PriceQuote Quote = new(
        "EUR",
        [new PricedItem("SKU-1", "One", 2, 10m, 20m)],
        20m,
        [new PriceComponent("tax", OrderProcessing.Pricing.Contracts.PriceComponentKind.Tax, "Tax", 2m)],
        22m);

    private readonly FakeOrderRepository _orders = new();
    private readonly RecordingPublisher _events = new();
    private readonly OtherServices _others = new();
    private readonly PlaceOrderHandler _placeOrder;
    private readonly OrderFulfillmentProcess _process;

    public OrderFlowTests()
    {
        var store = new OrderStore(_orders, _events);
        _placeOrder = new PlaceOrderHandler(_orders, store, TimeProvider.System);
        _process = new OrderFulfillmentProcess(
            _orders, store, _others, _others, _others, _others, TimeProvider.System, NullLogger<OrderFulfillmentProcess>.Instance);
    }

    [Fact]
    public async Task Receiving_an_order_records_it_as_submitted_and_checks_nothing_yet()
    {
        await _placeOrder.HandleAsync(Command(), default);

        Assert.Equal(OrderStatus.Submitted, Order.Status);
        var submitted = Assert.IsType<OrderSubmitted>(Assert.Single(_events.Events));
        Assert.Equal((Id, 1L), (submitted.OrderId, submitted.Version));
        Assert.Empty(_others.Commands);
    }

    [Fact]
    public async Task An_order_is_accepted_only_after_it_is_priced_and_its_stock_is_reserved()
    {
        await _placeOrder.HandleAsync(Command(), default);

        await _process.HandleAsync(Last<OrderSubmitted>(), default);
        Assert.Equal(["QuotePrice"], _others.Commands);

        await _process.HandleAsync(new PriceQuoted(Id, Quote), default);
        Assert.Equal(["QuotePrice", "ReserveStock"], _others.Commands);
        Assert.Equal((OrderStatus.Submitted, 22m), (Order.Status, Order.Price!.Total));

        await _process.HandleAsync(new StockReserved(Id, "3pl"), default);
        Assert.Equal(OrderStatus.Pending, Order.Status);

        // The event carries the summary and the version, so a read model never has to call Ordering.
        var placed = Last<OrderPlaced>();
        Assert.Equal((2L, 22m, "GR"), (placed.Version, placed.Summary.Total, placed.Summary.Destination.Country));

        await _process.HandleAsync(placed, default);
        Assert.Equal(["QuotePrice", "ReserveStock", "AuthorizePayment"], _others.Commands);
    }

    [Fact]
    public async Task An_order_that_cannot_be_priced_is_rejected_and_no_stock_is_asked_for()
    {
        await _placeOrder.HandleAsync(Command(), default);

        await _process.HandleAsync(new PriceQuoteRejected(Id, "pricing.unknown_product", "no price"), default);

        Assert.Equal(OrderStatus.Rejected, Order.Status);
        Assert.Contains("pricing.unknown_product", Last<OrderRejected>().Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("ReserveStock", _others.Commands);
    }

    [Theory]
    [InlineData("inventory.insufficient_stock")]
    [InlineData("inventory.unavailable")]
    public async Task An_order_is_rejected_when_stock_is_short_or_cannot_be_confirmed(string code)
    {
        await _placeOrder.HandleAsync(Command(), default);
        await _process.HandleAsync(new PriceQuoted(Id, Quote), default);

        await _process.HandleAsync(new StockReservationFailed(Id, code, []), default);

        Assert.Equal(OrderStatus.Rejected, Order.Status);
        Assert.StartsWith(code, Last<OrderRejected>().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_command_delivered_twice_creates_one_order_and_announces_the_same_fact()
    {
        await _placeOrder.HandleAsync(Command(), default);
        await _placeOrder.HandleAsync(Command(), default);

        Assert.Single(_orders.Saved);
        Assert.Single(_events.Events.Select(integrationEvent => integrationEvent.EventId).Distinct());
    }

    [Fact]
    public async Task Without_an_outbox_a_failed_publish_is_repaired_by_running_the_handler_again()
    {
        await _placeOrder.HandleAsync(Command(), default);
        await _process.HandleAsync(new PriceQuoted(Id, Quote), default);
        var reserved = new StockReserved(Id, "3pl");

        // The order is saved as Pending, then Service Bus is unreachable: the handler fails, so the
        // StockReserved message is not completed and is delivered again.
        _events.FailuresLeft = 1;
        await Assert.ThrowsAsync<TimeoutException>(() => _process.HandleAsync(reserved, default));
        Assert.Equal(OrderStatus.Pending, Order.Status);
        Assert.DoesNotContain(_events.Events, integrationEvent => integrationEvent is OrderPlaced);

        // Second delivery: the transition is a no-op, and OrderPlaced is published from the state.
        await _process.HandleAsync(reserved, default);

        Assert.Equal(2, Last<OrderPlaced>().Version);
    }

    [Fact]
    public async Task Stock_reserved_for_an_order_cancelled_meanwhile_is_given_back()
    {
        await _placeOrder.HandleAsync(Command(), default);
        await _process.HandleAsync(new PriceQuoted(Id, Quote), default);
        Order.Cancel("changed my mind", DateTimeOffset.UtcNow);

        await _process.HandleAsync(new StockReserved(Id, "3pl"), default);

        Assert.Equal(OrderStatus.Cancelled, Order.Status);
        Assert.Contains("ReleaseStock", _others.Commands);
    }

    [Fact]
    public async Task A_repeated_payment_event_does_not_reverse_the_payment_of_an_order_that_moved_on()
    {
        await _placeOrder.HandleAsync(Command(), default);
        await _process.HandleAsync(new PriceQuoted(Id, Quote), default);
        await _process.HandleAsync(new StockReserved(Id, "3pl"), default);
        var paid = new PaymentAuthorized(Id, "pay_1");
        await _process.HandleAsync(paid, default);
        await _process.HandleAsync(new ShipmentDispatched(Id, "acs", "T-1"), default);

        await _process.HandleAsync(paid, default);

        Assert.Equal(OrderStatus.Shipped, Order.Status);
        Assert.DoesNotContain("ReversePayment", _others.Commands);
    }

    private Order Order => _orders.Saved.Single();

    private TEvent Last<TEvent>() => _events.Events.OfType<TEvent>().Last();

    private static PlaceOrder Command() => new(
        new OrderId(Id), "customer-1", "EUR", [new PlaceOrderItem("SKU-1", 2)], TestOrders.Address, "card");

    /// <summary>The other services as Ordering sees them: it can only send them commands.</summary>
    private sealed class OtherServices : IPricingApi, IInventoryApi, IEcommerceApi, ICarriersApi
    {
        public List<string> Commands { get; } = [];

        public Task RequestQuoteAsync(PriceQuoteRequest request, CancellationToken cancellationToken) => Sent("QuotePrice");

        public Task RequestReservationAsync(ReserveStockRequest request, CancellationToken cancellationToken) => Sent("ReserveStock");

        public Task RequestReleaseAsync(Guid orderId, CancellationToken cancellationToken) => Sent("ReleaseStock");

        public Task RequestAuthorizationAsync(AuthorizePaymentRequest request, CancellationToken cancellationToken) => Sent("AuthorizePayment");

        public Task RequestReversalAsync(Guid orderId, CancellationToken cancellationToken) => Sent("ReversePayment");

        public Task RequestShipmentAsync(ShipmentRequest request, CancellationToken cancellationToken) => Sent("RequestShipment");

        public Task RequestCancellationAsync(Guid orderId, CancellationToken cancellationToken) => Sent("CancelShipment");

        private Task Sent(string command)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOrderRepository : IOrderRepository
    {
        public List<Order> Saved { get; } = [];

        public Task<Order?> FindAsync(OrderId id, CancellationToken cancellationToken) =>
            Task.FromResult(Saved.Find(order => order.Id == id));

        public Task SaveAsync(Order order, CancellationToken cancellationToken)
        {
            if (!Saved.Contains(order))
            {
                Saved.Add(order);
            }

            return Task.CompletedTask;
        }
    }
}
