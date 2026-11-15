using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderProcessing.BuildingBlocks.Azure;
using OrderProcessing.BuildingBlocks.Infrastructure;
using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Ecommerce.Contracts;
using OrderProcessing.Inventory.Contracts;
using OrderProcessing.Ordering.Functions;
using OrderProcessing.Ordering.Module;
using OrderProcessing.Pricing.Contracts;

// Composition root of the Ordering service as Azure Functions (isolated worker, ADR-0009).
// Every function here has a ServiceBusTrigger. The service has no HTTP endpoint and calls no HTTP
// endpoint: commands arrive on its queue, events on its subscriptions, and everything it says to the
// other services is a command on their queue or an event on its own topic (ADR-0003).
//
// It registers the same module as the local host. Only the edges differ: Service Bus instead of the
// in-memory bus, and queue senders instead of in-process calls.
//
// NOT RUN, and one placeholder decides what it can do today: the order store is still in memory, so it
// lives in one process. Until the Azure SQL repository exists, the two apps of this service
// (ADR-0010) must run as ONE app, or an order received by one is invisible to the other.

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddBuildingBlocksForFunctions();
builder.Services.AddAzureServiceBusMessaging(builder.Configuration);

new OrderingModule().Register(builder.Services, builder.Configuration);

builder.Services.AddScoped<IPricingApi, QueuedPricingClient>();
builder.Services.AddScoped<IInventoryApi, QueuedInventoryClient>();
builder.Services.AddScoped<IEcommerceApi, QueuedEcommerceClient>();
builder.Services.AddScoped<ICarriersApi, QueuedCarriersClient>();

await builder.Build().RunAsync();
