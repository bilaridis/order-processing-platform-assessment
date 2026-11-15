using OrderProcessing.BuildingBlocks.Azure;
using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Ecommerce.Contracts;
using OrderProcessing.Inventory.Contracts;
using OrderProcessing.Pricing.Contracts;

namespace OrderProcessing.Ordering.Functions;

// The other services' contracts, implemented as commands on their Service Bus queues. Ordering's
// handlers do not change: they depend on IPricingApi, IInventoryApi, IEcommerceApi and ICarriersApi and
// cannot tell whether the other service is in the same process (local host) or behind a queue.
// Every answer comes back as an event. Nothing is called over HTTP.
//
// The message id is stable per order and command, so Service Bus duplicate detection removes a command
// that a redelivered handler sends a second time inside the detection window.

internal sealed class QueuedPricingClient(ServiceBusCommandSender commands) : IPricingApi
{
    public Task RequestQuoteAsync(PriceQuoteRequest request, CancellationToken cancellationToken) =>
        commands.SendAsync(
            PricingMessaging.CommandQueue, PricingMessaging.QuotePrice, request, $"quote-{request.OrderId:N}", cancellationToken);
}

internal sealed class QueuedInventoryClient(ServiceBusCommandSender commands) : IInventoryApi
{
    public Task RequestReservationAsync(ReserveStockRequest request, CancellationToken cancellationToken) =>
        commands.SendAsync(
            InventoryMessaging.CommandQueue, InventoryMessaging.ReserveStock, request, $"reserve-{request.OrderId:N}", cancellationToken);

    public Task RequestReleaseAsync(Guid orderId, CancellationToken cancellationToken) =>
        commands.SendAsync(
            InventoryMessaging.CommandQueue, InventoryMessaging.ReleaseStock, new ReleaseStockRequest(orderId), $"release-{orderId:N}", cancellationToken);
}

internal sealed class QueuedEcommerceClient(ServiceBusCommandSender commands) : IEcommerceApi
{
    public Task RequestAuthorizationAsync(AuthorizePaymentRequest request, CancellationToken cancellationToken) =>
        commands.SendAsync(
            EcommerceMessaging.CommandQueue, EcommerceMessaging.AuthorizePayment, request, $"authorize-{request.OrderId:N}", cancellationToken);

    public Task RequestReversalAsync(Guid orderId, CancellationToken cancellationToken) =>
        commands.SendAsync(
            EcommerceMessaging.CommandQueue, EcommerceMessaging.ReversePayment, new ReversePaymentRequest(orderId), $"reverse-{orderId:N}", cancellationToken);
}

internal sealed class QueuedCarriersClient(ServiceBusCommandSender commands) : ICarriersApi
{
    public Task RequestShipmentAsync(ShipmentRequest request, CancellationToken cancellationToken) =>
        commands.SendAsync(
            CarriersMessaging.CommandQueue, CarriersMessaging.RequestShipment, request, $"ship-{request.OrderId:N}", cancellationToken);

    public Task RequestCancellationAsync(Guid orderId, CancellationToken cancellationToken) =>
        commands.SendAsync(
            CarriersMessaging.CommandQueue, CarriersMessaging.CancelShipment, new CancelShipmentRequest(orderId), $"cancel-{orderId:N}", cancellationToken);
}
