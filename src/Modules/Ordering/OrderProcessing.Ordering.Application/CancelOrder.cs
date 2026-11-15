using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.Ordering.Domain;

namespace OrderProcessing.Ordering.Application;

internal sealed record CancelOrder(OrderId OrderId, string CallerId, bool CanAccessAnyOrder, string? Reason)
    : ICommand<Result>;

/// <summary>
/// Cancels an order if its state permits. Only the state change happens here; releasing stock, reversing
/// the payment and stopping the shipment are compensations run by <see cref="OrderFulfillmentProcess"/>
/// when it receives OrderCancelled, so a slow or failing provider cannot block the cancellation itself.
/// </summary>
internal sealed class CancelOrderHandler(IOrderRepository orders, OrderStore store, TimeProvider clock)
    : ICommandHandler<CancelOrder, Result>
{
    public async Task<Result> HandleAsync(CancelOrder command, CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(command.OrderId, cancellationToken);

        if (order is null || (!command.CanAccessAnyOrder && order.CustomerId != command.CallerId))
        {
            return OrderErrors.NotFound;
        }

        var result = order.Cancel(command.Reason ?? "Cancelled by customer", clock.GetUtcNow());

        if (result.IsSuccess)
        {
            await store.SaveAsync(order, cancellationToken);
        }

        return result;
    }
}
