using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Infrastructure;
using OrderProcessing.Ecommerce.Contracts;

namespace OrderProcessing.Ecommerce;

public sealed class EcommerceModule : IModule
{
    public string Name => "Ecommerce";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // One adapter per provider. Swap for real ones: services.AddHttpClient<...>().AddStandardResilienceHandler();
        services.AddSingleton<IPaymentProvider>(ExampleProviders.CreateCards());
        services.AddSingleton<IPaymentProvider>(ExampleProviders.CreateWallet());
        services.AddSingleton<PaymentProviderResolver>();
        services.AddSingleton<InMemoryPaymentLedger>();
        services.AddScoped<IEcommerceApi, EcommerceApi>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The provider's signed webhook (asynchronous confirmations, chargebacks) would be mapped here
        // and translated into PaymentAuthorized / PaymentFailed, like the carrier webhook.
    }
}

internal sealed class EcommerceApi(PaymentProviderResolver providers, InMemoryPaymentLedger ledger, IEventPublisher events) : IEcommerceApi
{
    public async Task RequestAuthorizationAsync(AuthorizePaymentRequest request, CancellationToken cancellationToken)
    {
        if (ledger.TryGet(request.OrderId, out var existing))
        {
            await PublishAsync(new PaymentAuthorized(request.OrderId, existing!.Reference));
            return;
        }

        if (providers.For(request.PaymentMethod) is not { } provider)
        {
            await PublishAsync(new PaymentFailed(request.OrderId, "The payment method is not supported."));
            return;
        }

        var result = await provider.AuthorizeAsync(
            $"order-{request.OrderId:N}", request.Amount, request.Currency, request.PaymentMethod, request.PaymentMethodToken, cancellationToken);

        if (result.Approved)
        {
            ledger.Record(request.OrderId, new PaymentRecord(provider.Name, result.PaymentReference!));
            await PublishAsync(new PaymentAuthorized(request.OrderId, result.PaymentReference!));
        }
        else
        {
            await PublishAsync(new PaymentFailed(request.OrderId, result.DeclineReason ?? "Payment was not approved"));
        }

        // One outcome per order and type, so a repeated command repeats the same event id. The provider
        // is asked with an idempotency key, so a repeat after a failed publish gets the same answer.
        Task PublishAsync<TEvent>(TEvent outcome)
            where TEvent : IntegrationEvent =>
            events.PublishAsync(outcome.WithIdFor(request.OrderId), cancellationToken);
    }

    public async Task RequestReversalAsync(Guid orderId, CancellationToken cancellationToken)
    {
        // The reversal goes to the provider that took the payment, whatever the routing says today.
        if (ledger.TryRemove(orderId, out var payment) && providers.Find(payment!.Provider) is { } provider)
        {
            await provider.ReverseAsync(payment.Reference, cancellationToken);
        }
    }
}
