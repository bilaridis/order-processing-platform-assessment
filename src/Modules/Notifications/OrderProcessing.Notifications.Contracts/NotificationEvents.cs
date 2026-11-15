using OrderProcessing.BuildingBlocks.Application;

namespace OrderProcessing.Notifications.Contracts;

/// <summary>
/// A message reached the customer: the email or SMS provider reported it delivered, or the push
/// service accepted it. Published by the notification service from the delivery report, so that the
/// order timeline shows it and support can answer "did the customer get it?".
/// </summary>
/// <param name="Channel">email, sms or push (<see cref="NotificationChannels"/>).</param>
/// <param name="Trigger">What the message was about, for example order.shipped or shipment.out_for_delivery.</param>
public sealed record CustomerNotified(Guid OrderId, string Channel, string Trigger) : IntegrationEvent;

/// <summary>
/// A message could not be delivered and will not be retried: a hard bounce, an invalid number, a
/// rejected sender. The customer may have heard on another channel.
/// </summary>
public sealed record NotificationFailed(Guid OrderId, string Channel, string Trigger, string Reason) : IntegrationEvent;

public static class NotificationChannels
{
    public const string Email = "email";
    public const string Sms = "sms";
    public const string Push = "push";
}

/// <summary>Where the notification service's messages travel (ADR-0003, ADR-0006).</summary>
public static class NotificationsMessaging
{
    public const string EventsTopic = "notifications-events";

    /// <summary>
    /// Delivery reports of Azure Communication Services. ACS publishes them to Event Grid, and an Event
    /// Grid subscription (Terraform) delivers them into this queue, so the function that reads them is a
    /// Service Bus trigger like every other function of the platform.
    /// </summary>
    public const string DeliveryReportQueue = "notifications-delivery-reports";
}
