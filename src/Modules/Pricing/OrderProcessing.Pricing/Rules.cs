using Microsoft.Extensions.Options;
using OrderProcessing.Pricing.Contracts;

namespace OrderProcessing.Pricing;

public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    public decimal FlatShippingFee { get; set; } = 4.90m;

    public decimal FreeShippingThreshold { get; set; } = 50m;

    public decimal DefaultTaxRate { get; set; } = 0.20m;

    /// <summary>Tax rate by ISO 3166-1 alpha-2 destination country.</summary>
#pragma warning disable CA2227 // Bound from configuration.
    public Dictionary<string, decimal> TaxRates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore CA2227
}

internal sealed class ShippingChargeRule(IOptions<PricingOptions> options) : IPricingRule
{
    public int Order => 100;

    public IEnumerable<PriceComponent> Apply(PricingContext context)
    {
        if (context.DeliveryOffer is { } offer)
        {
            // The customer chose a carrier offer: the fee is what that carrier quoted.
            yield return new PriceComponent(
                "shipping", PriceComponentKind.Charge, $"{offer.Service} ({offer.Carrier})", offer.Price);
        }
        else if (context.Subtotal < options.Value.FreeShippingThreshold)
        {
            yield return new PriceComponent(
                "shipping", PriceComponentKind.Charge, "Standard shipping", options.Value.FlatShippingFee);
        }
    }
}

/// <summary>
/// Placeholder tax calculation: one rate per destination country applied to goods and charges.
/// Real tax (product tax classes, exemptions, jurisdictions) is expected to be delegated to a tax engine
/// behind this same rule interface.
/// </summary>
internal sealed class DestinationTaxRule(IOptions<PricingOptions> options) : IPricingRule
{
    public int Order => 900;

    public IEnumerable<PriceComponent> Apply(PricingContext context)
    {
        var rate = options.Value.TaxRates.GetValueOrDefault(context.DestinationCountry, options.Value.DefaultTaxRate);
        var tax = Rounding.Money(context.RunningTotal * rate);

        if (tax > 0)
        {
            yield return new PriceComponent(
                "tax", PriceComponentKind.Tax, $"Tax {rate:P0} ({context.DestinationCountry})", tax);
        }
    }
}
