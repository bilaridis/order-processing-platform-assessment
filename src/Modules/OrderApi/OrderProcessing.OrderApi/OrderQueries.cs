using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;

namespace OrderProcessing.OrderApi;

/// <param name="CanAccessAnyOrder">True for back-office callers; customers only see their own orders.</param>
public sealed record GetOrderView(Guid OrderId, string CallerId, bool CanAccessAnyOrder) : IQuery<Result<OrderView>>;

/// <summary>
/// The query side of the Order API: reads the view and nothing else. No service is called. A new order
/// is readable at once because the command side writes its view when it queues the command.
/// </summary>
internal sealed class GetOrderViewHandler(IOrderViewStore views) : IQueryHandler<GetOrderView, Result<OrderView>>
{
    public static readonly Error NotFound = Error.NotFound("order.not_found", "The order was not found.");

    public async Task<Result<OrderView>> HandleAsync(GetOrderView query, CancellationToken cancellationToken)
    {
        // "Not yours" is reported as "not found" so order ids cannot be probed.
        var view = query.CanAccessAnyOrder
            ? await views.FindAsync(query.OrderId, cancellationToken)
            : await views.FindAsync(query.CallerId, query.OrderId, cancellationToken);

        return view is null ? NotFound : view;
    }
}

public sealed record ListMyOrders(string CustomerId, int Take) : IQuery<Result<IReadOnlyList<OrderListItem>>>;

/// <param name="Total">Null until the order is accepted and has a price.</param>
public sealed record OrderListItem(
    Guid OrderId, string Status, string? CarrierStatus, DateTimeOffset SubmittedAt, decimal? Total, string? Currency, int Items);

/// <summary>"My orders": one read of the customer's partition.</summary>
internal sealed class ListMyOrdersHandler(IOrderViewStore views) : IQueryHandler<ListMyOrders, Result<IReadOnlyList<OrderListItem>>>
{
    public const int MaxPageSize = 50;

    public async Task<Result<IReadOnlyList<OrderListItem>>> HandleAsync(ListMyOrders query, CancellationToken cancellationToken)
    {
        var page = await views.ListAsync(query.CustomerId, Math.Clamp(query.Take, 1, MaxPageSize), cancellationToken);

        return Result.Success<IReadOnlyList<OrderListItem>>(
        [
            .. page.Select(view => new OrderListItem(
                view.OrderId,
                view.Status,
                view.CarrierStatus,
                view.SubmittedAt,
                view.Summary?.Total,
                view.Summary?.Currency,
                view.Items.Sum(item => item.Quantity))),
        ]);
    }
}
