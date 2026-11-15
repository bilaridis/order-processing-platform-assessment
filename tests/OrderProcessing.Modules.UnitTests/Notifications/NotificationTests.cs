using System.Text;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using OrderProcessing.BuildingBlocks.Azure;
using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Notifications;
using OrderProcessing.Notifications.Contracts;
using OrderProcessing.Ordering.Contracts;

namespace OrderProcessing.Modules.UnitTests.Notifications;

public sealed class NotificationTests : IDisposable
{
    private static readonly OrderSummaryDestination Destination = new("Ada Lovelace", "1 Main St", null, "Athens", "10431", "GR");

    private readonly NotificationStore _store = new();
    private readonly CustomerPreferences _preferences = new();
    private readonly RecordingSender _email = new("acs-email");
    private readonly RecordingSender _sms = new("acs-sms");
    private readonly RecordingSender _push = new("notification-hubs", deliveredOnAcceptance: true);
    private readonly RecordingPublisher _published = new();
    private readonly NotificationMetrics _metrics = new();
    private readonly CustomerNotifier _notifier;
    private readonly Guid _orderId = Guid.NewGuid();

    public NotificationTests() =>
        _notifier = new CustomerNotifier(
            _store, new NotificationPolicy(), _preferences, _email, _sms, _push, _published, _metrics, NullLogger<CustomerNotifier>.Instance);

    [Fact]
    public async Task An_accepted_order_is_confirmed_by_email_and_push_but_not_by_sms()
    {
        await SubmitAsync(new CustomerContact("ada@example.com", "+306900000001", "en"));

        await _notifier.HandleAsync(new OrderPlaced(_orderId, "customer-1", 29.64m, "EUR", 2, null!), CancellationToken.None);

        Assert.Equal("ada@example.com", Assert.Single(_email.Sent).To);
        Assert.Contains("accepted", _email.Sent[0].Message.Text, StringComparison.Ordinal);
        Assert.Single(_push.Sent);
        Assert.Empty(_sms.Sent);
    }

