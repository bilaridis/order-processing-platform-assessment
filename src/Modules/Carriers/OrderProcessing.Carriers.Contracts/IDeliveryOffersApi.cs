using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;

namespace OrderProcessing.Carriers.Contracts;

/// <summary>
/// Delivery offers of the carrier service (ADR-0006). The service asks every enabled carrier and reports
/// what they answer. It does not rank, filter by price or pick one: the customer chooses, the order
/// carries the chosen offer's token, and that carrier is the one booked.
///
/// Front ends never call the carrier service: they ask the Order API, which sends this request as a
/// command and shows the answer from its own view (ADR-0013). Only carriers call the carrier service,
/// through their webhooks.
/// </summary>
public interface IDeliveryOffersApi
{
    /// <summary>
    /// A request: the answer arrives as <see cref="DeliveryOffersQuoted"/>. Between services the request is
    /// a command on <see cref="CarriersMessaging.CommandQueue"/>. A carrier that fails or does not answer
    /// in time is left out; the others are still reported.
    /// </summary>
    Task RequestOffersAsync(DeliveryOffersQuoteRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Checks the signature and the expiry of an offer token and returns what was offered. Pricing uses
    /// this to take the delivery fee from the offer instead of trusting a price sent by the client.
    /// </summary>
    Result<VerifiedDeliveryOffer> Verify(string token);
}

public sealed record DeliveryOffersRequest(string DestinationCountry, string PostalCode, IReadOnlyList<ShipmentItem> Items);

/// <param name="RequestId">Chosen by the caller; the answer carries it back.</param>
/// <param name="CustomerId">Who asked, so the answer is shown only to them.</param>
public sealed record DeliveryOffersQuoteRequest(Guid RequestId, string CustomerId, DeliveryOffersRequest Request);

/// <summary>The offers of every carrier that answered. An empty list means no carrier can deliver.</summary>
public sealed record DeliveryOffersQuoted(Guid RequestId, string CustomerId, IReadOnlyList<DeliveryOffer> Offers) : IntegrationEvent;

/// <param name="Token">Opaque to clients. Sent back as <c>deliveryOfferToken</c> when the order is placed.</param>
public sealed record DeliveryOffer(
    string Carrier,
    string Service,
    DeliveryType Type,
    string? PointId,
    string? PointName,
    decimal Price,
    string Currency,
    DateOnly EstimatedDelivery,
    DateTimeOffset ExpiresAt,
    string Token);

public sealed record VerifiedDeliveryOffer(
    string Carrier,
    string Service,
    DeliveryType Type,
    string? PointId,
    decimal Price,
    string Currency,
    string DestinationCountry,
    DateTimeOffset ExpiresAt);

public enum DeliveryType
{
    Home,
    Locker,
    PickupPoint,
}
