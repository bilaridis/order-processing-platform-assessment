using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderProcessing.BuildingBlocks.Infrastructure;

namespace OrderProcessing.Notifications;

/// <summary>
/// The notification-integration-service: email and SMS through Azure Communication Services, push
/// through Azure Notification Hubs (ADR-0006). It has no endpoints: it only consumes events.
/// </summary>
public sealed class NotificationsModule : IModule
{
    /// <summary>The OpenTelemetry meter of this service, for the host to export (ADR-0008).</summary>
    public const string MeterName = "OrderProcessing.Notifications";

    public string Name => "Notifications";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<NotificationStore>();
        services.AddSingleton<CustomerPreferences>();
        services.AddSingleton<NotificationPolicy>();
        services.AddSingleton<NotificationMetrics>();

        // One adapter per channel. Replace the fakes with ACS EmailClient / SmsClient and the
        // Notification Hubs client, all with the service's managed identity.
        services.AddSingleton<SimulatedDeliveryReports>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<SimulatedDeliveryReports>());
        services.AddSingleton<IEmailSender, FakeAcsEmailSender>();
        services.AddSingleton<ISmsSender, FakeAcsSmsSender>();
        services.AddSingleton<IPushSender, FakeNotificationHubsSender>();

        services.AddScoped<DeliveryReportHandler>();
        services.AddIntegrationEventHandler<CustomerNotifier>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // None. Delivery reports reach the service through Event Grid and a Service Bus queue, not HTTP.
    }
}
