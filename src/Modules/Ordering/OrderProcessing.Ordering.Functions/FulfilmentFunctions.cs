using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Azure;
using OrderProcessing.BuildingBlocks.Infrastructure;
using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Ecommerce.Contracts;
using OrderProcessing.Inventory.Contracts;
using OrderProcessing.Ordering.Contracts;
using OrderProcessing.Pricing.Contracts;

namespace OrderProcessing.Ordering.Functions;

/// <summary>
/// Function app "ordering-intake", on the order intake plan (ADR-0010): the commands of the service.
/// A queue has one trigger, so PlaceOrder and CancelOrder share this function and are told apart by the
/// message subject. Each has its own handler behind <see cref="IOrderingApi"/>.
/// </summary>
public sealed class CommandFunctions(IOrderingApi ordering)
{
    [Function("OnOrderingCommand")]
    public Task OnOrderingCommandAsync(
        [ServiceBusTrigger(OrderingMessaging.CommandQueue, Connection = "ServiceBus", IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        message.Subject switch
        {
            OrderingMessaging.PlaceOrder => ordering.RequestPlacementAsync(
                ServiceBusMessages.Read<PlaceOrderRequest>(message), cancellationToken),
            OrderingMessaging.CancelOrder => ordering.RequestCancellationAsync(
                ServiceBusMessages.Read<CancelOrderRequest>(message), cancellationToken),
            _ => throw new InvalidOperationException($"Unknown ordering command '{message.Subject}'."),
        };
}

/// <summary>
/// Function app "ordering-flow", on the order flow plan (ADR-0010). One function per event the order
/// flow reacts to, each on its own subscription with a filter on the message subject, so each step
/// scales, retries and dead-letters on its own. Sessions keyed by order id keep one order's messages in order.
/// Every function does the same thing: read the event and hand it to the process manager through the inbox.
/// </summary>
public sealed class FulfilmentFunctions(IntegrationEventConsumer consumer)
{
    private const string Connection = "ServiceBus";
    private const string OrderingEvents = "ordering-events";
    private const string PricingEvents = "pricing-events";
    private const string InventoryEvents = "inventory-events";
    private const string PaymentsEvents = "payments-events";
    private const string ShippingEvents = "shipping-events";

    [Function("OnOrderSubmitted")]
    public Task OnOrderSubmittedAsync(
        [ServiceBusTrigger(OrderingEvents, "ordering-on-order-submitted", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<OrderSubmitted>(message, cancellationToken);

    [Function("OnPriceQuoted")]
    public Task OnPriceQuotedAsync(
        [ServiceBusTrigger(PricingEvents, "ordering-on-price-quoted", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<PriceQuoted>(message, cancellationToken);

    [Function("OnPriceQuoteRejected")]
    public Task OnPriceQuoteRejectedAsync(
        [ServiceBusTrigger(PricingEvents, "ordering-on-price-quote-rejected", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<PriceQuoteRejected>(message, cancellationToken);

    [Function("OnStockReserved")]
    public Task OnStockReservedAsync(
        [ServiceBusTrigger(InventoryEvents, "ordering-on-stock-reserved", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<StockReserved>(message, cancellationToken);

    [Function("OnStockReservationFailed")]
    public Task OnStockReservationFailedAsync(
        [ServiceBusTrigger(InventoryEvents, "ordering-on-stock-reservation-failed", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<StockReservationFailed>(message, cancellationToken);

    [Function("OnOrderPlaced")]
    public Task OnOrderPlacedAsync(
        [ServiceBusTrigger(OrderingEvents, "ordering-on-order-placed", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<OrderPlaced>(message, cancellationToken);

    [Function("OnPaymentAuthorized")]
    public Task OnPaymentAuthorizedAsync(
        [ServiceBusTrigger(PaymentsEvents, "ordering-on-payment-authorized", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<PaymentAuthorized>(message, cancellationToken);

    [Function("OnPaymentFailed")]
    public Task OnPaymentFailedAsync(
        [ServiceBusTrigger(PaymentsEvents, "ordering-on-payment-failed", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<PaymentFailed>(message, cancellationToken);

    [Function("OnOrderConfirmed")]
    public Task OnOrderConfirmedAsync(
        [ServiceBusTrigger(OrderingEvents, "ordering-on-order-confirmed", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<OrderConfirmed>(message, cancellationToken);

    [Function("OnShipmentDispatched")]
    public Task OnShipmentDispatchedAsync(
        [ServiceBusTrigger(ShippingEvents, "ordering-on-shipment-dispatched", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<ShipmentDispatched>(message, cancellationToken);

    [Function("OnShipmentDelivered")]
    public Task OnShipmentDeliveredAsync(
        [ServiceBusTrigger(ShippingEvents, "ordering-on-shipment-delivered", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<ShipmentDelivered>(message, cancellationToken);

    [Function("OnShipmentFailed")]
    public Task OnShipmentFailedAsync(
        [ServiceBusTrigger(ShippingEvents, "ordering-on-shipment-failed", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<ShipmentFailed>(message, cancellationToken);

    [Function("OnOrderRejected")]
    public Task OnOrderRejectedAsync(
        [ServiceBusTrigger(OrderingEvents, "ordering-on-order-rejected", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<OrderRejected>(message, cancellationToken);

    [Function("OnOrderCancelled")]
    public Task OnOrderCancelledAsync(
        [ServiceBusTrigger(OrderingEvents, "ordering-on-order-cancelled", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<OrderCancelled>(message, cancellationToken);

    // A handler that throws, also because it could not send its own message, leaves this message on the
    // subscription: Service Bus delivers it again. That retry is what replaces the outbox (ADR-0012).
    // After the maximum delivery count the message goes to the dead-letter queue, which must alert a
    // person, because the order it belongs to is now stuck.
    private Task ConsumeAsync<TEvent>(ServiceBusReceivedMessage message, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent =>
        consumer.ConsumeAsync(ServiceBusMessages.Read<TEvent>(message), cancellationToken);
}
