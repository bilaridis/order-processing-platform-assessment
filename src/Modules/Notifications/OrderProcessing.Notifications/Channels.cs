namespace OrderProcessing.Notifications;

/// <summary>One message to one customer on one channel, already worded.</summary>
/// <param name="IdempotencyKey">
/// {orderId}:{trigger}:{channel}. Passed to the provider as the operation id, so a send repeated after a
/// crash is recognised by the provider as well as by our own log.
/// </param>
internal sealed record OutgoingMessage(Guid OrderId, string CustomerId, string Trigger, string IdempotencyKey, string Subject, string Text);

/// <param name="ProviderMessageId">
/// The provider's id for the message. Its delivery report names it, which is how a report is matched to
/// the order. Null when the provider refused the message.
/// </param>
/// <param name="DeliveredOnAcceptance">
/// True where the provider sends no delivery report (push): acceptance is the last thing we learn.
/// </param>
internal sealed record SendResult(string? ProviderMessageId, string? RefusedReason = null, bool DeliveredOnAcceptance = false)
{
    public bool Accepted => RefusedReason is null;

    public static SendResult Refused(string reason) => new(null, reason);
}

/// <summary>
/// One port per channel (ADR-0006): the channels need different things (a subject and a body, 160
/// characters, a device) and cost different amounts. Adapters own the provider's SDK, errors and retries.
/// </summary>
internal interface IEmailSender
{
    string Provider { get; }

    Task<SendResult> SendAsync(string to, string language, OutgoingMessage message, CancellationToken cancellationToken);
}

internal interface ISmsSender
{
    string Provider { get; }

    Task<SendResult> SendAsync(string to, OutgoingMessage message, CancellationToken cancellationToken);
}

/// <summary>Push goes to every device the customer registered with Notification Hubs, by tag = customer id.</summary>
internal interface IPushSender
{
    string Provider { get; }

    Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken);
}
