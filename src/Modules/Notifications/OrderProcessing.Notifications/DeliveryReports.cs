using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.Notifications.Contracts;

namespace OrderProcessing.Notifications;

internal enum DeliveryOutcome
{
    Delivered,
    Failed,

    /// <summary>An intermediate report (for example "expanded" for a distribution list). Nothing to do.</summary>
    Pending,
}

internal sealed record DeliveryReport(string ProviderMessageId, DeliveryOutcome Outcome, string? Reason);

/// <summary>
/// Turns a provider's delivery report into a fact for the rest of the platform: <see cref="CustomerNotified"/>
/// or <see cref="NotificationFailed"/>. In Azure it is called by OnDeliveryReport, a Service Bus trigger
/// on <see cref="NotificationsMessaging.DeliveryReportQueue"/>, which Event Grid fills from ACS.
/// Safe to run twice: the event id is derived from order, channel and trigger.
/// </summary>
internal sealed partial class DeliveryReportHandler(
    NotificationStore store, IEventPublisher events, NotificationMetrics metrics, ILogger<DeliveryReportHandler> logger)
{
    public async Task HandleAsync(DeliveryReport report, CancellationToken cancellationToken)
    {
        if (report.Outcome == DeliveryOutcome.Pending)
        {
            return;
        }

        // A report for a message we have no record of: from another system on the same ACS resource,
        // or older than the retention period. Nothing to attach it to.
        if (store.FindByProviderMessageId(report.ProviderMessageId) is not { } sent)
        {
            LogUnknownReport(logger, report.ProviderMessageId);
            return;
        }

        IntegrationEvent outcome;

        if (report.Outcome == DeliveryOutcome.Delivered)
        {
            metrics.Delivered(sent.Channel);
            outcome = new CustomerNotified(sent.OrderId, sent.Channel, sent.Trigger);
        }
        else
        {
            var reason = report.Reason ?? "Undeliverable";
            metrics.Failed(sent.Channel, reason);
            LogUndeliverable(logger, sent.OrderId, sent.Channel, sent.Trigger, reason);
            outcome = new NotificationFailed(sent.OrderId, sent.Channel, sent.Trigger, reason);
        }

        await events.PublishAsync(
            outcome is CustomerNotified notified
                ? notified.WithIdFor(sent.OrderId, $"{sent.Channel}:{sent.Trigger}")
                : ((NotificationFailed)outcome).WithIdFor(sent.OrderId, $"{sent.Channel}:{sent.Trigger}"),
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivery report for unknown message {ProviderMessageId} ignored")]
    private static partial void LogUnknownReport(ILogger logger, string providerMessageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order {OrderId}: {Channel} for {Trigger} undeliverable: {Reason}")]
    private static partial void LogUndeliverable(ILogger logger, Guid orderId, string channel, string trigger, string reason);
}

/// <summary>
/// Reads the Event Grid events of Azure Communication Services, as Event Grid delivers them into the
/// Service Bus queue (one event per message, Event Grid schema). Two event types matter:
/// Microsoft.Communication.EmailDeliveryReportReceived (data.messageId, data.status) and
/// Microsoft.Communication.SMSDeliveryReportReceived (data.messageId, data.deliveryStatus).
/// The status values must be checked against the ACS documentation when the real adapter is built.
/// </summary>
internal static class AcsDeliveryReports
{
    public static DeliveryReport? Parse(ReadOnlySpan<byte> eventGridEvent)
    {
        using var document = JsonDocument.Parse(eventGridEvent.ToArray());
        var root = document.RootElement;
        var type = root.GetProperty("eventType").GetString();
        var data = root.GetProperty("data");

        var status = type switch
        {
            "Microsoft.Communication.EmailDeliveryReportReceived" => data.GetProperty("status").GetString(),
            "Microsoft.Communication.SMSDeliveryReportReceived" => data.GetProperty("deliveryStatus").GetString(),
            _ => null,
        };

        if (status is null)
        {
            return null;
        }

        var outcome = status switch
        {
            "Delivered" => DeliveryOutcome.Delivered,
            "Expanded" => DeliveryOutcome.Pending,
            _ => DeliveryOutcome.Failed,
        };

        return new DeliveryReport(data.GetProperty("messageId").GetString()!, outcome, outcome == DeliveryOutcome.Failed ? status : null);
    }
}
