using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Pricing.Contracts;

namespace OrderProcessing.Pricing;

/// <summary>Port to wherever list prices live (product catalogue / price list service).</summary>
internal interface IProductPriceSource
{
    Task<ProductPrice?> FindAsync(string sku, string currency, CancellationToken cancellationToken);
}

internal sealed record ProductPrice(string Sku, string Name, decimal UnitPrice);

/// <summary>
/// Extension point. Each rule contributes zero or more components (charge, tax, discount) to a quote.
/// Rules run in ascending <see cref="Order"/> and see the components added before them, so a tax rule can
/// tax the shipping charge. Adding a new charge or promotion is a new class plus one DI registration.
/// </summary>
internal interface IPricingRule
{
    int Order { get; }

    IEnumerable<PriceComponent> Apply(PricingContext context);
}

internal sealed record PricingContext(
    string Currency,
    string DestinationCountry,
    IReadOnlyList<PricedItem> Items,
    decimal Subtotal,
    IReadOnlyList<PriceComponent> ComponentsSoFar,
    VerifiedDeliveryOffer? DeliveryOffer = null)
{
    public decimal RunningTotal => Subtotal + ComponentsSoFar.Sum(component => component.Amount);
}

internal static class Rounding
{
    /// <summary>Single rounding policy for all money in a quote. Two decimals is a skeleton simplification.</summary>
    public static decimal Money(decimal amount) => Math.Round(amount, 2, MidpointRounding.ToEven);
}
