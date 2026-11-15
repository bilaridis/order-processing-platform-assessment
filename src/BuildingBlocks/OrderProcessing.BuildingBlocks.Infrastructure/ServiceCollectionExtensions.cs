using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderProcessing.BuildingBlocks.Application;

namespace OrderProcessing.BuildingBlocks.Infrastructure;

public static class ServiceCollectionExtensions
{
    public const string MessagingActivitySourceName = "OrderProcessing.Messaging";

    /// <summary>For the local host: everything in one process, on the in-memory bus.</summary>
    public static IServiceCollection AddBuildingBlocks(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<InMemoryInbox>();
        services.AddSingleton<InMemoryMessageBus>();
        services.AddSingleton<IEventPublisher>(provider => provider.GetRequiredService<InMemoryMessageBus>());
        services.AddHostedService(provider => provider.GetRequiredService<InMemoryMessageBus>());
        return services;
    }

    /// <summary>
    /// For a Functions host of one service. The host adds the real <see cref="IEventPublisher"/>
    /// (Service Bus) and passes each received message to <see cref="IntegrationEventConsumer"/>.
    /// </summary>
    public static IServiceCollection AddBuildingBlocksForFunctions(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<InMemoryInbox>();
        services.AddScoped<IntegrationEventConsumer>();
        return services;
    }

    /// <summary>Registers <typeparamref name="THandler"/> for every integration event it handles.</summary>
    public static IServiceCollection AddIntegrationEventHandler<THandler>(this IServiceCollection services)
        where THandler : class
    {
        services.TryAddScoped<THandler>();

        var handlerInterfaces = typeof(THandler)
            .GetInterfaces()
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>));

        foreach (var handlerInterface in handlerInterfaces)
        {
            services.AddScoped(handlerInterface, provider => provider.GetRequiredService<THandler>());
        }

        return services;
    }
}
