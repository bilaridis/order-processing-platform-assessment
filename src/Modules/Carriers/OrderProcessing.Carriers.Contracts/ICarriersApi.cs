using OrderProcessing.BuildingBlocks.Application;

namespace OrderProcessing.Carriers.Contracts;

/// <summary>
/// Public API of the carrier service for shipments. Progress is reported asynchronously with
/// <see cref="ShipmentDispatched"/>, <see cref="ShipmentDelivered"/> and <see cref="ShipmentFailed"/>.
/// </summary>
public interface ICarriersApi
{
    /// <summary>
    /// Books the shipment with the carrier of the delivery offer the customer chose. If that carrier
    /// refuses, <see cref="ShipmentFailed"/> is published; the service never substitutes another carrier.
    /// Idempotent per <see cref="ShipmentRequest.OrderId"/>.
    /// </summary>
    Task RequestShipmentAsync(ShipmentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Best effort: a parcel already handed to the carrier cannot be recalled, in which case
    /// <see cref="ShipmentDispatched"/> is still published. Idempotent.
    /// </summary>
    Task RequestCancellationAsync(Guid orderId, CancellationToken cancellationToken);
}

/// <param name="DeliveryOfferToken">
/// The offer the customer chose (see <see cref="IDeliveryOffersApi"/>). Without one the service's
/// default carrier is used, which keeps clients that do not offer a choice working.
/// </param>
public sealed record ShipmentRequest(
    Guid OrderId,
    ShipmentAddress Address,
    IReadOnlyList<ShipmentItem> Items,
    string? DeliveryOfferToken = null);

public sealed record ShipmentAddress(string Recipient, string Line1, string? Line2, string City, string PostalCode, string Country);

public sealed record ShipmentItem(string Sku, int Quantity);

/// <summary>The command behind <see cref="ICarriersApi.RequestCancellationAsync"/> when it travels as a message.</summary>
public sealed record CancelShipmentRequest(Guid OrderId);

/// <summary>Where the commands of this service travel between services (ADR-0003).</summary>
public static class CarriersMessaging
{
    public const string CommandQueue = "shipping-commands";
    public const string RequestShipment = nameof(RequestShipment);
    public const string CancelShipment = nameof(CancelShipment);
    public const string QuoteDeliveryOffers = nameof(QuoteDeliveryOffers);
}

public sealed record ShipmentDispatched(Guid OrderId, string Carrier, string TrackingNumber) : IntegrationEvent;

public sealed record ShipmentDelivered(Guid OrderId) : IntegrationEvent;

/// <summary>The chosen carrier refused the shipment, or no carrier could be determined for it.</summary>
public sealed record ShipmentFailed(Guid OrderId, string? Carrier, string Reason) : IntegrationEvent;

/// <summary>
/// Carrier detail between two lifecycle states of the order: in transit, out for delivery, ready for
/// collection. It is information for the tracking view of the Order API. Ordering does not consume it,
/// because none of these is a status of the order (ADR-0013).
/// </summary>
public sealed record ShipmentTrackingUpdated(Guid OrderId, string? Carrier, string Status, string? Location) : IntegrationEvent;
