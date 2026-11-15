using Microsoft.Extensions.Logging;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.Ordering.Contracts;
using OrderProcessing.Ordering.Domain;

namespace OrderProcessing.Ordering.Application;

/// <summary>Port implemented by the Infrastructure project.</summary>
internal interface IOrderRepository
{
    Task<Order?> FindAsync(OrderId id, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or updates the aggregate. The persistent implementation checks <c>Order.Version</c>
    /// (optimistic concurrency); the order id is the primary key, which is what makes PlaceOrder idempotent.
    /// </summary>
    Task SaveAsync(Order order, CancellationToken cancellationToken);
}

/// <summary>
/// Saves an order and announces its status. This is the single place where the order's state is
/// translated into the module's public integration events, and where "send from the state" (ADR-0012)
/// is implemented for them.
/// </summary>
internal sealed class OrderStore(IOrderRepository repository, IEventPublisher events)
{
    /// <summary>
    /// Saves the order if it changed, then publishes one event per new transition. If nothing changed
    /// (the transition had already happened), it publishes the event of the current status again:
    /// that is the case of a message delivered a second time after the save succeeded and the publish
    /// did not. The event id is derived from the order and the version, so a repeat is recognised.
    /// </summary>
    public async Task SaveAsync(Order order, CancellationToken cancellationToken)
    {
        var changes = order.DomainEvents.OfType<OrderStatusChanged>().ToList();

        if (changes.Count > 0)
        {
            await repository.SaveAsync(order, cancellationToken);
            order.ClearDomainEvents();
        }

        // The version of a transition is its position in the order's history.
        var firstVersion = order.History.Count - Math.Max(changes.Count, 1) + 1;

        for (var version = firstVersion; version <= order.History.Count; version++)
        {
            await events.PublishAsync(ToIntegrationEvent(order, order.History[version - 1], version), cancellationToken);
        }
    }

    /// <summary>For a quote stored on the order, which is a change without a transition: nothing to announce.</summary>
    public Task SaveQuietlyAsync(Order order, CancellationToken cancellationToken) =>
        repository.SaveAsync(order, cancellationToken);

    private static IIntegrationEvent ToIntegrationEvent(Order order, OrderStatusChange change, long version)
    {
        var id = order.Id.Value;

        IntegrationEvent integrationEvent = change.Status switch
        {
            OrderStatus.Submitted => new OrderSubmitted(
                id,
                order.CustomerId,
                [.. order.Items.Select(item => new OrderSubmittedItem(item.Sku, item.Quantity))],
                OrderSummaries.Destination(order),
                version,
                order.Contact is { } contact ? new CustomerContact(contact.Email, contact.Mobile, contact.Language) : null),
            OrderStatus.Pending => new OrderPlaced(
                id, order.CustomerId, order.Price!.Total, order.Price.Currency, version, OrderSummaries.Of(order)),
            OrderStatus.Confirmed => new OrderConfirmed(id, version),
            OrderStatus.Rejected => new OrderRejected(id, change.Reason ?? string.Empty, version),
            OrderStatus.Cancelled => new OrderCancelled(id, change.Reason ?? string.Empty, version),
            OrderStatus.Shipped => new OrderShipped(id, order.Shipment!.Carrier, order.Shipment.TrackingNumber, version),
            OrderStatus.Delivered => new OrderDelivered(id, version),
            _ => throw new InvalidOperationException($"No integration event is defined for status {change.Status}."),
        };

        return integrationEvent with
        {
            EventId = IntegrationEventIds.For(id, integrationEvent.GetType(), version),
            OccurredAt = change.OccurredAt,
        };
    }
}

/// <summary>Translates the aggregate into the public summary of the module's contracts.</summary>
internal static class OrderSummaries
{
    /// <summary>Only for an order that has a price, that is from Pending on.</summary>
    public static OrderSummary Of(Order order)
    {
        var price = order.Price ?? throw new InvalidOperationException($"Order {order.Id} has no price yet.");

        return new OrderSummary(
            [.. order.Lines.Select(line => new OrderSummaryLine(line.Sku, line.Name, line.Quantity, line.UnitPrice, line.LineTotal))],
            price.Subtotal,
            [.. price.Components.Select(component =>
                new OrderSummaryCharge(component.Code, component.Kind.ToString(), component.Description, component.Amount))],
            price.Total,
            price.Currency,
            Destination(order),
            order.SubmittedAt);
    }

    public static OrderSummaryDestination Destination(Order order)
    {
        var address = order.ShippingAddress;
        return new OrderSummaryDestination(
            address.Recipient, address.Line1, address.Line2, address.City, address.PostalCode, address.Country);
    }
}

/// <summary>
/// The commands of Ordering as the other services see them. In the local host this is called in
/// process; in Azure the same two commands arrive on the <c>ordering-commands</c> queue.
/// A command has no return value, so a refusal the customer must hear about is published as an event.
/// </summary>
internal sealed partial class OrderingApi(
    ICommandHandler<PlaceOrder, Result> placeOrder,
    ICommandHandler<CancelOrder, Result> cancelOrder,
    IEventPublisher events,
    ILogger<OrderingApi> logger) : IOrderingApi
{
    public async Task RequestPlacementAsync(PlaceOrderRequest request, CancellationToken cancellationToken)
    {
        var address = request.ShippingAddress;

        var result = await placeOrder.HandleAsync(
            new PlaceOrder(
                new OrderId(request.OrderId),
                request.CustomerId,
                request.Currency,
                [.. request.Items.Select(item => new PlaceOrderItem(item.Sku, item.Quantity))],
                new ShippingAddress(address.Recipient, address.Line1, address.Line2, address.City, address.PostalCode, address.Country),
                request.PaymentMethod,
                request.DeliveryOfferToken,
                request.Contact is { } contact ? new ContactDetails(contact.Email, contact.Mobile, contact.Language) : null,
                request.PaymentMethodToken),
            cancellationToken);

        if (result.IsFailure)
        {
            // The Order API checks the shape of a request before it sends the command, so this is a
            // sender that skipped that check. There is no order to reject; retrying cannot help.
            LogPlacementRefused(logger, request.OrderId, result.Error.Code);
        }
    }

    public async Task RequestCancellationAsync(CancelOrderRequest request, CancellationToken cancellationToken)
    {
        var result = await cancelOrder.HandleAsync(
            new CancelOrder(new OrderId(request.OrderId), request.CallerId, request.CanAccessAnyOrder, request.Reason),
            cancellationToken);

        // "Not found" also covers "not yours" and is deliberately not announced to anyone.
        if (result.IsFailure && result.Error.Type != ErrorType.NotFound)
        {
            await events.PublishAsync(
                new OrderCancellationRefused(request.OrderId, result.Error.Code, result.Error.Message).WithIdFor(request.OrderId),
                cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "PlaceOrder for {OrderId} was refused: {ErrorCode}. The command is dropped")]
    private static partial void LogPlacementRefused(ILogger logger, Guid orderId, string errorCode);
}
