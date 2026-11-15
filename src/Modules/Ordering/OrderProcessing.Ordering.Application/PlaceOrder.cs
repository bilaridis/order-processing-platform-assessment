using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.Ordering.Domain;

namespace OrderProcessing.Ordering.Application;

/// <param name="OrderId">Chosen by the sender of the command (the Order API). It makes the command idempotent.</param>
internal sealed record PlaceOrder(
    OrderId OrderId,
    string CustomerId,
    string Currency,
    IReadOnlyList<PlaceOrderItem> Items,
    ShippingAddress ShippingAddress,
    string PaymentMethod,
    string? DeliveryOfferToken = null,
    ContactDetails? Contact = null,
    string? PaymentMethodToken = null) : ICommand<Result>;

internal sealed record PlaceOrderItem(string Sku, int Quantity);

/// <summary>
/// Receives an order (ADR-0005). It only records it as Submitted. Nothing is checked here and no other
/// service is called: the price and the stock are requested over Service Bus by
/// <see cref="OrderFulfillmentProcess"/>, driven by the OrderSubmitted event, and the order is accepted
/// or rejected when both have answered.
/// </summary>
internal sealed class PlaceOrderHandler(IOrderRepository orders, OrderStore store, TimeProvider clock)
    : ICommandHandler<PlaceOrder, Result>
{
    public async Task<Result> HandleAsync(PlaceOrder command, CancellationToken cancellationToken)
    {
        if (Validate(command) is { } validationError)
        {
            return validationError;
        }

        // The same command delivered twice, or a client that retried: the order is already there.
        // Announce its status again, in case the first delivery saved it and failed to publish.
        if (await orders.FindAsync(command.OrderId, cancellationToken) is { } existing)
        {
            await store.SaveAsync(existing, cancellationToken);
            return Result.Success();
        }

        var order = Order.Submit(
            command.OrderId,
            command.CustomerId,
            command.Currency,
            command.Items.Select(item => new OrderItem(item.Sku, item.Quantity)),
            command.ShippingAddress,
            command.PaymentMethod,
            clock.GetUtcNow(),
            command.DeliveryOfferToken,
            command.Contact,
            command.PaymentMethodToken);

        if (order.IsFailure)
        {
            return order.Error;
        }

        await store.SaveAsync(order.Value, cancellationToken);
        return Result.Success();
    }

    private static Error? Validate(PlaceOrder command) =>
        command.Items.Any(item => string.IsNullOrWhiteSpace(item.Sku))
        || command.Currency is not { Length: 3 }
        || command.ShippingAddress.Country is not { Length: 2 }
        || string.IsNullOrWhiteSpace(command.PaymentMethod)
            ? Error.Validation(
                "order.invalid_request",
                "SKU, a 3-letter currency, a 2-letter country and a payment method token are required.")
            : null;
}
