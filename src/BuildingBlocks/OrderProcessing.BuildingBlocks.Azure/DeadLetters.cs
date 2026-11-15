using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;

namespace OrderProcessing.BuildingBlocks.Azure;

/// <summary>
/// One dead-lettered message, as L1 support needs to see it: which order, which step, why. It is written
/// as one structured log line to Log Analytics, where Grafana or a Workbook shows it as a table next to
/// the dead-letter counts that Service Bus publishes as Azure Monitor metrics (ADR-0008, ADR-0012).
/// No message body: it may hold personal data.
/// </summary>
/// <param name="EntityPath">The queue, or topic/Subscriptions/subscription, whose dead-letter queue holds it.</param>
/// <param name="OrderId">The session id, which is the order id for every message of the platform.</param>
public sealed record DeadLetterRecord(
    string EntityPath,
    string MessageId,
    long SequenceNumber,
    string? Subject,
    string? OrderId,
    string? Reason,
    string? Description,
    int DeliveryCount,
    DateTimeOffset EnqueuedAt)
{
    public static DeadLetterRecord From(string entityPath, ServiceBusReceivedMessage message) => new(
        entityPath,
        message.MessageId,
        message.SequenceNumber,
        message.Subject,
        message.SessionId,
        message.DeadLetterReason,
        message.DeadLetterErrorDescription,
        message.DeliveryCount,
        message.EnqueuedTime);

    /// <summary>
    /// Where a resubmitted message goes. A subscription cannot be sent to, so it goes to the topic: the
    /// other subscriptions receive it again and their inbox skips it, because the event id in the body
    /// is unchanged (ADR-0012). Command queues get it directly; command handlers are idempotent per order.
    /// </summary>
    public static string ResubmitTargetFor(string entityPath) =>
        entityPath.Split("/Subscriptions/", StringSplitOptions.None) is [var topic, _] ? topic : entityPath;

    /// <summary>
    /// A new message id, so that duplicate detection does not drop it as a repeat of the original.
    /// The consumers recognise a repeat by the event id in the body, not by the message id.
    /// </summary>
    public static string ResubmittedMessageId(string messageId, DateTimeOffset now) =>
        $"{messageId}:resubmit:{now:yyyyMMddHHmmss}";
}

/// <summary>
/// The dead-letter inspector and resubmit tool that ADR-0012 requires because there is no outbox: a
/// dead-lettered message is a stuck order until a person acts. Compiled, never run against Service Bus.
/// </summary>
public sealed partial class DeadLetterInspector(
    ServiceBusClient client, ServiceBusAdministrationClient administration, ILogger<DeadLetterInspector> logger)
{
    private const int PeekBatch = 50;

    // Highest sequence number already logged per dead-letter queue, so each message is logged once per
    // instance. Lost on restart, which logs the backlog again: harmless, the table shows distinct messages.
    private static readonly ConcurrentDictionary<string, long> LastLogged = new();

    /// <summary>Logs every new dead-lettered message of every queue and subscription. Returns how many.</summary>
    public async Task<int> InspectAsync(CancellationToken cancellationToken)
    {
        var found = 0;

        await foreach (var queue in administration.GetQueuesRuntimePropertiesAsync(cancellationToken))
        {
            if (queue.DeadLetterMessageCount > 0)
            {
                found += await InspectAsync(queue.Name, client.CreateReceiver(queue.Name, DeadLetterOptions), cancellationToken);
            }
        }

        await foreach (var topic in administration.GetTopicsAsync(cancellationToken))
        {
            await foreach (var subscription in administration.GetSubscriptionsRuntimePropertiesAsync(topic.Name, cancellationToken))
            {
                if (subscription.DeadLetterMessageCount > 0)
                {
                    var path = $"{topic.Name}/Subscriptions/{subscription.SubscriptionName}";
                    found += await InspectAsync(
                        path, client.CreateReceiver(topic.Name, subscription.SubscriptionName, DeadLetterOptions), cancellationToken);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Sends one dead-lettered message back to where it came from and removes it from the dead-letter
    /// queue. Run by a person after the cause is fixed. Safe because every handler is idempotent.
    /// </summary>
    /// <returns>False if the message is no longer in the dead-letter queue.</returns>
    public async Task<bool> ResubmitAsync(string entityPath, string messageId, CancellationToken cancellationToken)
    {
        var target = DeadLetterRecord.ResubmitTargetFor(entityPath);
        await using var receiver = entityPath.Split("/Subscriptions/", StringSplitOptions.None) is [var topic, var subscription]
            ? client.CreateReceiver(topic, subscription, DeadLetterOptions)
            : client.CreateReceiver(entityPath, DeadLetterOptions);
        await using var sender = client.CreateSender(target);

        foreach (var message in await receiver.ReceiveMessagesAsync(PeekBatch, TimeSpan.FromSeconds(5), cancellationToken))
        {
            if (message.MessageId != messageId)
            {
                await receiver.AbandonMessageAsync(message, cancellationToken: cancellationToken);
                continue;
            }

            var copy = new ServiceBusMessage(message)
            {
                MessageId = DeadLetterRecord.ResubmittedMessageId(message.MessageId, DateTimeOffset.UtcNow),
            };
            copy.ApplicationProperties.Remove("DeadLetterReason");
            copy.ApplicationProperties.Remove("DeadLetterErrorDescription");

            await sender.SendMessageAsync(copy, cancellationToken);
            await receiver.CompleteMessageAsync(message, cancellationToken);
            LogResubmitted(logger, entityPath, messageId, message.SessionId, target);
            return true;
        }

        return false;
    }

    private static ServiceBusReceiverOptions DeadLetterOptions => new() { SubQueue = SubQueue.DeadLetter };

    private async Task<int> InspectAsync(string entityPath, ServiceBusReceiver receiver, CancellationToken cancellationToken)
    {
        await using (receiver)
        {
            var from = LastLogged.GetValueOrDefault(entityPath) + 1;
            var messages = await receiver.PeekMessagesAsync(PeekBatch, from, cancellationToken);

            foreach (var message in messages)
            {
                var record = DeadLetterRecord.From(entityPath, message);
                LogDeadLetter(
                    logger, record.EntityPath, record.OrderId, record.Subject, record.Reason, record.Description,
                    record.DeliveryCount, record.MessageId, record.EnqueuedAt);
                LastLogged[entityPath] = Math.Max(LastLogged.GetValueOrDefault(entityPath), message.SequenceNumber);
            }

            return messages.Count;
        }
    }

    // One line per stuck message, with named fields: Log Analytics stores them as columns that Grafana
    // can filter (by order id, entity or reason) without parsing text.
    [LoggerMessage(
        EventId = 9001,
        Level = LogLevel.Error,
        Message = "Dead letter in {EntityPath}: order {OrderId}, {Subject}, reason {DeadLetterReason} ({DeadLetterDescription}), after {DeliveryCount} deliveries, message {MessageId}, enqueued {EnqueuedAt}")]
    private static partial void LogDeadLetter(
        ILogger logger, string entityPath, string? orderId, string? subject, string? deadLetterReason,
        string? deadLetterDescription, int deliveryCount, string messageId, DateTimeOffset enqueuedAt);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Warning, Message = "Dead letter {MessageId} of order {OrderId} resubmitted from {EntityPath} to {Target}")]
    private static partial void LogResubmitted(ILogger logger, string entityPath, string messageId, string? orderId, string target);
}
