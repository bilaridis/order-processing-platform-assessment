using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Pricing.Contracts;

namespace OrderProcessing.Pricing;

internal sealed class PricingApi(
    IProductPriceSource prices,
    IEnumerable<IPricingRule> rules,
    IDeliveryOffersApi? deliveryOffers = null,
    IEventPublisher? events = null) : IPricingApi
{
    public async Task RequestQuoteAsync(PriceQuoteRequest request, CancellationToken cancellationToken)
    {
        var quote = await QuoteAsync(request, cancellationToken);

        // Pricing keeps no state: asked twice, it calculates twice and gives the same answer with the same id.
        await (events ?? throw new InvalidOperationException("Pricing has no publisher to answer on.")).PublishAsync(
            quote.IsSuccess
                ? new PriceQuoted(request.OrderId, quote.Value).WithIdFor(request.OrderId)
                : new PriceQuoteRejected(request.OrderId, quote.Error.Code, quote.Error.Message).WithIdFor(request.OrderId),
            cancellationToken);
    }

    /// <summary>The calculation itself. Pure: it reads prices and rules and writes nothing.</summary>
    public async Task<Result<PriceQuote>> QuoteAsync(PriceQuoteRequest request, CancellationToken cancellationToken)
    {
        VerifiedDeliveryOffer? deliveryOffer = null;

        if (request.DeliveryOfferToken is not null)
        {
            var verified = deliveryOffers?.Verify(request.DeliveryOfferToken);

            if (verified is not { IsSuccess: true })
            {
                return verified?.Error
                    ?? Error.Validation("pricing.delivery_offer_invalid", "Delivery offers cannot be verified.");
            }

            deliveryOffer = verified.Value;

            // An offer is made for one destination in one currency; it must not be reused for another.
            if (!string.Equals(deliveryOffer.Currency, request.Currency, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(deliveryOffer.DestinationCountry, request.DestinationCountry, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Validation(
                    "pricing.delivery_offer_mismatch", "The delivery offer was made for another destination or currency.");
            }
        }

        var items = new List<PricedItem>(request.Items.Count);

        foreach (var item in request.Items)
        {
            var price = await prices.FindAsync(item.Sku, request.Currency, cancellationToken);
            if (price is null)
            {
                return Error.Validation(
                    "pricing.unknown_product", $"Product '{item.Sku}' has no price in {request.Currency}.");
            }

            items.Add(new PricedItem(
                price.Sku, price.Name, item.Quantity, price.UnitPrice, Rounding.Money(price.UnitPrice * item.Quantity)));
        }

        var subtotal = items.Sum(item => item.LineTotal);
        var components = new List<PriceComponent>();

        foreach (var rule in rules.OrderBy(rule => rule.Order))
        {
            var context = new PricingContext(request.Currency, request.DestinationCountry, items, subtotal, components, deliveryOffer);
            components.AddRange(rule.Apply(context).ToList());
        }

        var total = subtotal + components.Sum(component => component.Amount);
        return new PriceQuote(request.Currency, items, subtotal, components, total);
    }
}

/// <summary>SKELETON PLACEHOLDER. Every SKU costs 10.00 in any currency, except "UNKNOWN-" SKUs.</summary>
internal sealed class FakeProductPriceSource : IProductPriceSource
{
    public Task<ProductPrice?> FindAsync(string sku, string currency, CancellationToken cancellationToken) =>
        Task.FromResult(sku.StartsWith("UNKNOWN-", StringComparison.OrdinalIgnoreCase)
            ? null
            : new ProductPrice(sku, $"Product {sku}", 10.00m));
}
