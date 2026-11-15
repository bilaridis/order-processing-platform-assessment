using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.Ordering.Application;

namespace OrderProcessing.Ordering.Infrastructure;

public static class OrderingInfrastructure
{
    public static IServiceCollection AddOrderingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // configuration will carry the module's connection string once the EF Core repository exists.
        _ = configuration;
        services.AddSingleton<IOrderRepository, InMemoryOrderRepository>();
        return services;
    }
}
