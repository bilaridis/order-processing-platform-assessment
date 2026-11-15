using System.Collections.Concurrent;
using OrderProcessing.Ordering.Application;
using OrderProcessing.Ordering.Domain;

namespace OrderProcessing.Ordering.Infrastructure;

/// <summary>
/// SKELETON PLACEHOLDER. Keeps the solution runnable without a database.
/// The delivery team replaces it with an EF Core repository on Azure SQL schema "ordering" (ADR-0004):
/// optimistic concurrency on <c>Order.Version</c>, the order id as primary key.
/// </summary>
internal sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<OrderId, Order> _orders = new();

    public Task<Order?> FindAsync(OrderId id, CancellationToken cancellationToken) =>
        Task.FromResult(_orders.GetValueOrDefault(id));

    public Task SaveAsync(Order order, CancellationToken cancellationToken)
    {
        _orders[order.Id] = order;
        return Task.CompletedTask;
    }
}
