using OrderProcessing.BuildingBlocks.Domain;

namespace OrderProcessing.Ordering.Domain;

public readonly record struct OrderId(Guid Value)
{
    /// <summary>Version 7 GUIDs are time-ordered, which keeps the primary key index append-friendly.</summary>
    public static OrderId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>What the customer asked for, before it is priced.</summary>
public sealed record OrderItem(string Sku, int Quantity);

public sealed record OrderLine(string Sku, string Name, int Quantity, decimal UnitPrice, decimal LineTotal);

public enum PriceComponentKind
{
    Charge,
    Tax,
    Discount,
}

/// <summary>A tax, charge or discount applied on top of the item subtotal.</summary>
public sealed record OrderPriceComponent(string Code, PriceComponentKind Kind, string Description, decimal Amount);

/// <summary>
/// Immutable snapshot of the price the customer agreed to. Ordering does not know how prices are
/// calculated (that is the Pricing module); it only guarantees the snapshot is internally consistent.
/// </summary>
public sealed record OrderPrice(
    string Currency,
    decimal Subtotal,
    IReadOnlyList<OrderPriceComponent> Components,
    decimal Total);

public sealed record ShippingAddress(
    string Recipient,
    string Line1,
    string? Line2,
    string City,
    string PostalCode,
    string Country);

public sealed record ShipmentInfo(string Carrier, string TrackingNumber);

/// <summary>One entry of the order's lifecycle history.</summary>
public sealed record OrderStatusChange(OrderStatus Status, DateTimeOffset OccurredAt, string? Reason);

public sealed record OrderStatusChanged(
    OrderId OrderId,
    OrderStatus? Previous,
    OrderStatus Current,
    string? Reason,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Email, mobile number and language for the customer's notifications. Stored with the order, never used by it.</summary>
public sealed record ContactDetails(string? Email, string? Mobile, string Language);
