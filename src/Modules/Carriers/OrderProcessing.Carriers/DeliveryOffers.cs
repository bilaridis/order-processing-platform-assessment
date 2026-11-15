using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.Carriers.Contracts;

namespace OrderProcessing.Carriers;

/// <summary>
/// Signs and reads offer tokens: base64url(payload) + "." + base64url(HMAC-SHA256 of the payload).
/// The signature is what lets Pricing and the booking trust a price and a carrier that travelled through
/// the client. In Azure the key comes from Key Vault; without a configured key a random one is used, so
/// tokens are valid only within one process (enough for local development and tests).
/// </summary>
internal sealed class DeliveryOfferTokens(IOptions<CarriersOptions> options, TimeProvider clock)
{
    public static readonly Error Invalid =
        Error.Validation("shipping.delivery_offer_invalid", "The delivery offer is not valid.");

    public static readonly Error Expired =
        Error.Validation("shipping.delivery_offer_expired", "The delivery offer has expired. Request the offers again.");

    private readonly byte[] _key = options.Value.OfferSigningKey is { Length: > 0 } key
        ? Encoding.UTF8.GetBytes(key)
        : RandomNumberGenerator.GetBytes(32);

    public string Sign(VerifiedDeliveryOffer offer)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(offer);
        return $"{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(HMACSHA256.HashData(_key, payload))}";
    }

    /// <param name="checkExpiry">
    /// False when booking: the offer was accepted in time when the order was placed, and the booking of
    /// a paid order must not fail because minutes have passed since.
    /// </param>
    public Result<VerifiedDeliveryOffer> Read(string token, bool checkExpiry = true)
    {
        var parts = token.Split('.');

        if (parts.Length != 2 || !Base64Url.IsValid(parts[0]) || !Base64Url.IsValid(parts[1]))
        {
            return Invalid;
        }

        var payload = Base64Url.DecodeFromChars(parts[0]);

        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_key, payload), Base64Url.DecodeFromChars(parts[1])))
        {
            return Invalid;
        }

        var offer = JsonSerializer.Deserialize<VerifiedDeliveryOffer>(payload)!;
        return checkExpiry && offer.ExpiresAt <= clock.GetUtcNow() ? Expired : offer;
    }
}

/// <summary>Registry of the enabled carriers. In Azure this is a table of the service; here it is configuration.</summary>
internal sealed class CarrierRegistry(IEnumerable<ICarrier> carriers, IOptions<CarriersOptions> options)
{
    private readonly Dictionary<string, ICarrier> _byName =
        carriers.ToDictionary(carrier => carrier.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ICarrier> Enabled =>
        [.. options.Value.Enabled.Where(_byName.ContainsKey).Select(name => _byName[name])];

    /// <summary>Used only for a shipment requested without a delivery offer.</summary>
    public ICarrier? Default => Enabled is [var first, ..] ? first : null;

    public ICarrier? Find(string name) =>
        options.Value.Enabled.Contains(name, StringComparer.OrdinalIgnoreCase) ? _byName.GetValueOrDefault(name) : null;
}

internal sealed class DeliveryOffersApi(
    CarrierRegistry carriers,
    DeliveryOfferTokens tokens,
    IOptions<CarriersOptions> options,
    TimeProvider clock,
    IEventPublisher? events = null)
    : IDeliveryOffersApi
{
    // The answer is published, not returned: the caller is another service (ADR-0003). Asking again with
    // the same request id publishes offers with the same event id, so a redelivery is one answer.
    public async Task RequestOffersAsync(DeliveryOffersQuoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        var offers = await GetOffersAsync(request.Request, cancellationToken);
        await events.PublishAsync(
            new DeliveryOffersQuoted(request.RequestId, request.CustomerId, offers).WithIdFor(request.RequestId),
            cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryOffer>> GetOffersAsync(
        DeliveryOffersRequest request, CancellationToken cancellationToken)
    {
        // All carriers are asked at the same time, so the answer takes as long as the slowest one that
        // is still inside its timeout, not the sum of all of them.
        var answers = await Task.WhenAll(carriers.Enabled.Select(carrier => AskAsync(carrier, request, cancellationToken)));

        // Deliberately no sorting and no "best" offer: carriers are listed in registry order.
        return [.. answers.SelectMany(offers => offers)];
    }

    public Result<VerifiedDeliveryOffer> Verify(string token) => tokens.Read(token);

    private async Task<IReadOnlyList<DeliveryOffer>> AskAsync(
        ICarrier carrier, DeliveryOffersRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<CarrierQuote> quotes;

        try
        {
            quotes = await carrier.GetQuotesAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException)
        {
            // One carrier being down must not hide the offers of the others.
            return [];
        }

        var now = clock.GetUtcNow();
        var expiresAt = now.AddMinutes(options.Value.OfferValidityMinutes);

        return [.. quotes.Select(quote =>
        {
            var offer = new VerifiedDeliveryOffer(
                carrier.Name, quote.Service, quote.Type, quote.PointId, quote.Price, quote.Currency,
                request.DestinationCountry.ToUpperInvariant(), expiresAt);

            return new DeliveryOffer(
                carrier.Name, quote.Service, quote.Type, quote.PointId, quote.PointName, quote.Price, quote.Currency,
                DateOnly.FromDateTime(now.UtcDateTime).AddDays(quote.DeliveryDays), expiresAt, tokens.Sign(offer));
        })];
    }
}
