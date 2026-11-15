using System.Security.Claims;
using HotChocolate;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.BuildingBlocks.Infrastructure;
using OrderProcessing.Ordering.Contracts;

namespace OrderProcessing.OrderApi;

// The GraphQL surface of the Order API (ADR-0013). Resolvers are thin: read the caller from the token,
// call the same command and query handlers for both schemas, and shape the answer. A resolver never
// decides anything about an order.
//
// These types are public because the GraphQL server only builds a schema from public types. Other
// services still cannot use them: they may reference Contracts projects only (boundary tests).

// The GraphQL server binds resolvers to instance methods of the type it is given.
#pragma warning disable CA1822 // Mark members as static

/// <summary>Queries of the customer API: the caller's own orders, read from the order views.</summary>
[GraphQLName("Query")]
public sealed class CustomerQueries
{
    [GraphQLDescription("One of your orders, with its status, payment, carrier detail and timeline. Null if it is not yours.")]
    public async Task<OrderView?> OrderAsync(
        Guid id,
        ClaimsPrincipal user,
        [Service] IQueryHandler<GetOrderView, Result<OrderView>> getOrder,
        CancellationToken cancellationToken)
    {
        Caller.Require(user, OrderApiModule.ReadScope);
        var result = await getOrder.HandleAsync(new GetOrderView(id, Caller.Id(user), CanAccessAnyOrder: false), cancellationToken);
        return result.IsSuccess ? result.Value : null;
    }

    [GraphQLDescription("Your orders, newest first.")]
    public async Task<IReadOnlyList<OrderListItem>> MyOrdersAsync(
        ClaimsPrincipal user,
        [Service] IQueryHandler<ListMyOrders, Result<IReadOnlyList<OrderListItem>>> listOrders,
        CancellationToken cancellationToken,
        int take = 20)
    {
        Caller.Require(user, OrderApiModule.ReadScope);
        return (await listOrders.HandleAsync(new ListMyOrders(Caller.Id(user), take), cancellationToken)).Value;
    }

    [GraphQLDescription("The delivery offers you asked for with requestDeliveryOffers. Status Requested until the carriers have answered, then Ready. Null if the request is not yours.")]
    public async Task<DeliveryOffersView?> DeliveryOffersAsync(
        Guid requestId,
        ClaimsPrincipal user,
        [Service] IQueryHandler<GetDeliveryOffers, Result<DeliveryOffersView>> getOffers,
        CancellationToken cancellationToken)
    {
        Caller.Require(user, OrderApiModule.ReadScope);
        var result = await getOffers.HandleAsync(new GetDeliveryOffers(Caller.Id(user), requestId), cancellationToken);
        return result.IsSuccess ? result.Value : null;
    }
}

/// <summary>
/// Mutations of the customer API. A mutation queues a command and answers at once: the order it returns
/// is "received" (<c>Submitted</c>), not accepted. The outcome is read with the <c>order</c> query.
/// </summary>
[GraphQLName("Mutation")]
public sealed class CustomerMutations
{
    [GraphQLDescription("Send an order. It is received with status Submitted; price and stock are checked afterwards.")]
    public async Task<PlaceOrderPayload> PlaceOrderAsync(
        PlaceOrderInput input,
        ClaimsPrincipal user,
        [Service] ICommandHandler<SubmitOrder, Result<OrderView>> submitOrder,
        CancellationToken cancellationToken)
    {
        Caller.Require(user, OrderApiModule.WriteScope);

        var result = await submitOrder.HandleAsync(
            new SubmitOrder(
                Caller.Id(user),
                input.IdempotencyKey,
                input.Currency,
                input.Items,
                input.ShippingAddress,
                input.PaymentMethod,
                input.DeliveryOfferToken,
                input.Contact,
                input.PaymentMethodToken),
            cancellationToken);

        return result.IsSuccess
            ? new PlaceOrderPayload(result.Value, Error: null)
            : new PlaceOrderPayload(Order: null, new UserError(result.Error.Code, result.Error.Message));
    }

    [GraphQLDescription("Ask every enabled carrier what it offers for this basket and destination. The answer is read with the deliveryOffers query; the platform lists the offers and does not choose one.")]
    public async Task<RequestDeliveryOffersPayload> RequestDeliveryOffersAsync(
        DeliveryOffersInput input,
        ClaimsPrincipal user,
        [Service] ICommandHandler<RequestDeliveryOffers, Result<DeliveryOffersView>> requestOffers,
        CancellationToken cancellationToken)
    {
        Caller.Require(user, OrderApiModule.WriteScope);

        var result = await requestOffers.HandleAsync(
            new RequestDeliveryOffers(Caller.Id(user), input.DestinationCountry, input.PostalCode, input.Items),
            cancellationToken);

        return result.IsSuccess
            ? new RequestDeliveryOffersPayload(result.Value, Error: null)
            : new RequestDeliveryOffersPayload(DeliveryOffers: null, new UserError(result.Error.Code, result.Error.Message));
    }

