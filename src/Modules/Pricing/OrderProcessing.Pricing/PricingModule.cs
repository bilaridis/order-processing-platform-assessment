using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.BuildingBlocks.Infrastructure;
using OrderProcessing.Pricing.Contracts;

namespace OrderProcessing.Pricing;

public sealed class PricingModule : IModule
{
    public string Name => "Pricing";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PricingOptions>(configuration.GetSection(PricingOptions.SectionName));
        services.AddSingleton<IProductPriceSource, FakeProductPriceSource>();
        services.AddSingleton<IPricingRule, ShippingChargeRule>();
        services.AddSingleton<IPricingRule, DestinationTaxRule>();
        services.AddScoped<IPricingApi, PricingApi>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // A "POST /quotes" endpoint for basket previews would be mapped here.
    }
}
