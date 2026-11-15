using System.Diagnostics.Metrics;

namespace OrderProcessing.Notifications;

/// <summary>
/// What an L1 support dashboard needs from this service, as OpenTelemetry metrics (ADR-0008). They go to
/// Azure Monitor and are shown in Azure Managed Grafana: messages sent, delivered, failed and skipped,
/// by channel and by reason. No personal data in tags: never an address or a number.
/// </summary>
internal sealed class NotificationMetrics : IDisposable
{
    private readonly Meter _meter = new(NotificationsModule.MeterName);
    private readonly Counter<long> _sent;
    private readonly Counter<long> _delivered;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _skipped;

    public NotificationMetrics()
    {
        _sent = _meter.CreateCounter<long>("notifications.sent", description: "Messages accepted by the provider");
        _delivered = _meter.CreateCounter<long>("notifications.delivered", description: "Messages the provider reported delivered");
        _failed = _meter.CreateCounter<long>("notifications.failed", description: "Messages refused or undeliverable");
        _skipped = _meter.CreateCounter<long>("notifications.skipped", description: "Messages not sent, by reason");
    }

    public void Sent(string channel, string trigger) => _sent.Add(1, Tag("channel", channel), Tag("trigger", trigger));

    public void Delivered(string channel) => _delivered.Add(1, Tag("channel", channel));

    public void Failed(string channel, string reason) => _failed.Add(1, Tag("channel", channel), Tag("reason", reason));

    public void Skipped(string channel, string reason) => _skipped.Add(1, Tag("channel", channel), Tag("reason", reason));

    public void Dispose() => _meter.Dispose();

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);
}
