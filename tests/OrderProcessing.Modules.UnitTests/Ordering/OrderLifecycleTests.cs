using OrderProcessing.Ordering.Domain;

namespace OrderProcessing.Modules.UnitTests.Ordering;

/// <summary>The lifecycle rules of docs/architecture/02-order-lifecycle.md, as executable examples.</summary>
public class OrderLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_received_order_is_submitted_has_no_price_and_records_its_first_history_entry()
    {
        var order = TestOrders.Submitted(Now);

        Assert.Equal(OrderStatus.Submitted, order.Status);
        Assert.Null(order.Price);
        Assert.Empty(order.Lines);
        var raised = Assert.IsType<OrderStatusChanged>(Assert.Single(order.DomainEvents));
        Assert.Null(raised.Previous);
    }

    [Fact]
    public void An_order_needs_at_least_one_item()
    {
        var result = Order.Submit(OrderId.New(), "customer-1", "EUR", [], TestOrders.Address, "tok", Now);

        Assert.Equal(OrderErrors.NoLines, result.Error);
    }

    [Fact]
    public void An_order_is_not_accepted_before_it_has_a_price()
    {
        var order = TestOrders.Submitted(Now);

        Assert.Equal(OrderErrors.NotPriced, order.Accept(Now).Error);
        Assert.Equal(OrderStatus.Submitted, order.Status);
    }

    [Fact]
    public void A_quote_that_does_not_add_up_is_refused()
    {
        var order = TestOrders.Submitted(Now);

        var result = order.ApplyQuote(TestOrders.Lines, TestOrders.Price with { Total = 99m });

        Assert.Equal(OrderErrors.InconsistentPrice, result.Error);
        Assert.Null(order.Price);
    }

    [Fact]
    public void The_happy_path_goes_submitted_pending_confirmed_shipped_delivered()
    {
        var order = TestOrders.Submitted(Now);

        Assert.True(order.ApplyQuote(TestOrders.Lines, TestOrders.Price).IsSuccess);
        Assert.True(order.Accept(Now.AddSeconds(2)).IsSuccess);
        Assert.True(order.Confirm("pay_1", Now.AddMinutes(1)).IsSuccess);
        Assert.True(order.MarkShipped(new ShipmentInfo("ACS", "T-1"), Now.AddHours(5)).IsSuccess);
        Assert.True(order.MarkDelivered(Now.AddDays(2)).IsSuccess);

        Assert.Equal(
            [OrderStatus.Submitted, OrderStatus.Pending, OrderStatus.Confirmed, OrderStatus.Shipped, OrderStatus.Delivered],
            order.History.Select(change => change.Status));
    }

    [Theory]
    [InlineData(OrderStatus.Submitted)]
    [InlineData(OrderStatus.Pending)]
    public void An_order_can_be_rejected_while_it_is_checked_or_waits_for_payment(OrderStatus status)
    {
        var order = TestOrders.In(status, Now);

        Assert.True(order.Reject("inventory.insufficient_stock: SKU-1", Now).IsSuccess);
        Assert.Equal(OrderStatus.Rejected, order.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Submitted)]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Confirmed)]
    public void An_order_can_be_cancelled_until_it_ships(OrderStatus status)
    {
        var order = TestOrders.In(status, Now);

        var result = order.Cancel("changed my mind", Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal("changed my mind", order.History[^1].Reason);
    }

    [Theory]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.Rejected)]
    public void An_order_cannot_be_cancelled_once_shipped_or_finished(OrderStatus status)
    {
        var order = TestOrders.In(status, Now);

        var result = order.Cancel("too late", Now);

        Assert.Equal("order.cannot_cancel", result.Error?.Code);
        Assert.Equal(status, order.Status);
    }

    [Fact]
    public void Repeating_a_transition_is_a_no_op_because_events_are_delivered_at_least_once()
    {
        var order = TestOrders.In(OrderStatus.Confirmed, Now);
        order.ClearDomainEvents();
        var historyLength = order.History.Count;

        var result = order.Confirm("pay_1", Now);

        Assert.True(result.IsSuccess);
        Assert.Empty(order.DomainEvents);
        Assert.Equal(historyLength, order.History.Count);
    }

    [Fact]
    public void A_cancelled_order_cannot_be_confirmed_by_a_late_payment()
    {
        var order = TestOrders.In(OrderStatus.Cancelled, Now);

        var result = order.Confirm("pay_1", Now);

        Assert.Equal("order.invalid_transition", result.Error?.Code);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }
}
