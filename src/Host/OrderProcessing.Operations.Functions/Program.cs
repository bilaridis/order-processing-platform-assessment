using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using OrderProcessing.BuildingBlocks.Azure;

// Function app "operations", on the on-demand plan (ADR-0010): tools for the people who run the
// platform. Today the dead-letter inspector and resubmit tool that ADR-0012 requires.

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddDeadLetterInspector(builder.Configuration);
builder.Services.AddAzureServiceBusMessaging(builder.Configuration);

await builder.Build().RunAsync();
