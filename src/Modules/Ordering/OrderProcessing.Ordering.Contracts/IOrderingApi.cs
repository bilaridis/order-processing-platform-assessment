namespace OrderProcessing.Ordering.Contracts;

/// <summary>
/// The commands of the Ordering service. Both are requests without a return value: what happened is
/// published as events (<see cref="OrderSubmitted"/>, <see cref="OrderPlaced"/>, <see cref="OrderRejected"/>,
/// <see cref="OrderCancelled"/>, <see cref="OrderCancellationRefused"/>). Between services they are
/// commands on <see cref="OrderingMessaging.CommandQueue"/>, sent by the Order API (ADR-0013).
/// </summary>
public interface IOrderingApi
{
    /// <summary>Idempotent per <see cref="PlaceOrderRequest.OrderId"/>, which the sender chooses.</summary>
    Task RequestPlacementAsync(PlaceOrderRequest request, CancellationToken cancellationToken);

    Task RequestCancellationAsync(CancelOrderRequest request, CancellationToken cancellationToken);
}

/// <param name="OrderId">
/// Chosen by the sender, so that it can answer the client before the order exists and so that a
/// repeated command is recognised.
/// </param>
/// <param name="CustomerId">From the access token the sender validated. Never from the request body.</param>
public sealed record PlaceOrderRequest(
    Guid OrderId,
    string CustomerId,
    string Currency,
    IReadOnlyList<OrderSubmittedItem> Items,
    OrderSummaryDestination ShippingAddress,
    string PaymentMethod,
    string? DeliveryOfferToken = null,
    CustomerContact? Contact = null,
    string? PaymentMethodToken = null);

/// <param name="CanAccessAnyOrder">True for a back-office caller. Ordering checks ownership itself.</param>
public sealed record CancelOrderRequest(Guid OrderId, string CallerId, bool CanAccessAnyOrder, string? Reason);

/// <summary>Where the commands of this service travel between services (ADR-0003).</summary>
public static class OrderingMessaging
{
    public const string CommandQueue = "ordering-commands";
    public const string PlaceOrder = nameof(PlaceOrder);
    public const string CancelOrder = nameof(CancelOrder);
}
