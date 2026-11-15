using OrderProcessing.BuildingBlocks.Application;

namespace OrderProcessing.Ordering.Contracts;

// Integration events published by the Ordering module. One event per lifecycle transition, so any
// consumer (the Order API's views, notifications, analytics) can follow an order without calling Ordering.
//
// Version is the position of the transition in the order's history (1 = placed). Delivery is at least
// once and not ordered across topics, so a consumer that keeps a copy of the status ignores an event
// whose version is not higher than the one it already has (ADR-0013).

/// <summary>
/// The order was received and is being checked: priced, then stock reserved. It is not accepted yet.
/// </summary>
public sealed record OrderSubmitted(
    Guid OrderId,
    string CustomerId,
    IReadOnlyList<OrderSubmittedItem> Items,
    OrderSummaryDestination Destination,
    long Version,
    CustomerContact? Contact = null) : IntegrationEvent;

public sealed record OrderSubmittedItem(string Sku, int Quantity);

/// <summary>
/// How the customer wants to hear about this order. Given at checkout. Personal data: it travels only
/// in <see cref="OrderSubmitted"/>, and the notification service keeps its own copy from there.
/// </summary>
/// <param name="Language">ISO 639-1 code for the wording of the messages, for example "el" or "en".</param>
public sealed record CustomerContact(string? Email, string? Mobile, string Language = "el");

/// <summary>The order passed both checks and is accepted (<c>Pending</c>): waiting for payment.</summary>
/// <param name="Summary">
/// What was ordered, at what price, to where. Carried so that a consumer can build its own read model
/// without calling back into Ordering. Contains personal data (the destination).
/// </param>
public sealed record OrderPlaced(
    Guid OrderId, string CustomerId, decimal Total, string Currency, long Version, OrderSummary Summary) : IntegrationEvent;

public sealed record OrderConfirmed(Guid OrderId, long Version) : IntegrationEvent;

public sealed record OrderRejected(Guid OrderId, string Reason, long Version) : IntegrationEvent;

public sealed record OrderCancelled(Guid OrderId, string Reason, long Version) : IntegrationEvent;

public sealed record OrderShipped(Guid OrderId, string Carrier, string TrackingNumber, long Version) : IntegrationEvent;

public sealed record OrderDelivered(Guid OrderId, long Version) : IntegrationEvent;

/// <summary>
/// A cancellation was asked for and refused, typically because the order has shipped. Not a transition,
/// so it has no version. It exists because a command has no return value to carry the refusal.
/// </summary>
public sealed record OrderCancellationRefused(Guid OrderId, string Code, string Reason) : IntegrationEvent;

public sealed record OrderSummary(
    IReadOnlyList<OrderSummaryLine> Lines,
    decimal Subtotal,
    IReadOnlyList<OrderSummaryCharge> Charges,
    decimal Total,
    string Currency,
    OrderSummaryDestination Destination,
    DateTimeOffset PlacedAt);

public sealed record OrderSummaryLine(string Sku, string Name, int Quantity, decimal UnitPrice, decimal LineTotal);

/// <param name="Kind">Charge, Tax or Discount.</param>
public sealed record OrderSummaryCharge(string Code, string Kind, string Description, decimal Amount);

public sealed record OrderSummaryDestination(string Recipient, string Line1, string? Line2, string City, string PostalCode, string Country);