    [GraphQLDescription("Ask to cancel one of your orders. Whether it can be cancelled is decided by Ordering and shows in the order's timeline.")]
    public Task<CancelOrderPayload> CancelOrderAsync(
        Guid orderId,
        string? reason,
        ClaimsPrincipal user,
        [Service] IOrderingApi ordering,
        CancellationToken cancellationToken)
    {
        Caller.Require(user, OrderApiModule.WriteScope);
        return OrderCancellation.RequestAsync(ordering, orderId, Caller.Id(user), canAccessAnyOrder: false, reason, cancellationToken);
    }
}

/// <summary>Queries of the back-office API: any order, for staff.</summary>
[GraphQLName("Query")]
public sealed class BackOfficeQueries
{
    [GraphQLDescription("Any order by id.")]
    public async Task<OrderView?> OrderAsync(
        Guid id,
        ClaimsPrincipal user,
        [Service] IQueryHandler<GetOrderView, Result<OrderView>> getOrder,
        CancellationToken cancellationToken)
    {
        Caller.Require(user, OrderApiModule.ManageScope);
        var result = await getOrder.HandleAsync(new GetOrderView(id, Caller.Id(user), CanAccessAnyOrder: true), cancellationToken);
        return result.IsSuccess ? result.Value : null;
    }

    [GraphQLDescription("The orders of one customer, newest first.")]
    public async Task<IReadOnlyList<OrderListItem>> OrdersOfCustomerAsync(
        string customerId,
        ClaimsPrincipal user,
        [Service] IQueryHandler<ListMyOrders, Result<IReadOnlyList<OrderListItem>>> listOrders,
        CancellationToken cancellationToken,
        int take = 20)
    {
        Caller.Require(user, OrderApiModule.ManageScope);
        return (await listOrders.HandleAsync(new ListMyOrders(customerId, take), cancellationToken)).Value;
    }
}

/// <summary>Mutations of the back-office API.</summary>
[GraphQLName("Mutation")]
public sealed class BackOfficeMutations
{
    [GraphQLDescription("Ask to cancel any order on behalf of the customer. Ordering still decides whether it can be cancelled.")]
    public Task<CancelOrderPayload> CancelOrderAsync(
        Guid orderId,
        string? reason,
        ClaimsPrincipal user,
        [Service] IOrderingApi ordering,
        CancellationToken cancellationToken)
    {
        Caller.Require(user, OrderApiModule.ManageScope);
        return OrderCancellation.RequestAsync(ordering, orderId, Caller.Id(user), canAccessAnyOrder: true, reason, cancellationToken);
    }
}

/// <param name="IdempotencyKey">The same key from the same customer names the same order, so a retry is safe.</param>
public sealed record PlaceOrderInput(
    string Currency,
    IReadOnlyList<OrderSubmittedItem> Items,
    OrderSummaryDestination ShippingAddress,
    [property: GraphQLDescription("How the customer will pay, for example card or wallet. Never card data: the card is entered on the provider's page.")] string PaymentMethod,
    string? DeliveryOfferToken = null,
    CustomerContact? Contact = null,
    string? IdempotencyKey = null,
    [property: GraphQLDescription("Optional. A token from the payment provider or a wallet for a method that is already authenticated or saved. It holds no card number.")] string? PaymentMethodToken = null);

public sealed record DeliveryOffersInput(string DestinationCountry, string PostalCode, IReadOnlyList<OrderSubmittedItem> Items);

/// <summary>Either the request, to be read with the deliveryOffers query, or the reason it was not taken.</summary>
public sealed record RequestDeliveryOffersPayload(DeliveryOffersView? DeliveryOffers, UserError? Error);

/// <summary>Either the received order or the reason the request was not taken. Never both.</summary>
public sealed record PlaceOrderPayload(OrderView? Order, UserError? Error);

/// <param name="Accepted">The request was queued. It does not mean the order was cancelled.</param>
public sealed record CancelOrderPayload(Guid OrderId, bool Accepted);

/// <param name="Code">Stable; clients branch on it. The same codes as the events and the timeline.</param>
public sealed record UserError(string Code, string Message);

internal static class OrderCancellation
{
    // Whether the order can still be cancelled is Ordering's rule, not this API's. The answer
    // (OrderCancelled or OrderCancellationRefused) shows in the view.
    public static async Task<CancelOrderPayload> RequestAsync(
        IOrderingApi ordering, Guid orderId, string callerId, bool canAccessAnyOrder, string? reason, CancellationToken cancellationToken)
    {
        await ordering.RequestCancellationAsync(new CancelOrderRequest(orderId, callerId, canAccessAnyOrder, reason), cancellationToken);
        return new CancelOrderPayload(orderId, Accepted: true);
    }
}

internal static class Caller
{
    public static string Id(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue("sub")
        ?? throw new InvalidOperationException("The access token has no subject claim.");

    /// <summary>The endpoint lets a token in; each operation still needs its own scope.</summary>
    public static void Require(ClaimsPrincipal user, string scope)
    {
        if (!user.HasScope(scope))
        {
            throw new GraphQLException(
                ErrorBuilder.New().SetMessage($"The scope '{scope}' is required.").SetCode("AUTH_NOT_AUTHORIZED").Build());
        }
    }
}
