using Microsoft.Extensions.Options;
using OrderProcessing.Carriers;
using OrderProcessing.Carriers.Contracts;

namespace OrderProcessing.Modules.UnitTests.Carriers;

/// <summary>The carrier service lists what the carriers offer. It does not choose for the customer.</summary>
public class DeliveryOffersTests
{
    private static readonly ShipmentAddress Address = new("Ada Lovelace", "1 Main St", null, "Athens", "10431", "GR");

    private readonly CarriersOptions _options = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero));
    private readonly RecordingPublisher _published = new();
    private readonly InMemoryShipmentStore _shipments = new();
    private readonly DeliveryOfferTokens _tokens;
    private readonly CarrierRegistry _carriers;
    private readonly DeliveryOffersApi _offers;
    private readonly CarriersApi _shipping;

    public DeliveryOffersTests()
    {
        var options = Options.Create(_options);
        _tokens = new DeliveryOfferTokens(options, _clock);
        _carriers = new CarrierRegistry(
            [ExampleCarriers.CreateAcs(), ExampleCarriers.CreateBoxNow(), ExampleCarriers.CreateSkroutzPoint(), new DownCarrier()],
            options);
        _offers = new DeliveryOffersApi(_carriers, _tokens, options, _clock, _published);
        _shipping = new CarriersApi(_carriers, _tokens, _shipments, _published);
    }

    [Fact]
    public async Task Every_carrier_that_answers_is_listed_in_registry_order_without_ranking()
    {
        _options.Enabled.Add("down");

        var offers = await _offers.GetOffersAsync(Request(quantity: 1), CancellationToken.None);

        // ACS is the most expensive and still comes first; the carrier that is down is simply missing.
        Assert.Equal([ExampleCarriers.Acs, ExampleCarriers.BoxNow, ExampleCarriers.SkroutzPoint], offers.Select(offer => offer.Carrier));
        Assert.Equal([3.90m, 1.90m, 1.50m], offers.Select(offer => offer.Price));
    }

    [Fact]
    public async Task A_request_from_the_Order_API_is_answered_with_an_event_for_the_customer_who_asked()
    {
        var requestId = Guid.NewGuid();

        await _offers.RequestOffersAsync(new DeliveryOffersQuoteRequest(requestId, "customer-1", Request(1)), CancellationToken.None);

        var quoted = Assert.IsType<DeliveryOffersQuoted>(Assert.Single(_published.Events));
        Assert.Equal((requestId, "customer-1", 3), (quoted.RequestId, quoted.CustomerId, quoted.Offers.Count));
    }

    [Fact]
    public async Task A_carrier_that_cannot_serve_the_parcel_makes_no_offer()
    {
        var offers = await _offers.GetOffersAsync(Request(quantity: 6), CancellationToken.None);

        Assert.DoesNotContain(offers, offer => offer.Carrier == ExampleCarriers.BoxNow);
    }

    [Fact]
    public async Task A_token_gives_back_exactly_what_was_offered()
    {
        var offer = (await _offers.GetOffersAsync(Request(1), CancellationToken.None))[1];

        var verified = _offers.Verify(offer.Token).Value;

        Assert.Equal((ExampleCarriers.BoxNow, 1.90m, "EUR", "GR"), (verified.Carrier, verified.Price, verified.Currency, verified.DestinationCountry));
    }

    [Fact]
    public async Task A_token_that_was_changed_or_signed_elsewhere_is_rejected()
    {
        var offer = (await _offers.GetOffersAsync(Request(1), CancellationToken.None))[0];
        var cheaper = new DeliveryOfferTokens(Options.Create(new CarriersOptions()), _clock)
            .Sign(_offers.Verify(offer.Token).Value with { Price = 0m });
        var forged = $"{cheaper.Split('.')[0]}.{offer.Token.Split('.')[1]}";

        Assert.Equal(DeliveryOfferTokens.Invalid, _offers.Verify(cheaper).Error);
        Assert.Equal(DeliveryOfferTokens.Invalid, _offers.Verify(forged).Error);
        Assert.Equal(DeliveryOfferTokens.Invalid, _offers.Verify("not a token").Error);
    }

    [Fact]
    public async Task An_offer_cannot_be_used_after_it_expires()
    {
        var offer = (await _offers.GetOffersAsync(Request(1), CancellationToken.None))[0];

        _clock.Advance(TimeSpan.FromMinutes(_options.OfferValidityMinutes + 1));

        Assert.Equal(DeliveryOfferTokens.Expired, _offers.Verify(offer.Token).Error);
    }

    [Fact]
    public async Task The_shipment_is_booked_with_the_chosen_carrier_even_after_the_offer_expired()
    {
        var offer = (await _offers.GetOffersAsync(Request(1), CancellationToken.None))[2];
        var orderId = Guid.NewGuid();
        _clock.Advance(TimeSpan.FromHours(2));

        await _shipping.RequestShipmentAsync(new ShipmentRequest(orderId, Address, [new ShipmentItem("SKU-1", 1)], offer.Token), CancellationToken.None);

        Assert.True(_shipments.TryRemove(orderId, out var carrier));
        Assert.Equal(ExampleCarriers.SkroutzPoint, carrier);
        Assert.Empty(_published.Events);
    }

    [Fact]
    public async Task A_refusal_by_the_chosen_carrier_fails_the_shipment_instead_of_switching_carrier()
    {
        var offer = (await _offers.GetOffersAsync(Request(1), CancellationToken.None))[1];
        var orderId = Guid.NewGuid();

        await _shipping.RequestShipmentAsync(
            new ShipmentRequest(orderId, Address with { Recipient = "REFUSE" }, [new ShipmentItem("SKU-1", 1)], offer.Token),
            CancellationToken.None);

        var failed = Assert.IsType<ShipmentFailed>(Assert.Single(_published.Events));
        Assert.Equal(ExampleCarriers.BoxNow, failed.Carrier);
        Assert.False(_shipments.Exists(orderId));
    }

    private static DeliveryOffersRequest Request(int quantity) => new("GR", "10431", [new ShipmentItem("SKU-1", quantity)]);

    private sealed class DownCarrier : ICarrier
    {
        public string Name => "down";

        public Task<IReadOnlyList<CarrierQuote>> GetQuotesAsync(DeliveryOffersRequest request, CancellationToken cancellationToken) =>
            throw new TimeoutException("carrier did not answer");

        public Task<CarrierBooking> BookAsync(
            string idempotencyKey, ShipmentRequest request, VerifiedDeliveryOffer? offer, CancellationToken cancellationToken) =>
            throw new TimeoutException();

        public Task CancelAsync(string idempotencyKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
