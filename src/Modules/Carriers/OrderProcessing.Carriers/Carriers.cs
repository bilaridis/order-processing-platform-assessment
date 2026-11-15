using System.Collections.Concurrent;
using OrderProcessing.Carriers.Contracts;

namespace OrderProcessing.Carriers;

/// <summary>
/// Port to one carrier (ADR-0006). One adapter per carrier. The adapter translates the carrier's API,
/// errors and tracking codes into ours and owns its timeouts and retries.
/// </summary>
internal interface ICarrier
{
    /// <summary>Key of the carrier in the registry, in offer tokens and in webhook routes.</summary>
    string Name { get; }

    /// <summary>What this carrier can do for the parcel. An empty list means "cannot serve it".</summary>
    Task<IReadOnlyList<CarrierQuote>> GetQuotesAsync(DeliveryOffersRequest request, CancellationToken cancellationToken);

    /// <summary>The carrier must deduplicate on <paramref name="idempotencyKey"/>.</summary>
    Task<CarrierBooking> BookAsync(
        string idempotencyKey, ShipmentRequest request, VerifiedDeliveryOffer? offer, CancellationToken cancellationToken);

    Task CancelAsync(string idempotencyKey, CancellationToken cancellationToken);
}

internal sealed record CarrierQuote(
    string Service, DeliveryType Type, string? PointId, string? PointName, decimal Price, string Currency, int DeliveryDays);

internal sealed record CarrierBooking(bool Accepted, string? RefusalReason)
{
    public static readonly CarrierBooking Ok = new(true, null);

    public static CarrierBooking Refused(string reason) => new(false, reason);
}

/// <summary>
/// SKELETON PLACEHOLDER for a real carrier adapter (typed HttpClient + resilience pipeline). It answers
/// one fixed quote. Prices and delivery times are illustrative, not the carriers' tariffs.
/// Recipients named "REFUSE" are refused, so the failure path can be exercised.
/// </summary>
internal sealed class FakeCarrier(string name, CarrierQuote quote, int? maxItems = null) : ICarrier
{
    public string Name => name;

    public Task<IReadOnlyList<CarrierQuote>> GetQuotesAsync(
        DeliveryOffersRequest request, CancellationToken cancellationToken)
    {
        var fits = maxItems is null || request.Items.Sum(item => item.Quantity) <= maxItems;
        return Task.FromResult<IReadOnlyList<CarrierQuote>>(fits ? [quote] : []);
    }

    public Task<CarrierBooking> BookAsync(
        string idempotencyKey, ShipmentRequest request, VerifiedDeliveryOffer? offer, CancellationToken cancellationToken) =>
        Task.FromResult(request.Address.Recipient == "REFUSE"
            ? CarrierBooking.Refused($"{name} cannot take this parcel")
            : CarrierBooking.Ok);

    public Task CancelAsync(string idempotencyKey, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The three example carriers of the skeleton (docs/architecture/06-integration-services.md).</summary>
internal static class ExampleCarriers
{
    public const string Acs = "acs";
    public const string BoxNow = "boxnow";
    public const string SkroutzPoint = "skroutz-point";

    /// <summary>Courier to the door.</summary>
    public static FakeCarrier CreateAcs() => new(
        Acs, new CarrierQuote("Home delivery", DeliveryType.Home, null, null, 3.90m, "EUR", 1));

    /// <summary>Parcel lockers. A locker has a size limit, so large parcels get no offer.</summary>
    public static FakeCarrier CreateBoxNow() => new(
        BoxNow, new CarrierQuote("Locker", DeliveryType.Locker, "BN-0042", "Syntagma", 1.90m, "EUR", 1), maxItems: 5);

    /// <summary>Collection from a pickup point.</summary>
    public static FakeCarrier CreateSkroutzPoint() => new(
        SkroutzPoint, new CarrierQuote("Pickup point", DeliveryType.PickupPoint, "SP-0117", "Kallithea", 1.50m, "EUR", 2));
}

/// <summary>SKELETON PLACEHOLDER for the service's Shipments table (PartitionKey = order id).</summary>
internal sealed class InMemoryShipmentStore
{
    private readonly ConcurrentDictionary<Guid, string> _carrierByOrder = new();

    public bool Exists(Guid orderId) => _carrierByOrder.ContainsKey(orderId);

    public void Record(Guid orderId, string carrier) => _carrierByOrder[orderId] = carrier;

    public bool TryRemove(Guid orderId, out string? carrier) => _carrierByOrder.TryRemove(orderId, out carrier);
}
