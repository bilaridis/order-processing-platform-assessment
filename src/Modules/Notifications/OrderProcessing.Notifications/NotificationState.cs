using System.Collections.Concurrent;
using OrderProcessing.Ordering.Contracts;

namespace OrderProcessing.Notifications;

/// <summary>What the service knows about one order: where to write, and how far it has told the customer.</summary>
/// <param name="Contact">Null when the customer gave no contact details: nothing is sent.</param>
/// <param name="LastVersion">Version of the last lifecycle event notified. Older events are not sent.</param>
internal sealed record OrderRecipient(Guid OrderId, string CustomerId, CustomerContact? Contact, long LastVersion, bool Closed);

/// <summary>A message the service sent, so that its delivery report can be matched to the order.</summary>
internal sealed record SentNotification(Guid OrderId, string Channel, string Trigger, string Provider);

/// <summary>
/// SKELETON PLACEHOLDER for the service's Table storage: Recipients (PartitionKey = order id), the sent
/// log (PartitionKey = order id, RowKey = trigger and channel) and its index by provider message id.
/// The contact copy is personal data and is purged with the order data (timer function PurgeContacts).
/// </summary>
internal sealed class NotificationStore
{
    private readonly ConcurrentDictionary<Guid, OrderRecipient> _recipients = new();
    private readonly ConcurrentDictionary<string, SentNotification> _sent = new();
    private readonly ConcurrentDictionary<string, SentNotification> _byProviderMessageId = new();

    public OrderRecipient? Find(Guid orderId) => _recipients.GetValueOrDefault(orderId);

    public void Save(OrderRecipient recipient) => _recipients[recipient.OrderId] = recipient;

    public bool WasSent(string idempotencyKey) => _sent.ContainsKey(idempotencyKey);

    public void RecordSent(string idempotencyKey, string? providerMessageId, SentNotification sent)
    {
        _sent[idempotencyKey] = sent;

        if (providerMessageId is not null)
        {
            _byProviderMessageId[providerMessageId] = sent;
        }
    }

    public SentNotification? FindByProviderMessageId(string providerMessageId) =>
        _byProviderMessageId.GetValueOrDefault(providerMessageId);
}

/// <summary>
/// SKELETON PLACEHOLDER for the preferences table (PartitionKey = customer id), written by the Order API
/// from the account page. A customer may turn off SMS and push for order updates. Email cannot be turned
/// off: it carries the receipt.
/// </summary>
internal sealed class CustomerPreferences
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _disabled = new();

    public bool Allows(string customerId, string channel) =>
        channel == Contracts.NotificationChannels.Email
        || !_disabled.TryGetValue(customerId, out var channels)
        || !channels.Contains(channel);

    public void Disable(string customerId, string channel) =>
        _disabled.AddOrUpdate(customerId, _ => [channel], (_, channels) => [.. channels, channel]);
}
