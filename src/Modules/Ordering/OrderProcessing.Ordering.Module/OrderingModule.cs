using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.BuildingBlocks.Infrastructure;
using OrderProcessing.Ordering.Application;
using OrderProcessing.Ordering.Contracts;
using OrderProcessing.Ordering.Infrastructure;

namespace OrderProcessing.Ordering.Module;

/// <summary>
/// Entry point of the Ordering service. It has no HTTP surface: it receives commands
/// (<see cref="IOrderingApi"/>) and events, and publishes events. The public API is the Order API (ADR-0013).
/// </summary>
public sealed class OrderingModule : IModule
{
    public string Name => "Ordering";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOrderingInfrastructure(configuration);

        services.AddScoped<OrderStore>();
        services.AddScoped<ICommandHandler<PlaceOrder, Result>, PlaceOrderHandler>();
        services.AddScoped<ICommandHandler<CancelOrder, Result>, CancelOrderHandler>();
        services.AddScoped<IOrderingApi, OrderingApi>();
        services.AddIntegrationEventHandler<OrderFulfillmentProcess>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Nothing: no service is called over HTTP by another service.
    }
}
