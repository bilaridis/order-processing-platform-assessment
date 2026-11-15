using Microsoft.Extensions.Options;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Pricing;
using OrderProcessing.Pricing.Contracts;

namespace OrderProcessing.Modules.UnitTests.Pricing;

public class PricingApiTests
{
    private static readonly PricingOptions Settings = new()
    {
        FlatShippingFee = 5m,
        FreeShippingThreshold = 50m,
        DefaultTaxRate = 0.20m,
        TaxRates = { ["GR"] = 0.24m },
    };

    private readonly PricingApi _pricing = new(
        new FakeProductPriceSource(),
        [new DestinationTaxRule(Options.Create(Settings)), new ShippingChargeRule(Options.Create(Settings))]);

    [Fact]
    public async Task Tax_is_calculated_on_goods_plus_charges_at_the_destination_rate()
    {
        var quote = (await _pricing.QuoteAsync(Request("GR", quantity: 2), CancellationToken.None)).Value;

        Assert.Equal(20m, quote.Subtotal);
        Assert.Equal(5m, quote.Components.Single(component => component.Code == "shipping").Amount);
        Assert.Equal(6m, quote.Components.Single(component => component.Kind == PriceComponentKind.Tax).Amount);
        Assert.Equal(31m, quote.Total);
    }

    [Fact]
    public async Task Shipping_is_free_from_the_threshold_and_unknown_countries_use_the_default_rate()
    {
        var quote = (await _pricing.QuoteAsync(Request("ZZ", quantity: 5), CancellationToken.None)).Value;

        Assert.DoesNotContain(quote.Components, component => component.Code == "shipping");
        Assert.Equal(60m, quote.Total);
    }

    [Fact]
    public async Task A_product_without_a_price_fails_the_quote()
    {
        var request = new PriceQuoteRequest(Guid.NewGuid(), "EUR", "GR", [new PriceQuoteItem("UNKNOWN-1", 1)]);

        var result = await _pricing.QuoteAsync(request, CancellationToken.None);

        Assert.Equal("pricing.unknown_product", result.Error?.Code);
    }

    [Fact]
    public async Task The_delivery_fee_is_the_price_of_the_offer_the_customer_chose()
    {
        var offer = new VerifiedDeliveryOffer("boxnow", "Locker", DeliveryType.Locker, "BN-1", 1.90m, "EUR", "GR", DateTimeOffset.MaxValue);
        var pricing = WithOffers(new StubOffers(offer));

        var quote = (await pricing.QuoteAsync(Request("GR", quantity: 6) with { DeliveryOfferToken = "t" }, CancellationToken.None)).Value;

        // 60.00 of goods is above the free-shipping threshold, but a chosen offer always costs what the carrier quoted.
        Assert.Equal(1.90m, quote.Components.Single(component => component.Code == "shipping").Amount);
    }

    [Fact]
    public async Task An_offer_that_fails_verification_or_was_made_for_another_destination_fails_the_quote()
    {
        var forGermany = new VerifiedDeliveryOffer("acs", "Home", DeliveryType.Home, null, 3.90m, "EUR", "DE", DateTimeOffset.MaxValue);
        var request = Request("GR", quantity: 1) with { DeliveryOfferToken = "t" };

        var rejected = await WithOffers(new StubOffers(Error.Validation("shipping.delivery_offer_expired", "expired"))).QuoteAsync(request, CancellationToken.None);
        var mismatch = await WithOffers(new StubOffers(forGermany)).QuoteAsync(request, CancellationToken.None);

        Assert.Equal("shipping.delivery_offer_expired", rejected.Error?.Code);
        Assert.Equal("pricing.delivery_offer_mismatch", mismatch.Error?.Code);
    }

    [Fact]
    public async Task A_quote_is_requested_by_command_and_answered_by_an_event()
    {
        var events = new RecordingPublisher();
        var pricing = new PricingApi(new FakeProductPriceSource(), [], null, events);
        var priced = Request("GR", quantity: 2);
        var unknown = new PriceQuoteRequest(Guid.NewGuid(), "EUR", "GR", [new PriceQuoteItem("UNKNOWN-1", 1)]);

        await pricing.RequestQuoteAsync(priced, CancellationToken.None);
        await pricing.RequestQuoteAsync(unknown, CancellationToken.None);

        Assert.Equal(20m, Assert.IsType<PriceQuoted>(events.Events[0]).Quote.Total);
        Assert.Equal("pricing.unknown_product", Assert.IsType<PriceQuoteRejected>(events.Events[1]).Code);
    }

    private static PricingApi WithOffers(IDeliveryOffersApi offers) => new(
        new FakeProductPriceSource(),
        [new DestinationTaxRule(Options.Create(Settings)), new ShippingChargeRule(Options.Create(Settings))],
        offers);

    private sealed class StubOffers(Result<VerifiedDeliveryOffer> verification) : IDeliveryOffersApi
    {
        public Task RequestOffersAsync(DeliveryOffersQuoteRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Result<VerifiedDeliveryOffer> Verify(string token) => verification;
    }

    private static PriceQuoteRequest Request(string country, int quantity) =>
        new(Guid.NewGuid(), "EUR", country, [new PriceQuoteItem("SKU-1", quantity)]);
}
