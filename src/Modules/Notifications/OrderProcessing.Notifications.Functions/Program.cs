using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using OrderProcessing.BuildingBlocks.Azure;
using OrderProcessing.BuildingBlocks.Infrastructure;
using OrderProcessing.Notifications;

// Composition root of the notification-integration-service as Azure Functions (isolated worker), on
// the on-demand plan (ADR-0010). It sends no command to anyone: it reads events and delivery reports,
// and publishes CustomerNotified / NotificationFailed on its own topic.

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddBuildingBlocksForFunctions();
builder.Services.AddAzureServiceBusMessaging(builder.Configuration);

new NotificationsModule().Register(builder.Services, builder.Configuration);

await builder.Build().RunAsync();