    [Fact]
    public async Task Out_for_delivery_is_worth_an_sms()
    {
        await SubmitAsync(new CustomerContact(null, "+306900000001", "el"));

        await _notifier.HandleAsync(new ShipmentTrackingUpdated(_orderId, "ACS", "out_for_delivery", null), CancellationToken.None);

        Assert.Equal("+306900000001", Assert.Single(_sms.Sent).To);
        Assert.Contains("σήμερα", _sms.Sent[0].Message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redelivered_event_sends_nothing_twice()
    {
        await SubmitAsync(new CustomerContact("ada@example.com", null, "en"));
        var shipped = new OrderShipped(_orderId, "ACS", "T-1", 4);

        await _notifier.HandleAsync(shipped, CancellationToken.None);
        await _notifier.HandleAsync(shipped, CancellationToken.None);

        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task A_late_event_is_not_sent_after_a_newer_one()
    {
        await SubmitAsync(new CustomerContact("ada@example.com", null, "en"));

        await _notifier.HandleAsync(new OrderDelivered(_orderId, 5), CancellationToken.None);
        await _notifier.HandleAsync(new OrderShipped(_orderId, "ACS", "T-1", 4), CancellationToken.None);
        await _notifier.HandleAsync(new ShipmentTrackingUpdated(_orderId, "ACS", "out_for_delivery", null), CancellationToken.None);

        Assert.Equal("order.delivered", Assert.Single(_email.Sent).Message.Trigger);
    }

    [Fact]
    public async Task Without_contact_details_only_push_can_reach_the_customer()
    {
        await SubmitAsync(contact: null);

        await _notifier.HandleAsync(new OrderPlaced(_orderId, "customer-1", 10m, "EUR", 2, null!), CancellationToken.None);

        Assert.Empty(_email.Sent);
        Assert.Single(_push.Sent);
    }

    [Fact]
    public async Task An_event_that_overtakes_OrderSubmitted_fails_so_that_the_bus_delivers_it_again()
    {
        await Assert.ThrowsAsync<RecipientNotKnownException>(
            () => _notifier.HandleAsync(new OrderPlaced(_orderId, "customer-1", 10m, "EUR", 2, null!), CancellationToken.None));
    }

    [Fact]
    public async Task A_customer_can_turn_off_sms_but_not_the_email_receipt()
    {
        _preferences.Disable("customer-1", NotificationChannels.Sms);
        _preferences.Disable("customer-1", NotificationChannels.Email);
        await SubmitAsync(new CustomerContact("ada@example.com", "+306900000001", "en"));

        await _notifier.HandleAsync(new ShipmentTrackingUpdated(_orderId, "ACS", "ready_for_collection", "BoxNow Syntagma"), CancellationToken.None);
        await _notifier.HandleAsync(new OrderPlaced(_orderId, "customer-1", 10m, "EUR", 2, null!), CancellationToken.None);

        Assert.Empty(_sms.Sent);
        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task A_refused_sms_is_published_as_NotificationFailed()
    {
        _sms.RefuseWith = "Invalid phone number";
        await SubmitAsync(new CustomerContact(null, "6900000001", "en"));

        await _notifier.HandleAsync(new ShipmentTrackingUpdated(_orderId, "ACS", "out_for_delivery", null), CancellationToken.None);

        var failed = Assert.Single(_published.Events.OfType<NotificationFailed>());
        Assert.Equal((NotificationChannels.Sms, "Invalid phone number"), (failed.Channel, failed.Reason));
    }

    [Fact]
    public async Task Delivery_reports_become_CustomerNotified_or_NotificationFailed_once_per_message()
    {
        await SubmitAsync(new CustomerContact("ada@example.com", null, "en"));
        await _notifier.HandleAsync(new OrderShipped(_orderId, "ACS", "T-1", 4), CancellationToken.None);
        var reports = new DeliveryReportHandler(_store, _published, _metrics, NullLogger<DeliveryReportHandler>.Instance);
        var messageId = _email.Sent[0].ProviderMessageId;

        await reports.HandleAsync(new DeliveryReport(messageId, DeliveryOutcome.Delivered, null), CancellationToken.None);
        await reports.HandleAsync(new DeliveryReport(messageId, DeliveryOutcome.Delivered, null), CancellationToken.None);
        await reports.HandleAsync(new DeliveryReport("unknown", DeliveryOutcome.Failed, "Bounced"), CancellationToken.None);

        var notified = _published.Events.OfType<CustomerNotified>().Where(notice => notice.Channel == NotificationChannels.Email).ToList();
        Assert.Equal(2, notified.Count);
        Assert.Equal(notified[0].EventId, notified[1].EventId);
        Assert.Equal(("email", "order.shipped"), (notified[0].Channel, notified[0].Trigger));
    }

    [Theory]
    [InlineData("Microsoft.Communication.EmailDeliveryReportReceived", "status", "Delivered", "Delivered")]
    [InlineData("Microsoft.Communication.EmailDeliveryReportReceived", "status", "Bounced", "Failed")]
    [InlineData("Microsoft.Communication.SMSDeliveryReportReceived", "deliveryStatus", "Failed", "Failed")]
    public void An_ACS_report_from_Event_Grid_is_read(string eventType, string statusField, string status, string expected)
    {
        var json = $$$"""{"eventType":"{{{eventType}}}","data":{"messageId":"m-1","{{{statusField}}}":"{{{status}}}"}}""";

        var report = AcsDeliveryReports.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(("m-1", expected), (report!.ProviderMessageId, report.Outcome.ToString()));
    }

    [Fact]
    public void A_dead_letter_is_described_by_order_step_and_reason_and_goes_back_to_its_topic()
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            messageId: "e-1", subject: "OrderShipped", sessionId: "order-1", deliveryCount: 10, sequenceNumber: 7,
            properties: new Dictionary<string, object>
            {
                ["DeadLetterReason"] = "MaxDeliveryCountExceeded",
                ["DeadLetterErrorDescription"] = "Message could not be consumed after 10 delivery attempts.",
            });

        var record = DeadLetterRecord.From("ordering-events/Subscriptions/notifications-on-order-shipped", message);

        Assert.Equal(("order-1", "OrderShipped", "MaxDeliveryCountExceeded", 10), (record.OrderId, record.Subject, record.Reason, record.DeliveryCount));
        Assert.Equal("ordering-events", DeadLetterRecord.ResubmitTargetFor(record.EntityPath));
        Assert.Equal("payments-commands", DeadLetterRecord.ResubmitTargetFor("payments-commands"));
        Assert.NotEqual("e-1", DeadLetterRecord.ResubmittedMessageId("e-1", DateTimeOffset.UtcNow));
    }

    public void Dispose() => _metrics.Dispose();

    private Task SubmitAsync(CustomerContact? contact) =>
        _notifier.HandleAsync(
            new OrderSubmitted(_orderId, "customer-1", [new OrderSubmittedItem("SKU-1", 1)], Destination, 1, contact),
            CancellationToken.None);

    private sealed class RecordingSender(string provider, bool deliveredOnAcceptance = false) : IEmailSender, ISmsSender, IPushSender
    {
        public List<(string? To, OutgoingMessage Message, string ProviderMessageId)> Sent { get; } = [];

        public string? RefuseWith { get; set; }

        public string Provider => provider;

        public Task<SendResult> SendAsync(string to, string language, OutgoingMessage message, CancellationToken cancellationToken) =>
            Record(to, message);

        public Task<SendResult> SendAsync(string to, OutgoingMessage message, CancellationToken cancellationToken) => Record(to, message);

        public Task<SendResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken) => Record(null, message);

        private Task<SendResult> Record(string? to, OutgoingMessage message)
        {
            if (RefuseWith is { } reason)
            {
                return Task.FromResult(SendResult.Refused(reason));
            }

            var id = $"{provider}:{message.IdempotencyKey}";
            Sent.Add((to, message, id));
            return Task.FromResult(new SendResult(id, DeliveredOnAcceptance: deliveredOnAcceptance));
        }
    }
}
