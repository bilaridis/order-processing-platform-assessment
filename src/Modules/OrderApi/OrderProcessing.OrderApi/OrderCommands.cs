using System.Security.Cryptography;
using System.Text;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.Ordering.Contracts;

namespace OrderProcessing.OrderApi;

/// <param name="IdempotencyKey">From the client. The same key gives the same order id.</param>
public sealed record SubmitOrder(
    string CustomerId,
    string? IdempotencyKey,
    string Currency,
    IReadOnlyList<OrderSubmittedItem> Items,
    OrderSummaryDestination ShippingAddress,
    string PaymentMethod,
    string? DeliveryOfferToken,
    CustomerContact? Contact = null,
    string? PaymentMethodToken = null) : ICommand<Result<OrderView>>;

/// <summary>
/// The command side of the Order API (ADR-0013). It checks the shape of the request, chooses the order
/// id, sends the PlaceOrder command to Ordering and answers. It decides nothing about the order: whether
/// it is accepted is Ordering's answer, and it arrives in the view. The client gets 202, not 201.
/// </summary>
internal sealed class SubmitOrderHandler(IOrderingApi ordering, IOrderViewStore views, TimeProvider clock)
    : ICommandHandler<SubmitOrder, Result<OrderView>>
{
    public async Task<Result<OrderView>> HandleAsync(SubmitOrder command, CancellationToken cancellationToken)
    {
        if (Validate(command) is { } error)
        {
            return error;
        }

        var orderId = OrderIdFor(command.CustomerId, command.IdempotencyKey);

        // Send first. If the send fails the client gets an error and nothing was promised.
        // If the same request is sent twice, Ordering receives the same order id twice and keeps one order.
        await ordering.RequestPlacementAsync(
            new PlaceOrderRequest(
                orderId,
                command.CustomerId,
                command.Currency,
                command.Items,
                command.ShippingAddress,
                command.PaymentMethod,
                command.DeliveryOfferToken,
                command.Contact,
                command.PaymentMethodToken),
            cancellationToken);

        // Then make the order readable at once, before any event has come back. If the projector was
        // faster, its record is kept.
        var view = await views.FindAsync(orderId, cancellationToken);

        if (view is null)
        {
            view = OrderViews.New(orderId, command.CustomerId, command.Items, command.ShippingAddress, clock.GetUtcNow());
            await views.UpsertAsync(view, cancellationToken);
        }

        return view;
    }

    /// <summary>
    /// With an idempotency key the id is derived from customer and key, so a retry after a timeout names
    /// the same order and no lookup table is needed. Without a key every request is a new order.
    /// </summary>
    internal static Guid OrderIdFor(string customerId, string? idempotencyKey) =>
        idempotencyKey is null
            ? Guid.CreateVersion7()
            : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{customerId}|{idempotencyKey}")).AsSpan(0, 16));

    // Shape only: is this a request Ordering can work with. No rule about products, prices or stock.
    private static Error? Validate(SubmitOrder command)
    {
        if (command.Items.Count == 0)
        {
            return Error.Validation("order.no_lines", "An order must contain at least one product.");
        }

        if (command.Items.Any(item => item.Quantity <= 0))
        {
            return Error.Validation("order.invalid_quantity", "Quantities must be greater than zero.");
        }

        if (command.Items.DistinctBy(item => item.Sku, StringComparer.OrdinalIgnoreCase).Count() != command.Items.Count)
        {
            return Error.Validation("order.duplicate_sku", "Each product may appear only once in an order.");
        }

        if (command.Contact is { Email: { } email } && !email.Contains('@', StringComparison.Ordinal))
        {
            return Error.Validation("order.invalid_contact", "The email address is not valid.");
        }

        return command.Items.Any(item => string.IsNullOrWhiteSpace(item.Sku))
            || command.Currency is not { Length: 3 }
            || command.ShippingAddress.Country is not { Length: 2 }
            || string.IsNullOrWhiteSpace(command.PaymentMethod)
                ? Error.Validation(
                    "order.invalid_request",
                    "SKU, a 3-letter currency, a 2-letter country and a payment method token are required.")
                : null;
    }
}
