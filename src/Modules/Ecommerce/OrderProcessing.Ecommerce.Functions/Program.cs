using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using OrderProcessing.BuildingBlocks.Azure;
using OrderProcessing.BuildingBlocks.Infrastructure;
using OrderProcessing.Ecommerce;

// Composition root of the ecommerce-integration-service (payments) as Azure Functions (isolated worker).
// It needs no client of another service: it receives commands from its queue and publishes events.

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddBuildingBlocksForFunctions();
builder.Services.AddAzureServiceBusMessaging(builder.Configuration);

new EcommerceModule().Register(builder.Services, builder.Configuration);

await builder.Build().RunAsync();
