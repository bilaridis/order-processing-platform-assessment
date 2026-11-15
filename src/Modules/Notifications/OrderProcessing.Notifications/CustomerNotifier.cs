using Microsoft.Extensions.Logging;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Notifications.Contracts;
using OrderProcessing.Ordering.Contracts;

namespace OrderProcessing.Notifications;

/// <summary>
/// The notification-integration-service (ADR-0006, 06 Integration services). A pure consumer: it
/// listens to order and shipping events and tells the customer. Nobody asks it for anything and nothing
/// waits for it, so it can be slow or down without affecting an order. It never calls Ordering.
///
/// Rules:
/// 1. Contact details come only from OrderSubmitted, into its own copy. Later events carry the order id.
/// 2. One message per order, trigger and channel: the sent log is checked first, and the provider gets
///    the same key as operation id, so a redelivered event sends nothing twice (ADR-0012).
/// 3. A lifecycle event older than the last one notified is not sent ("delivered" is not followed by a
///    late "shipped"), and nothing is sent after the order is closed.
/// 4. Which event goes to which channel is the policy's decision, and the customer may turn off SMS and push.
/// </summary>
internal sealed partial class CustomerNotifier(
    NotificationStore store,
    NotificationPolicy policy,
    CustomerPreferences preferences,
    IEmailSender email,
    ISmsSender sms,
    IPushSender push,
    IEventPublisher events,
    NotificationMetrics metrics,
    ILogger<CustomerNotifier> logger) :
    IIntegrationEventHandler<OrderSubmitted>,
    IIntegrationEventHandler<OrderPlaced>,
    IIntegrationEventHandler<OrderRejected>,
    IIntegrationEventHandler<OrderShipped>,
    IIntegrationEventHandler<OrderDelivered>,
    IIntegrationEventHandler<OrderCancelled>,
    IIntegrationEventHandler<ShipmentTrackingUpdated>
{
    public Task HandleAsync(OrderSubmitted integrationEvent, CancellationToken cancellationToken)
    {
        if (store.Find(integrationEvent.OrderId) is null)
        {
            store.Save(new OrderRecipient(integrationEvent.OrderId, integrationEvent.CustomerId, integrationEvent.Contact, integrationEvent.Version, false));
        }

        return Task.CompletedTask;
    }

    public Task HandleAsync(OrderPlaced integrationEvent, CancellationToken cancellationToken) =>
        NotifyAsync(integrationEvent.OrderId, integrationEvent.Version, Triggers.OrderPlaced,
            $"{integrationEvent.Total:0.00} {integrationEvent.Currency}", closes: false, cancellationToken);

    public Task HandleAsync(OrderRejected integrationEvent, CancellationToken cancellationToken) =>
        NotifyAsync(integrationEvent.OrderId, integrationEvent.Version, Triggers.OrderRejected,
            integrationEvent.Reason, closes: true, cancellationToken);

    public Task HandleAsync(OrderShipped integrationEvent, CancellationToken cancellationToken) =>
        NotifyAsync(integrationEvent.OrderId, integrationEvent.Version, Triggers.OrderShipped,
            integrationEvent.Carrier, closes: false, cancellationToken);

    public Task HandleAsync(OrderDelivered integrationEvent, CancellationToken cancellationToken) =>
        NotifyAsync(integrationEvent.OrderId, integrationEvent.Version, Triggers.OrderDelivered, null, closes: true, cancellationToken);

    public Task HandleAsync(OrderCancelled integrationEvent, CancellationToken cancellationToken) =>
        NotifyAsync(integrationEvent.OrderId, integrationEvent.Version, Triggers.OrderCancelled, null, closes: true, cancellationToken);

    // Carrier detail has no version: it is sent unless the order is already closed.
    public Task HandleAsync(ShipmentTrackingUpdated integrationEvent, CancellationToken cancellationToken) =>
        NotifyAsync(integrationEvent.OrderId, version: null, Triggers.ForCarrierStatus(integrationEvent.Status),
            integrationEvent.Location, closes: false, cancellationToken);

    private async Task NotifyAsync(
        Guid orderId, long? version, string trigger, string? detail, bool closes, CancellationToken cancellationToken)
    {
        var channels = policy.ChannelsFor(trigger);

        if (channels.Count == 0)
        {
            return;
        }

        // The event overtook OrderSubmitted (different topics). Failing makes the bus deliver it again later.
        var recipient = store.Find(orderId) ?? throw new RecipientNotKnownException(orderId, trigger);

        if (recipient.Closed || (version is { } newer && newer <= recipient.LastVersion))
        {
            foreach (var channel in channels)
            {
                metrics.Skipped(channel, "stale");
            }

            return;
        }

        foreach (var channel in channels)
        {
            await SendAsync(recipient, channel, trigger, detail, cancellationToken);
        }

        store.Save(recipient with { LastVersion = version ?? recipient.LastVersion, Closed = recipient.Closed || closes });
    }

    private async Task SendAsync(OrderRecipient recipient, string channel, string trigger, string? detail, CancellationToken cancellationToken)
    {
        var key = $"{recipient.OrderId:N}:{trigger}:{channel}";

        if (store.WasSent(key))
        {
            return;
        }

        if (!preferences.Allows(recipient.CustomerId, channel))
        {
            metrics.Skipped(channel, "turned_off");
            return;
        }

        var contact = recipient.Contact;
        var language = contact?.Language ?? "el";
        var (subject, text) = NotificationTemplates.Render(trigger, language, recipient.OrderId, detail);
        var message = new OutgoingMessage(recipient.OrderId, recipient.CustomerId, trigger, key, subject, text);

        var (provider, result) = channel switch
        {
            NotificationChannels.Email when contact?.Email is { } address =>
                (email.Provider, await email.SendAsync(address, language, message, cancellationToken)),
            NotificationChannels.Sms when contact?.Mobile is { } number =>
                (sms.Provider, await sms.SendAsync(number, message, cancellationToken)),
            NotificationChannels.Push =>
                (push.Provider, await push.SendAsync(message, cancellationToken)),
            _ => (string.Empty, (SendResult?)null),
        };

        if (result is null)
        {
            metrics.Skipped(channel, "no_contact");
            return;
        }

        store.RecordSent(key, result.ProviderMessageId, new SentNotification(recipient.OrderId, channel, trigger, provider));

        if (!result.Accepted)
        {
            // A refusal is final: an invalid number does not become valid on retry.
            metrics.Failed(channel, result.RefusedReason!);
            LogRefused(logger, recipient.OrderId, channel, trigger, result.RefusedReason!);

            if (channel != NotificationChannels.Push)
            {
                await events.PublishAsync(
                    new NotificationFailed(recipient.OrderId, channel, trigger, result.RefusedReason!).WithIdFor(recipient.OrderId, $"{channel}:{trigger}"),
                    cancellationToken);
            }

            return;
        }

        metrics.Sent(channel, trigger);
        LogSent(logger, recipient.OrderId, channel, trigger, provider);

        if (result.DeliveredOnAcceptance)
        {
            metrics.Delivered(channel);
            await events.PublishAsync(
                new CustomerNotified(recipient.OrderId, channel, trigger).WithIdFor(recipient.OrderId, $"{channel}:{trigger}"),
                cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId}: {Channel} for {Trigger} accepted by {Provider}")]
    private static partial void LogSent(ILogger logger, Guid orderId, string channel, string trigger, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order {OrderId}: {Channel} for {Trigger} refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, Guid orderId, string channel, string trigger, string reason);
}

internal sealed class RecipientNotKnownException(Guid orderId, string trigger)
    : InvalidOperationException($"Order {orderId}: {trigger} arrived before OrderSubmitted. The bus will deliver it again.");
