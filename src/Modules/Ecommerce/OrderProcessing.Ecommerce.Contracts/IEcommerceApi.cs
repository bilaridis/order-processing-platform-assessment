using OrderProcessing.BuildingBlocks.Application;

namespace OrderProcessing.Ecommerce.Contracts;

/// <summary>
/// Public, in-process API of the Ecommerce module. Both operations are requests: the outcome is reported
/// asynchronously with <see cref="PaymentAuthorized"/> or <see cref="PaymentFailed"/>, because real payment
/// providers confirm out of band (3-D Secure, webhooks) and may take seconds or minutes.
/// </summary>
public interface IEcommerceApi
{
    /// <summary>Idempotent per <see cref="AuthorizePaymentRequest.OrderId"/>.</summary>
    Task RequestAuthorizationAsync(AuthorizePaymentRequest request, CancellationToken cancellationToken);

    /// <summary>Voids the authorization or refunds the capture of an order. Idempotent; no-op if nothing was paid.</summary>
    Task RequestReversalAsync(Guid orderId, CancellationToken cancellationToken);
}

/// <param name="PaymentMethod">How the customer pays: card, wallet. It selects the provider.</param>
/// <param name="PaymentMethodToken">
/// Optional token from the provider or a wallet for a method that is already authenticated or saved.
/// It holds no card number: raw card data never reaches this platform.
/// </param>
public sealed record AuthorizePaymentRequest(
    Guid OrderId, decimal Amount, string Currency, string PaymentMethod, string? PaymentMethodToken = null);

/// <summary>The command behind <see cref="IEcommerceApi.RequestReversalAsync"/> when it travels as a message.</summary>
public sealed record ReversePaymentRequest(Guid OrderId);

/// <summary>Where the commands of this service travel between services (ADR-0003).</summary>
public static class EcommerceMessaging
{
    public const string CommandQueue = "payments-commands";
    public const string AuthorizePayment = nameof(AuthorizePayment);
    public const string ReversePayment = nameof(ReversePayment);
}

public sealed record PaymentAuthorized(Guid OrderId, string PaymentReference) : IntegrationEvent;

public sealed record PaymentFailed(Guid OrderId, string Reason) : IntegrationEvent;
