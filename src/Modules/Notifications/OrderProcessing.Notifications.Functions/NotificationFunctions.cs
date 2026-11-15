using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Azure;
using OrderProcessing.BuildingBlocks.Infrastructure;
using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Notifications.Contracts;
using OrderProcessing.Ordering.Contracts;

namespace OrderProcessing.Notifications.Functions;

/// <summary>
/// Function app "notifications". Every function is a Service Bus trigger: one per event on its own
/// subscription, and one for the delivery reports that Event Grid puts on a queue. A failed function
/// leaves its message on the queue; after the maximum delivery count it is dead-lettered and alerts.
/// </summary>
public sealed class NotificationFunctions(IntegrationEventConsumer consumer, IServiceProvider services)
{
    private const string Connection = "ServiceBus";
    private const string OrderingEvents = "ordering-events";
    private const string ShippingEvents = "shipping-events";

    [Function("OnOrderSubmitted")]
    public Task OnOrderSubmittedAsync(
        [ServiceBusTrigger(OrderingEvents, "notifications-on-order-submitted", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<OrderSubmitted>(message, cancellationToken);

    [Function("OnOrderPlaced")]
    public Task OnOrderPlacedAsync(
        [ServiceBusTrigger(OrderingEvents, "notifications-on-order-placed", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<OrderPlaced>(message, cancellationToken);

    [Function("OnOrderRejected")]
    public Task OnOrderRejectedAsync(
        [ServiceBusTrigger(OrderingEvents, "notifications-on-order-rejected", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<OrderRejected>(message, cancellationToken);

    [Function("OnOrderShipped")]
    public Task OnOrderShippedAsync(
        [ServiceBusTrigger(OrderingEvents, "notifications-on-order-shipped", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<OrderShipped>(message, cancellationToken);

    [Function("OnOrderDelivered")]
    public Task OnOrderDeliveredAsync(
        [ServiceBusTrigger(OrderingEvents, "notifications-on-order-delivered", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<OrderDelivered>(message, cancellationToken);

    [Function("OnOrderCancelled")]
    public Task OnOrderCancelledAsync(
        [ServiceBusTrigger(OrderingEvents, "notifications-on-order-cancelled", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<OrderCancelled>(message, cancellationToken);

    [Function("OnShipmentTrackingUpdated")]
    public Task OnShipmentTrackingUpdatedAsync(
        [ServiceBusTrigger(ShippingEvents, "notifications-on-shipment-tracking-updated", Connection = Connection, IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken) =>
        ConsumeAsync<ShipmentTrackingUpdated>(message, cancellationToken);

    /// <summary>
    /// ACS → Event Grid → this queue. The message body is one Event Grid event. Reports for other event
    /// types or unknown messages are completed without effect.
    /// </summary>
    [Function("OnDeliveryReport")]
    public async Task OnDeliveryReportAsync(
        [ServiceBusTrigger(NotificationsMessaging.DeliveryReportQueue, Connection = Connection)]
        ServiceBusReceivedMessage message, CancellationToken cancellationToken)
    {
        if (AcsDeliveryReports.Parse(message.Body.ToMemory().Span) is { } report)
        {
            await services.GetRequiredService<DeliveryReportHandler>().HandleAsync(report, cancellationToken);
        }
    }

    private Task ConsumeAsync<TEvent>(ServiceBusReceivedMessage message, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent =>
        consumer.ConsumeAsync(ServiceBusMessages.Read<TEvent>(message), cancellationToken);
}
