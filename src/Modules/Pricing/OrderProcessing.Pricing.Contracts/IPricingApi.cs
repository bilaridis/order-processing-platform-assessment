using OrderProcessing.BuildingBlocks.Application;

namespace OrderProcessing.Pricing.Contracts;

/// <summary>
/// Public API of the Pricing service. A request: the answer arrives as <see cref="PriceQuoted"/> or
/// <see cref="PriceQuoteRejected"/>. Between services the request is a command on
/// <see cref="PricingMessaging.CommandQueue"/>; nothing is called over HTTP (ADR-0003).
/// </summary>
public interface IPricingApi
{
    /// <summary>
    /// Prices the basket of an order. The caller stores the quote as an immutable snapshot; an accepted
    /// order is never re-priced. Idempotent per <see cref="PriceQuoteRequest.OrderId"/> in effect: the
    /// same request gives the same quote.
    /// </summary>
    Task RequestQuoteAsync(PriceQuoteRequest request, CancellationToken cancellationToken);
}

/// <param name="DeliveryOfferToken">
/// The delivery offer the customer chose. When present, the delivery fee is the price of that offer,
/// taken from the verified token and never from the client. When absent, the standard shipping rule applies.
/// </param>
public sealed record PriceQuoteRequest(
    Guid OrderId,
    string Currency,
    string DestinationCountry,
    IReadOnlyList<PriceQuoteItem> Items,
    string? DeliveryOfferToken = null);

public sealed record PriceQuoteItem(string Sku, int Quantity);

public sealed record PriceQuote(
    string Currency,
    IReadOnlyList<PricedItem> Items,
    decimal Subtotal,
    IReadOnlyList<PriceComponent> Components,
    decimal Total);

public sealed record PricedItem(string Sku, string Name, int Quantity, decimal UnitPrice, decimal LineTotal);

/// <summary>Anything added on top of the item subtotal: a tax, a charge (shipping, handling) or a discount.</summary>
public sealed record PriceComponent(string Code, PriceComponentKind Kind, string Description, decimal Amount);

public enum PriceComponentKind
{
    Charge,
    Tax,
    Discount,
}

public sealed record PriceQuoted(Guid OrderId, PriceQuote Quote) : IntegrationEvent;

/// <summary>The basket cannot be priced: an unknown product, an invalid or expired delivery offer.</summary>
public sealed record PriceQuoteRejected(Guid OrderId, string Code, string Reason) : IntegrationEvent;

/// <summary>Where the commands of this service travel between services (ADR-0003).</summary>
public static class PricingMessaging
{
    public const string CommandQueue = "pricing-commands";
    public const string QuotePrice = nameof(QuotePrice);
}
