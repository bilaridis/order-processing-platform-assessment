using OrderProcessing.Notifications.Contracts;

namespace OrderProcessing.Notifications;

/// <summary>
/// Which event is worth a message, and on which channels (06 Integration services). In Azure it is a
/// table the business can change without a deployment; here it is the launch proposal in code.
/// SMS is used only where time matters, because it is the channel that costs money per message.
/// </summary>
internal sealed class NotificationPolicy
{
    private static readonly string[] Receipt = [NotificationChannels.Email, NotificationChannels.Push];
    private static readonly string[] Urgent = [NotificationChannels.Sms, NotificationChannels.Push];

    private readonly Dictionary<string, string[]> _channels = new(StringComparer.Ordinal)
    {
        [Triggers.OrderPlaced] = Receipt,
        [Triggers.OrderRejected] = Receipt,
        [Triggers.OrderShipped] = Receipt,
        [Triggers.OrderDelivered] = Receipt,
        [Triggers.OrderCancelled] = Receipt,
        [Triggers.OutForDelivery] = Urgent,
        [Triggers.ReadyForCollection] = Urgent,
    };

    /// <summary>Empty when the event is not worth a message (OrderSubmitted, in transit).</summary>
    public IReadOnlyList<string> ChannelsFor(string trigger) => _channels.GetValueOrDefault(trigger) ?? [];
}

internal static class Triggers
{
    public const string OrderPlaced = "order.placed";
    public const string OrderRejected = "order.rejected";
    public const string OrderShipped = "order.shipped";
    public const string OrderDelivered = "order.delivered";
    public const string OrderCancelled = "order.cancelled";
    public const string OutForDelivery = "shipment.out_for_delivery";
    public const string ReadyForCollection = "shipment.ready_for_collection";

    public static string ForCarrierStatus(string status) => $"shipment.{status}";
}

/// <summary>
/// SKELETON PLACEHOLDER for the templates in Blob storage (per trigger, channel and language,
/// versioned). Greek and English for the launch; anything else falls back to English.
/// </summary>
internal static class NotificationTemplates
{
    private static readonly Dictionary<(string Trigger, string Language), string> Texts = new()
    {
        [(Triggers.OrderPlaced, "en")] = "We have accepted your order {0}. Total {1}.",
        [(Triggers.OrderPlaced, "el")] = "Η παραγγελία σας {0} έγινε δεκτή. Σύνολο {1}.",
        [(Triggers.OrderRejected, "en")] = "We could not complete your order {0}: {1}.",
        [(Triggers.OrderRejected, "el")] = "Δεν ήταν δυνατό να ολοκληρωθεί η παραγγελία σας {0}: {1}.",
        [(Triggers.OrderShipped, "en")] = "Your order {0} is on its way with {1}.",
        [(Triggers.OrderShipped, "el")] = "Η παραγγελία σας {0} στάλθηκε με {1}.",
        [(Triggers.OrderDelivered, "en")] = "Your order {0} has been delivered.",
        [(Triggers.OrderDelivered, "el")] = "Η παραγγελία σας {0} παραδόθηκε.",
        [(Triggers.OrderCancelled, "en")] = "Your order {0} has been cancelled. Any payment will be returned.",
        [(Triggers.OrderCancelled, "el")] = "Η παραγγελία σας {0} ακυρώθηκε. Τυχόν πληρωμή θα επιστραφεί.",
        [(Triggers.OutForDelivery, "en")] = "Your order {0} will be delivered today.",
        [(Triggers.OutForDelivery, "el")] = "Η παραγγελία σας {0} θα παραδοθεί σήμερα.",
        [(Triggers.ReadyForCollection, "en")] = "Your order {0} is ready for collection at {1}.",
        [(Triggers.ReadyForCollection, "el")] = "Η παραγγελία σας {0} σας περιμένει στο {1}.",
    };

    public static (string Subject, string Text) Render(string trigger, string language, Guid orderId, string? detail)
    {
        var template = Texts.GetValueOrDefault((trigger, language)) ?? Texts.GetValueOrDefault((trigger, "en")) ?? "{0}";
        var shortId = orderId.ToString("N")[..8].ToUpperInvariant();
        var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, shortId, detail ?? string.Empty);
        return (text.Split('.')[0], text);
    }
}
