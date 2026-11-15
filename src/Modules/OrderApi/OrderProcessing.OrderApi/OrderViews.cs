using System.Collections.Concurrent;
using OrderProcessing.Ordering.Contracts;

namespace OrderProcessing.OrderApi;

/// <summary>
/// The read model of the Order API (ADR-0013): one denormalised record per order, shaped for the order
/// and tracking screens and fed by the events of several services. It is a copy. The Order aggregate in
/// Ordering is the only owner of <see cref="Status"/>; if the two disagree, Ordering is right.
/// </summary>
/// <param name="Version">
/// Version of the last lifecycle event applied. Older events do not change the status. 0 means the
/// Order API has queued the order and Ordering has not confirmed receiving it yet.
/// </param>
/// <param name="Items">What was asked for. Known from the moment the order is sent.</param>
/// <param name="Summary">Lines, price and charges. Null until the order is accepted (Pending).</param>
/// <param name="Payment">NotRequested, Pending, Authorized or Failed.</param>
/// <param name="CarrierStatus">The carrier's latest word: in_transit, ready_for_collection. Not an order status.</param>
[HotChocolate.GraphQLName("Order")]
public sealed record OrderView(
    Guid OrderId,
    string CustomerId,
    string Status,
    long Version,
    DateTimeOffset UpdatedAt,
    DateTimeOffset SubmittedAt,
    IReadOnlyList<OrderSubmittedItem> Items,
    OrderSummaryDestination Destination,
    OrderSummary? Summary,
    string Payment,
    string? Carrier,
    string? TrackingNumber,
    string? CarrierStatus,
    IReadOnlyList<TrackingEntry> Tracking);

/// <summary>One line of the tracking timeline. <paramref name="Id"/> is the id of the event it came from.</summary>
public sealed record TrackingEntry(Guid Id, DateTimeOffset OccurredAt, string Source, string Code, string? Detail);

/// <summary>Port to the view tables. Owned by the Order API; no other service reads or writes them.</summary>
internal interface IOrderViewStore
{
    /// <summary>Point read in the customer's partition. A customer can never reach another partition.</summary>
    Task<OrderView?> FindAsync(string customerId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>Read from the second table, keyed by order id. For the back office and the projector.</summary>
    Task<OrderView?> FindAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>One partition read: the customer's orders, newest first.</summary>
    Task<IReadOnlyList<OrderView>> ListAsync(string customerId, int take, CancellationToken cancellationToken);

    Task UpsertAsync(OrderView view, CancellationToken cancellationToken);
}

/// <summary>
/// SKELETON PLACEHOLDER for the two Azure Table storage tables: <c>OrderViewsByCustomer</c>
/// (PartitionKey = customer id, RowKey = order id) and <c>OrderViewsByOrder</c> (PartitionKey = order id).
/// </summary>
internal sealed class InMemoryOrderViewStore : IOrderViewStore
{
    private readonly ConcurrentDictionary<(string CustomerId, Guid OrderId), OrderView> _byCustomer = new();
    private readonly ConcurrentDictionary<Guid, OrderView> _byOrder = new();

    public Task<OrderView?> FindAsync(string customerId, Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(_byCustomer.GetValueOrDefault((customerId, orderId)));

    public Task<OrderView?> FindAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(_byOrder.GetValueOrDefault(orderId));

    public Task<IReadOnlyList<OrderView>> ListAsync(string customerId, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OrderView>>(
        [
            .. _byCustomer
                .Where(row => row.Key.CustomerId == customerId)
                .Select(row => row.Value)
                .OrderByDescending(view => view.SubmittedAt)
                .Take(take),
        ]);

    public Task UpsertAsync(OrderView view, CancellationToken cancellationToken)
    {
        _byCustomer[(view.CustomerId, view.OrderId)] = view;
        _byOrder[view.OrderId] = view;
        return Task.CompletedTask;
    }
}

internal static class OrderViews
{
    /// <summary>The view of an order that has just been sent or received: nothing checked, nothing priced.</summary>
    public static OrderView New(
        Guid orderId,
        string customerId,
        IReadOnlyList<OrderSubmittedItem> items,
        OrderSummaryDestination destination,
        DateTimeOffset now) =>
        new(
            orderId,
            customerId,
            "Submitted",
            Version: 0,
            UpdatedAt: now,
            SubmittedAt: now,
            items,
            destination,
            Summary: null,
            Payment: "NotRequested",
            Carrier: null,
            TrackingNumber: null,
            CarrierStatus: null,
            Tracking: []);
}
