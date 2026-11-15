using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Ecommerce.Contracts;
using OrderProcessing.OrderApi;
using OrderProcessing.Ordering.Contracts;

namespace OrderProcessing.Modules.UnitTests.OrderApi;

/// <summary>
/// The tracking view of ADR-0013: built from the events of several services, tolerant of duplicates and
/// late events, and never the owner of the order's status.
/// </summary>
public class OrderViewTests
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static readonly OrderSummary Summary = new(
        [new OrderSummaryLine("SKU-1", "One", 2, 10m, 20m)],
        20m,
        [new OrderSummaryCharge("shipping", "Charge", "Locker (boxnow)", 1.90m)],
        21.90m,
        "EUR",
        new OrderSummaryDestination("Ada Lovelace", "1 Main St", null, "Athens", "10431", "GR"),
        Start);

    private readonly InMemoryOrderViewStore _views = new();
    private readonly FakeOrdering _ordering = new();
    private readonly OrderViewProjector _projector;
    private readonly GetOrderViewHandler _getOrder;
    private readonly SubmitOrderHandler _submit;

    public OrderViewTests()
    {
        _projector = new OrderViewProjector(_views);
        _getOrder = new GetOrderViewHandler(_views);
        _submit = new SubmitOrderHandler(_ordering, _views, TimeProvider.System);
    }

    [Fact]
    public async Task Sending_an_order_queues_the_command_and_makes_the_order_readable_at_once()
    {
        var sent = (await _submit.HandleAsync(Submit("key-1"), default)).Value;

        // Nothing has come back from any service yet, and the customer can already follow the order.
        var view = (await _getOrder.HandleAsync(new GetOrderView(sent.OrderId, "customer-1", false), default)).Value;
        Assert.Equal(("Submitted", 0L, null), (view.Status, view.Version, view.Summary));
        Assert.Equal(sent.OrderId, Assert.Single(_ordering.Placed).OrderId);
    }

    [Fact]
    public async Task The_same_idempotency_key_names_the_same_order()
    {
        var first = (await _submit.HandleAsync(Submit("key-1"), default)).Value;
        var retry = (await _submit.HandleAsync(Submit("key-1"), default)).Value;
        var other = (await _submit.HandleAsync(Submit("key-2"), default)).Value;

        // Ordering receives the same order id twice and keeps one order.
        Assert.Equal(first.OrderId, retry.OrderId);
        Assert.NotEqual(first.OrderId, other.OrderId);
    }

    [Fact]
    public async Task A_request_Ordering_could_not_work_with_is_refused_before_anything_is_sent()
    {
        var result = await _submit.HandleAsync(Submit("key-1") with { Items = [] }, default);

        Assert.Equal("order.no_lines", result.Error?.Code);
        Assert.Empty(_ordering.Placed);
    }

    [Fact]
    public async Task The_view_collects_what_every_service_says_about_the_order()
    {
        await _projector.HandleAsync(Submitted(), default);
        await _projector.HandleAsync(Placed(), default);
        await _projector.HandleAsync(At(2, new PaymentAuthorized(OrderId, "pay_1")), default);
        await _projector.HandleAsync(At(3, new OrderConfirmed(OrderId, 3)), default);
        await _projector.HandleAsync(At(4, new OrderShipped(OrderId, "boxnow", "T-1", 4)), default);
        await _projector.HandleAsync(At(5, new ShipmentTrackingUpdated(OrderId, "boxnow", "ready_for_collection", "Syntagma")), default);

        var view = (await _getOrder.HandleAsync(new GetOrderView(OrderId, "customer-1", false), default)).Value;

        Assert.Equal(("Shipped", 4L, "Authorized", "ready_for_collection"), (view.Status, view.Version, view.Payment, view.CarrierStatus));
        Assert.Equal(21.90m, view.Summary!.Total);
        Assert.Equal(
            ["order.submitted", "order.accepted", "payment.authorized", "order.confirmed", "order.shipped", "carrier.ready_for_collection"],
            view.Tracking.Select(entry => entry.Code));
    }

    [Fact]
    public async Task Carrier_detail_is_shown_but_never_becomes_the_order_status()
    {
        await _projector.HandleAsync(Submitted(), default);
        await _projector.HandleAsync(Placed(), default);
        await _projector.HandleAsync(At(2, new OrderConfirmed(OrderId, 3)), default);

        // Even "delivered" from a carrier does not move the order: only Ordering's OrderDelivered does.
        await _projector.HandleAsync(At(3, new ShipmentTrackingUpdated(OrderId, "acs", "out_for_delivery", null)), default);
        await _projector.HandleAsync(At(4, new ShipmentFailed(OrderId, "acs", "address not found")), default);

        var view = (await _views.FindAsync(OrderId, default))!;
        Assert.Equal(("Confirmed", 3L, "failed"), (view.Status, view.Version, view.CarrierStatus));
    }

    [Fact]
    public async Task An_event_that_arrives_late_does_not_move_the_status_back_but_its_data_is_kept()
    {
        await _projector.HandleAsync(Submitted(), default);
        await _projector.HandleAsync(At(4, new OrderShipped(OrderId, "acs", "T-1", 4)), default);

        // OrderPlaced (version 2) arrives after OrderShipped (version 4).
        await _projector.HandleAsync(Placed(), default);

        var view = (await _views.FindAsync(OrderId, default))!;
        Assert.Equal(("Shipped", 4L), (view.Status, view.Version));
        Assert.Equal(21.90m, view.Summary!.Total);
        Assert.Equal(["order.submitted", "order.accepted", "order.shipped"], view.Tracking.Select(entry => entry.Code));
    }

    [Fact]
    public async Task The_same_event_delivered_twice_changes_nothing()
    {
        var confirmed = At(2, new OrderConfirmed(OrderId, 3));
        await _projector.HandleAsync(Submitted(), default);
        await _projector.HandleAsync(Submitted(), default);
        await _projector.HandleAsync(confirmed, default);
        await _projector.HandleAsync(confirmed, default);

        Assert.Equal(2, (await _views.FindAsync(OrderId, default))!.Tracking.Count);
    }

    [Fact]
    public async Task An_event_that_overtakes_OrderSubmitted_is_refused_so_that_it_is_delivered_again()
    {
        await Assert.ThrowsAsync<OrderViewNotReadyException>(
            () => _projector.HandleAsync(new PaymentAuthorized(OrderId, "pay_1"), default));
    }

    [Fact]
    public async Task A_refused_cancellation_is_shown_and_leaves_the_status_alone()
    {
        await _projector.HandleAsync(Submitted(), default);
        await _projector.HandleAsync(At(4, new OrderShipped(OrderId, "acs", "T-1", 4)), default);

        await _projector.HandleAsync(At(5, new OrderCancellationRefused(OrderId, "order.cannot_cancel", "already shipped")), default);

        var view = (await _views.FindAsync(OrderId, default))!;
        Assert.Equal("Shipped", view.Status);
        Assert.Equal("order.cancellation_refused", view.Tracking[^1].Code);
    }

    [Fact]
    public async Task A_customer_reads_only_their_own_partition()
    {
        await _projector.HandleAsync(Submitted(), default);

        var stranger = await _getOrder.HandleAsync(new GetOrderView(OrderId, "customer-2", false), default);
        var backOffice = await _getOrder.HandleAsync(new GetOrderView(OrderId, "agent-7", true), default);
        var mine = await new ListMyOrdersHandler(_views).HandleAsync(new ListMyOrders("customer-1", 20), default);
        var theirs = await new ListMyOrdersHandler(_views).HandleAsync(new ListMyOrders("customer-2", 20), default);

        Assert.Equal("order.not_found", stranger.Error?.Code);
        Assert.True(backOffice.IsSuccess);
        Assert.Equal(OrderId, Assert.Single(mine.Value).OrderId);
        Assert.Empty(theirs.Value);
    }

    private static OrderSubmitted Submitted() =>
        new(OrderId, "customer-1", [new OrderSubmittedItem("SKU-1", 2)], Summary.Destination, 1) { OccurredAt = Start };

    private static SubmitOrder Submit(string key) => new(
        "customer-1", key, "EUR", [new OrderSubmittedItem("SKU-1", 2)], Summary.Destination, "card", null);

    private static OrderPlaced Placed() => new(OrderId, "customer-1", 21.90m, "EUR", 2, Summary) { OccurredAt = Start.AddMinutes(1) };

    private static T At<T>(int minutes, T integrationEvent)
        where T : BuildingBlocks.Application.IntegrationEvent =>
        integrationEvent with { OccurredAt = Start.AddMinutes(minutes) };

    /// <summary>Stands in for Ordering: the Order API can only send it commands.</summary>
    private sealed class FakeOrdering : IOrderingApi
    {
        public List<PlaceOrderRequest> Placed { get; } = [];

        public Task RequestPlacementAsync(PlaceOrderRequest request, CancellationToken cancellationToken)
        {
            Placed.Add(request);
            return Task.CompletedTask;
        }

        public Task RequestCancellationAsync(CancelOrderRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
