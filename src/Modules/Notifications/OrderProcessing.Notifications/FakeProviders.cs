using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OrderProcessing.Notifications;

/// <summary>
/// SKELETON PLACEHOLDER for the Azure Communication Services Email adapter (EmailClient with the
/// service's managed identity). Behaves like ACS: it accepts the message, and the outcome arrives later
/// as a delivery report. An address at bounce.example bounces.
/// </summary>
internal sealed class FakeAcsEmailSender(SimulatedDeliveryReports reports) : IEmailSender
{
    public string Provider => "acs-email";

    public Task<SendResult> SendAsync(string to, string language, OutgoingMessage message, CancellationToken cancellationToken)
    {
        var messageId = SimulatedDeliveryReports.MessageIdFor(Provider, message.IdempotencyKey);
        var bounced = to.EndsWith("@bounce.example", StringComparison.OrdinalIgnoreCase);
        reports.Report(new DeliveryReport(messageId, bounced ? DeliveryOutcome.Failed : DeliveryOutcome.Delivered, bounced ? "Bounced" : null));
        return Task.FromResult(new SendResult(messageId));
    }
}

/// <summary>
/// SKELETON PLACEHOLDER for the Azure Communication Services SMS adapter (SmsClient, with delivery
/// reports enabled). A number without an international prefix is refused at once; a number ending in
/// 000 is accepted and later reported undeliverable.
/// </summary>
internal sealed class FakeAcsSmsSender(SimulatedDeliveryReports reports) : ISmsSender
{
    public string Provider => "acs-sms";

    public Task<SendResult> SendAsync(string to, OutgoingMessage message, CancellationToken cancellationToken)
    {
        if (!to.StartsWith('+'))
        {
            return Task.FromResult(SendResult.Refused("Invalid phone number"));
        }

        var messageId = SimulatedDeliveryReports.MessageIdFor(Provider, message.IdempotencyKey);
        var unreachable = to.EndsWith("000", StringComparison.Ordinal);
        reports.Report(new DeliveryReport(messageId, unreachable ? DeliveryOutcome.Failed : DeliveryOutcome.Delivered, unreachable ? "Unreachable" : null));
        return Task.FromResult(new SendResult(messageId));
    }
}

/// <summary>
/// SKELETON PLACEHOLDER for the Azure Notification Hubs adapter. Notification Hubs reports no delivery
/// per device, so acceptance is the outcome. A customer whose id starts with "web-" has no app installed.
/// </summary>
internal sealed class FakeNotificationHubsSender : IPushSender
{
    public string Provider => "notification-hubs";

    public Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken) =>
        Task.FromResult(message.CustomerId.StartsWith("web-", StringComparison.Ordinal)
            ? SendResult.Refused("No device registered")
            : new SendResult(SimulatedDeliveryReports.MessageIdFor(Provider, message.IdempotencyKey), DeliveredOnAcceptance: true));
}

/// <summary>
/// SKELETON PLACEHOLDER for the path ACS → Event Grid → Service Bus queue → OnDeliveryReport. In the
/// local host the fake providers drop their reports here and a background loop hands them to
/// <see cref="DeliveryReportHandler"/>, a moment later and in its own scope, as the queue would.
/// </summary>
internal sealed class SimulatedDeliveryReports(IServiceScopeFactory scopes) : BackgroundService
{
    private readonly Channel<DeliveryReport> _reports = Channel.CreateUnbounded<DeliveryReport>();
    private readonly ConcurrentDictionary<string, bool> _reported = new();

    public static string MessageIdFor(string provider, string idempotencyKey) => $"{provider}:{idempotencyKey}";

    public void Report(DeliveryReport report)
    {
        // The provider deduplicates by operation id, so a repeated send gets no second report.
        if (_reported.TryAdd(report.ProviderMessageId, true))
        {
            _reports.Writer.TryWrite(report);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var report in _reports.Reader.ReadAllAsync(stoppingToken))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DeliveryReportHandler>().HandleAsync(report, stoppingToken);
        }
    }
}
