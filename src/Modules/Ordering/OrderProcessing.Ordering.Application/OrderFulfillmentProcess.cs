using Microsoft.Extensions.Logging;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Ecommerce.Contracts;
using OrderProcessing.Inventory.Contracts;
using OrderProcessing.Ordering.Contracts;
using OrderProcessing.Ordering.Domain;
using OrderProcessing.Pricing.Contracts;

namespace OrderProcessing.Ordering.Application;

/// <summary>
/// The process manager (orchestrated saga) for everything that happens after an order is received.
/// It is the one place that knows the sequence "price, reserve, accept, pay, ship" and the compensations,
/// so the order lifecycle can be read top to bottom in this file (ADR-0005).
///
/// It holds no state of its own: the Order aggregate is the saga state. Each handler does one step and
/// is safe to run more than once. There is no outbox (ADR-0012), so each handler decides what to send
/// from the state it finds: run a second time after a failed send, it sends again.
/// </summary>
internal sealed partial class OrderFulfillmentProcess(
    IOrderRepository orders,
    OrderStore store,
    IPricingApi pricing,
    IEcommerceApi payments,
    ICarriersApi shipping,
    IInventoryApi inventory,
    TimeProvider clock,
    ILogger<OrderFulfillmentProcess> logger) :
    IIntegrationEventHandler<OrderSubmitted>,
    IIntegrationEventHandler<PriceQuoted>,
    IIntegrationEventHandler<PriceQuoteRejected>,
    IIntegrationEventHandler<StockReserved>,
    IIntegrationEventHandler<StockReservationFailed>,
    IIntegrationEventHandler<OrderPlaced>,
    IIntegrationEventHandler<PaymentAuthorized>,
    IIntegrationEventHandler<PaymentFailed>,
    IIntegrationEventHandler<OrderConfirmed>,
    IIntegrationEventHandler<ShipmentDispatched>,
    IIntegrationEventHandler<ShipmentDelivered>,
    IIntegrationEventHandler<ShipmentFailed>,
    IIntegrationEventHandler<OrderRejected>,
    IIntegrationEventHandler<OrderCancelled>
{
    // Check 1: received → ask Pricing for the price.
    public async Task HandleAsync(OrderSubmitted integrationEvent, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(new OrderId(integrationEvent.OrderId), cancellationToken);

        if (order is { Status: OrderStatus.Submitted, Price: null })
        {
            await pricing.RequestQuoteAsync(
                new PriceQuoteRequest(
                    order.Id.Value,
                    order.Currency,
                    order.ShippingAddress.Country,
                    [.. order.Items.Select(item => new PriceQuoteItem(item.Sku, item.Quantity))],
                    order.DeliveryOfferToken),
                cancellationToken);
        }
    }

    // Check 2: priced → keep the quote as the order's price and ask Inventory for the stock.
    public async Task HandleAsync(PriceQuoted integrationEvent, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(new OrderId(integrationEvent.OrderId), cancellationToken);

        if (order is not { Status: OrderStatus.Submitted })
        {
            return;
        }

        var quote = integrationEvent.Quote;
        var priced = order.ApplyQuote(
            quote.Items.Select(item => new OrderLine(item.Sku, item.Name, item.Quantity, item.UnitPrice, item.LineTotal)),
            new OrderPrice(
                quote.Currency,
                quote.Subtotal,
                [.. quote.Components.Select(component => new OrderPriceComponent(
                    component.Code,
                    // Translate the other service's enum by name so the two can evolve independently.
                    Enum.Parse<Domain.PriceComponentKind>(component.Kind.ToString()),
                    component.Description,
                    component.Amount))],
                quote.Total));

        if (priced.IsFailure)
        {
            await ApplyAsync(order.Id.Value, o => o.Reject(priced.Error.Message, clock.GetUtcNow()), cancellationToken);
            return;
        }

        await store.SaveQuietlyAsync(order, cancellationToken);

        // Sent from the state "Submitted and priced", so a redelivered PriceQuoted asks again and
        // Inventory, which is idempotent per order, answers again.
        await inventory.RequestReservationAsync(
            new ReserveStockRequest(order.Id.Value, [.. order.Items.Select(item => new StockItem(item.Sku, item.Quantity))]),
            cancellationToken);
    }

    // Check 1 failed: unknown product, invalid delivery offer → Rejected. Nothing was reserved.
    public Task HandleAsync(PriceQuoteRejected integrationEvent, CancellationToken cancellationToken) =>
        ApplyAsync(
            integrationEvent.OrderId,
            order => order.Reject($"{integrationEvent.Code}: {integrationEvent.Reason}", clock.GetUtcNow()),
            cancellationToken);

    // Both checks passed: the order is accepted. Submitted → Pending, which publishes OrderPlaced.
    public async Task HandleAsync(StockReserved integrationEvent, CancellationToken cancellationToken)
    {
        await ApplyAsync(integrationEvent.OrderId, order => order.Accept(clock.GetUtcNow()), cancellationToken);

        // From the state: stock reserved for an order that was cancelled or rejected meanwhile goes back.
        var order = await orders.FindAsync(new OrderId(integrationEvent.OrderId), cancellationToken);

        if (order is { Status: OrderStatus.Cancelled or OrderStatus.Rejected })
        {
            await inventory.RequestReleaseAsync(integrationEvent.OrderId, cancellationToken);
        }
    }

    // Check 2 failed: not enough stock, or no source could confirm it (fail closed) → Rejected.
    public Task HandleAsync(StockReservationFailed integrationEvent, CancellationToken cancellationToken)
    {
        var missing = string.Join(", ", integrationEvent.Shortages.Select(shortage => shortage.Sku));
        var reason = missing.Length > 0 ? $"{integrationEvent.Code}: {missing}" : integrationEvent.Code;

        return ApplyAsync(integrationEvent.OrderId, order => order.Reject(reason, clock.GetUtcNow()), cancellationToken);
    }

    // Step 1: accepted → ask for payment.
    public async Task HandleAsync(OrderPlaced integrationEvent, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(new OrderId(integrationEvent.OrderId), cancellationToken);

        if (order is { Status: OrderStatus.Pending })
        {
            await payments.RequestAuthorizationAsync(
                new AuthorizePaymentRequest(order.Id.Value, order.Price!.Total, order.Price.Currency, order.PaymentMethod, order.PaymentMethodToken),
                cancellationToken);
        }
    }

    // Step 2a: paid → Confirmed.
    public async Task HandleAsync(PaymentAuthorized integrationEvent, CancellationToken cancellationToken)
    {
        await ApplyAsync(
            integrationEvent.OrderId,
            order => order.Confirm(integrationEvent.PaymentReference, clock.GetUtcNow()),
            cancellationToken);

        // Decided from the state, not from "the transition was refused": a payment that arrives for an
        // order that was cancelled or rejected meanwhile is given back. A repeat of this message for an
        // order that has moved on to Shipped must not reverse a good payment.
        var order = await orders.FindAsync(new OrderId(integrationEvent.OrderId), cancellationToken);

        if (order is { Status: OrderStatus.Cancelled or OrderStatus.Rejected })
        {
            await payments.RequestReversalAsync(integrationEvent.OrderId, cancellationToken);
        }
    }

    // Step 2b: payment failed → Rejected.
    public Task HandleAsync(PaymentFailed integrationEvent, CancellationToken cancellationToken) =>
        ApplyAsync(
            integrationEvent.OrderId,
            order => order.Reject(integrationEvent.Reason, clock.GetUtcNow()),
            cancellationToken);

    // Step 3: Confirmed → ask for shipment.
    public async Task HandleAsync(OrderConfirmed integrationEvent, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(new OrderId(integrationEvent.OrderId), cancellationToken);

        if (order is { Status: OrderStatus.Confirmed })
        {
            var address = order.ShippingAddress;

            await shipping.RequestShipmentAsync(
                new ShipmentRequest(
                    order.Id.Value,
                    new ShipmentAddress(
                        address.Recipient, address.Line1, address.Line2, address.City, address.PostalCode, address.Country),
                    [.. order.Lines.Select(line => new ShipmentItem(line.Sku, line.Quantity))],
                    order.DeliveryOfferToken),
                cancellationToken);
        }
    }

    // Step 4: carrier has it → Shipped.
    public Task HandleAsync(ShipmentDispatched integrationEvent, CancellationToken cancellationToken) =>
        ApplyAsync(
            integrationEvent.OrderId,
            order => order.MarkShipped(
                new ShipmentInfo(integrationEvent.Carrier, integrationEvent.TrackingNumber), clock.GetUtcNow()),
            cancellationToken);

    // Step 5: Shipped → Delivered.
    public Task HandleAsync(ShipmentDelivered integrationEvent, CancellationToken cancellationToken) =>
        ApplyAsync(integrationEvent.OrderId, order => order.MarkDelivered(clock.GetUtcNow()), cancellationToken);

    // The chosen carrier refused a paid order. The platform does not pick another carrier for the
    // customer (ADR-0006), and what the customer is offered next (choose again, or refund) is a business
    // decision that is still open. Until it is taken the order stays Confirmed and is flagged for a person.
    public Task HandleAsync(ShipmentFailed integrationEvent, CancellationToken cancellationToken)
    {
        LogShipmentFailed(logger, integrationEvent.OrderId, integrationEvent.Carrier, integrationEvent.Reason);
        return Task.CompletedTask;
    }

    // Compensation: a rejected order was never paid, so only the stock is released (a no-op if none was held).
    public Task HandleAsync(OrderRejected integrationEvent, CancellationToken cancellationToken) =>
        inventory.RequestReleaseAsync(integrationEvent.OrderId, cancellationToken);

    // Compensation: undo whatever had been done by the time the order was cancelled. Each call is an
    // idempotent no-op if there is nothing to undo.
    public async Task HandleAsync(OrderCancelled integrationEvent, CancellationToken cancellationToken)
    {
        await shipping.RequestCancellationAsync(integrationEvent.OrderId, cancellationToken);
        await payments.RequestReversalAsync(integrationEvent.OrderId, cancellationToken);
        await inventory.RequestReleaseAsync(integrationEvent.OrderId, cancellationToken);
    }

    /// <returns>False if the transition was not permitted in the order's current state.</returns>
    private async Task<bool> ApplyAsync(Guid orderId, Func<Order, Result> transition, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(new OrderId(orderId), cancellationToken);

        if (order is null)
        {
            LogOrderMissing(logger, orderId);
            return false;
        }

        var result = transition(order);

        if (result.IsFailure)
        {
            // Not retried: the same event would fail the same way. These need a person or a follow-up
            // process (for example a parcel dispatched for a cancelled order becomes a return).
            LogTransitionRefused(logger, orderId, order.Status, result.Error.Code);
            return false;
        }

        await store.SaveAsync(order, cancellationToken);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Received an event for unknown order {OrderId}")]
    private static partial void LogOrderMissing(ILogger logger, Guid orderId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Order {OrderId} in status {Status} refused a transition: {ErrorCode}. Manual follow-up may be needed")]
    private static partial void LogTransitionRefused(ILogger logger, Guid orderId, OrderStatus status, string errorCode);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Shipment of order {OrderId} failed at carrier {Carrier}: {Reason}. Manual follow-up is needed")]
    private static partial void LogShipmentFailed(ILogger logger, Guid orderId, string? carrier, string reason);
}
