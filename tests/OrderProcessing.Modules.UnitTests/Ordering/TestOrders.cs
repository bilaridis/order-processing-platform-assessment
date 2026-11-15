using OrderProcessing.Ordering.Domain;

namespace OrderProcessing.Modules.UnitTests.Ordering;

internal static class TestOrders
{
    public static readonly ShippingAddress Address = new("Ada Lovelace", "1 Main St", null, "Athens", "10431", "GR");

    public static readonly OrderPrice Price =
        new("EUR", 20m, [new OrderPriceComponent("tax", PriceComponentKind.Tax, "Tax", 4m)], 24m);

    public static readonly OrderLine[] Lines = [new("SKU-1", "One", 2, 10m, 20m)];

    public static Order Submitted(DateTimeOffset now) =>
        Order.Submit(OrderId.New(), "customer-1", "EUR", [new OrderItem("SKU-1", 2)], Address, "card", now).Value;

    /// <summary>An accepted order: submitted, priced, accepted.</summary>
    public static Order Pending(DateTimeOffset now) => In(OrderStatus.Pending, now);

    /// <summary>Builds an order in the requested status by walking the real transitions.</summary>
    public static Order In(OrderStatus status, DateTimeOffset now)
    {
        var order = Submitted(now);

        if (status == OrderStatus.Submitted)
        {
            return order;
        }

        order.ApplyQuote(Lines, Price);
        order.Accept(now);

        switch (status)
        {
            case OrderStatus.Pending:
                break;
            case OrderStatus.Confirmed:
                order.Confirm("pay_1", now);
                break;
            case OrderStatus.Shipped:
                order.Confirm("pay_1", now);
                order.MarkShipped(new ShipmentInfo("ACS", "T-1"), now);
                break;
            case OrderStatus.Delivered:
                order.Confirm("pay_1", now);
                order.MarkShipped(new ShipmentInfo("ACS", "T-1"), now);
                order.MarkDelivered(now);
                break;
            case OrderStatus.Cancelled:
                order.Cancel("test", now);
                break;
            case OrderStatus.Rejected:
                order.Reject("test", now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        return order;
    }
}
