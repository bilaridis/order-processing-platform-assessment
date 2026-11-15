using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Ecommerce.Contracts;
using OrderProcessing.Notifications.Contracts;
using OrderProcessing.Ordering.Contracts;

namespace OrderProcessing.OrderApi;

/// <summary>
/// Builds the order views from the events of Ordering, Ecommerce and Carriers (ADR-0013).
/// In Azure these handlers are the Service Bus-triggered functions of the projector function app.
///
/// Three rules:
/// 1. It only writes views. It never calls Ordering and never sends a command that changes an order.
/// 2. The status comes only from Ordering's events, and only from one that is newer than the view.
/// 3. Facts of other services (payment state, carrier detail) are added next to the status, never into it.
///
/// Every handler is safe to run twice: an event already in the timeline is skipped. Event ids are
/// derived from the fact they report (ADR-0012), so the same fact published twice is one entry.
/// </summary>
internal sealed class OrderViewProjector(IOrderViewStore views) :
    IIntegrationEventHandler<OrderSubmitted>,
    IIntegrationEventHandler<OrderPlaced>,
    IIntegrationEventHandler<OrderCancellationRefused>,
    IIntegrationEventHandler<OrderConfirmed>,
    IIntegrationEventHandler<OrderRejected>,
    IIntegrationEventHandler<OrderCancelled>,
    IIntegrationEventHandler<OrderShipped>,
    IIntegrationEventHandler<OrderDelivered>,
    IIntegrationEventHandler<PaymentAuthorized>,
    IIntegrationEventHandler<PaymentFailed>,
    IIntegrationEventHandler<ShipmentTrackingUpdated>,
    IIntegrationEventHandler<ShipmentFailed>,
    IIntegrationEventHandler<CustomerNotified>,
    IIntegrationEventHandler<NotificationFailed>
{
    private const string Ordering = "ordering";
    private const string Payments = "payments";
    private const string Carrier = "carrier";
    private const string Notifications = "notifications";

    // Ordering has the order. The Order API may already have written the view when it queued the
    // command (version 0); either way the result is the same record.
    public async Task HandleAsync(OrderSubmitted integrationEvent, CancellationToken cancellationToken)
    {
        var existing = await views.FindAsync(integrationEvent.OrderId, cancellationToken);

        if (existing is { Version: > 0 })
        {
            return;
        }

        await views.UpsertAsync(
            OrderViews.New(
                integrationEvent.OrderId,
                integrationEvent.CustomerId,
                integrationEvent.Items,
                integrationEvent.Destination,
                integrationEvent.OccurredAt) with
            {
                Version = integrationEvent.Version,
                Tracking = [Entry(integrationEvent, Ordering, "order.submitted")],
            },
            cancellationToken);
    }

    // Accepted: priced and stock reserved. The event carries the whole summary, so the view gets the
    // price without a call to Ordering.
    public Task HandleAsync(OrderPlaced integrationEvent, CancellationToken cancellationToken) =>
        ApplyStatusAsync(integrationEvent, integrationEvent.OrderId, integrationEvent.Version, "order.accepted", null,
            view => view with { Status = "Pending", Payment = "Pending" },
            cancellationToken,
            always: view => view with { Summary = integrationEvent.Summary });

    public Task HandleAsync(OrderCancellationRefused integrationEvent, CancellationToken cancellationToken) =>
        ApplyFactAsync(integrationEvent, integrationEvent.OrderId, Ordering, "order.cancellation_refused", integrationEvent.Reason,
            view => view, cancellationToken);

    public Task HandleAsync(OrderConfirmed integrationEvent, CancellationToken cancellationToken) =>
        ApplyStatusAsync(integrationEvent, integrationEvent.OrderId, integrationEvent.Version, "order.confirmed", null,
            view => view with { Status = "Confirmed" }, cancellationToken);

    public Task HandleAsync(OrderRejected integrationEvent, CancellationToken cancellationToken) =>
        ApplyStatusAsync(integrationEvent, integrationEvent.OrderId, integrationEvent.Version, "order.rejected", integrationEvent.Reason,
            view => view with { Status = "Rejected" }, cancellationToken);

    public Task HandleAsync(OrderCancelled integrationEvent, CancellationToken cancellationToken) =>
        ApplyStatusAsync(integrationEvent, integrationEvent.OrderId, integrationEvent.Version, "order.cancelled", integrationEvent.Reason,
            view => view with { Status = "Cancelled" }, cancellationToken);

    public Task HandleAsync(OrderShipped integrationEvent, CancellationToken cancellationToken) =>
        ApplyStatusAsync(integrationEvent, integrationEvent.OrderId, integrationEvent.Version, "order.shipped", integrationEvent.Carrier,
            view => view with { Status = "Shipped", Carrier = integrationEvent.Carrier, TrackingNumber = integrationEvent.TrackingNumber },
            cancellationToken);

    public Task HandleAsync(OrderDelivered integrationEvent, CancellationToken cancellationToken) =>
        ApplyStatusAsync(integrationEvent, integrationEvent.OrderId, integrationEvent.Version, "order.delivered", null,
            view => view with { Status = "Delivered" }, cancellationToken);

    public Task HandleAsync(PaymentAuthorized integrationEvent, CancellationToken cancellationToken) =>
        ApplyFactAsync(integrationEvent, integrationEvent.OrderId, Payments, "payment.authorized", null,
            view => view with { Payment = "Authorized" }, cancellationToken);

    public Task HandleAsync(PaymentFailed integrationEvent, CancellationToken cancellationToken) =>
        ApplyFactAsync(integrationEvent, integrationEvent.OrderId, Payments, "payment.failed", integrationEvent.Reason,
            view => view with { Payment = "Failed" }, cancellationToken);

    // "In transit" or "ready for collection" is what the customer wants to see and what Ordering does
    // not want to store. It goes into the view directly and leaves the order's status alone.
    public Task HandleAsync(ShipmentTrackingUpdated integrationEvent, CancellationToken cancellationToken) =>
        ApplyFactAsync(integrationEvent, integrationEvent.OrderId, Carrier, $"carrier.{integrationEvent.Status}", integrationEvent.Location,
            view => view with { CarrierStatus = integrationEvent.Status, Carrier = integrationEvent.Carrier ?? view.Carrier },
            cancellationToken);

    public Task HandleAsync(ShipmentFailed integrationEvent, CancellationToken cancellationToken) =>
        ApplyFactAsync(integrationEvent, integrationEvent.OrderId, Carrier, "carrier.failed", integrationEvent.Reason,
            view => view with { CarrierStatus = "failed", Carrier = integrationEvent.Carrier ?? view.Carrier },
            cancellationToken);

    // For support: "did the customer get the SMS?" is answered from the order's timeline.
    public Task HandleAsync(CustomerNotified integrationEvent, CancellationToken cancellationToken) =>
        ApplyFactAsync(integrationEvent, integrationEvent.OrderId, Notifications, $"notification.{integrationEvent.Channel}.delivered",
            integrationEvent.Trigger, view => view, cancellationToken);

    public Task HandleAsync(NotificationFailed integrationEvent, CancellationToken cancellationToken) =>
        ApplyFactAsync(integrationEvent, integrationEvent.OrderId, Notifications, $"notification.{integrationEvent.Channel}.failed",
            $"{integrationEvent.Trigger}: {integrationEvent.Reason}", view => view, cancellationToken);

    /// <summary>A lifecycle event of Ordering: it may move the status, but only forward.</summary>
    private Task ApplyStatusAsync(
        IIntegrationEvent integrationEvent,
        Guid orderId,
        long version,
        string code,
        string? detail,
        Func<OrderView, OrderView> change,
        CancellationToken cancellationToken,
        Func<OrderView, OrderView>? always = null) =>
        ApplyAsync(
            integrationEvent,
            orderId,
            Entry(integrationEvent, Ordering, code, detail),
            view =>
            {
                // "always" is data the event carries that is true whatever arrived first (the price).
                view = always?.Invoke(view) ?? view;

                // An event older than the view arrived late. It is kept in the timeline, but the status stays.
                return version > view.Version ? change(view) with { Version = version } : view;
            },
            cancellationToken);

    /// <summary>A fact from another service: shown next to the status, never changes it.</summary>
    private Task ApplyFactAsync(
        IIntegrationEvent integrationEvent,
        Guid orderId,
        string source,
        string code,
        string? detail,
        Func<OrderView, OrderView> change,
        CancellationToken cancellationToken) =>
        ApplyAsync(integrationEvent, orderId, Entry(integrationEvent, source, code, detail), change, cancellationToken);

    private async Task ApplyAsync(
        IIntegrationEvent integrationEvent,
        Guid orderId,
        TrackingEntry entry,
        Func<OrderView, OrderView> change,
        CancellationToken cancellationToken)
    {
        // An event can overtake OrderSubmitted, because the services publish to different topics. Failing
        // makes the bus deliver it again later, by which time the view exists.
        var view = await views.FindAsync(orderId, cancellationToken)
            ?? throw new OrderViewNotReadyException(orderId, integrationEvent.GetType().Name);

        if (view.Tracking.Any(existing => existing.Id == entry.Id))
        {
            return;
        }

        var updated = change(view) with
        {
            Tracking = [.. view.Tracking.Append(entry).OrderBy(existing => existing.OccurredAt)],
            UpdatedAt = integrationEvent.OccurredAt > view.UpdatedAt ? integrationEvent.OccurredAt : view.UpdatedAt,
        };

        // The persistent store writes with the row's ETag, so two projector instances cannot overwrite each other.
        await views.UpsertAsync(updated, cancellationToken);
    }

    private static TrackingEntry Entry(IIntegrationEvent integrationEvent, string source, string code, string? detail = null) =>
        new(integrationEvent.EventId, integrationEvent.OccurredAt, source, code, detail);
}

#pragma warning disable CA1032, CA1064 // Raised and caught inside this module only.
internal sealed class OrderViewNotReadyException(Guid orderId, string eventType)
    : Exception($"The view of order {orderId} does not exist yet; {eventType} must be delivered again.");
#pragma warning restore CA1032, CA1064
