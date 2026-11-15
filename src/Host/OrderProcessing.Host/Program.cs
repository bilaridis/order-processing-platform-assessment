using Microsoft.AspNetCore.Authentication.JwtBearer;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OrderProcessing.BuildingBlocks.Infrastructure;
using OrderProcessing.Carriers;
using OrderProcessing.Ecommerce;
using OrderProcessing.Inventory;
using OrderProcessing.Notifications;
using OrderProcessing.OrderApi;
using OrderProcessing.Ordering.Module;
using OrderProcessing.Pricing;

// Composition root. The host owns cross-cutting concerns (authentication, telemetry, error format,
// health) and the list of modules. Everything else belongs to a module.

var builder = WebApplication.CreateBuilder(args);

IModule[] modules =
[
    new OrderingModule(),
    new InventoryModule(),
    new PricingModule(),
    new EcommerceModule(),
    new CarriersModule(),
    new OrderApiModule(),
    new NotificationsModule(),
];

builder.Services.AddBuildingBlocks();

// Security: every endpoint requires a valid JWT from the identity provider unless it opts out explicitly.
// Issuer and audience come from configuration ("Authentication:Schemes:Bearer").
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Errors: one format for all failures (RFC 9457 problem details), including the trace id for support.
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddHealthChecks();

// Observability: traces, metrics and logs over OTLP. The endpoint comes from OTEL_EXPORTER_OTLP_ENDPOINT,
// so the same image works with any backend. Without it nothing is exported.
var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("order-processing-platform"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource(ServiceCollectionExtensions.MessagingActivitySourceName))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter(NotificationsModule.MeterName))
    .WithLogging();

if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    telemetry.UseOtlpExporter();
}

foreach (var module in modules)
{
    module.Register(builder.Services, builder.Configuration);
}

var app = builder.Build();

// A body that cannot be read (malformed JSON, a value of the wrong type) is the caller's fault: 400, not 500.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
        ? badRequest.StatusCode
        : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// Liveness: the process is up. Readiness: dependencies (database, broker) are reachable; modules add
// their checks with the "ready" tag as real infrastructure is introduced.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

foreach (var module in modules)
{
    module.MapEndpoints(app);
}

await app.RunAsync();
