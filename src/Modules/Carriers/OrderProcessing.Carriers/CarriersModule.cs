using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.BuildingBlocks.Infrastructure;
using OrderProcessing.Carriers.Contracts;

namespace OrderProcessing.Carriers;

public sealed class CarriersOptions
{
    public const string SectionName = "Carriers";

    /// <summary>Shared secret for carrier webhooks. Supplied from a secret store, never from source control.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Key that signs delivery offer tokens. Supplied from a secret store. Random per process if not set.</summary>
    public string? OfferSigningKey { get; set; }

    /// <summary>How long an offer may be used to place an order.</summary>
    public int OfferValidityMinutes { get; set; } = 30;

    /// <summary>
    /// Enabled carriers, in the order they are listed to the customer. The first one is also the default
    /// for a shipment requested without a delivery offer.
    /// </summary>
#pragma warning disable CA2227 // Bound from configuration.
    public IList<string> Enabled { get; set; } = [ExampleCarriers.Acs, ExampleCarriers.BoxNow, ExampleCarriers.SkroutzPoint];
#pragma warning restore CA2227
}

public sealed class CarriersModule : IModule
{
    public string Name => "Carriers";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CarriersOptions>(configuration.GetSection(CarriersOptions.SectionName));

        // One adapter per carrier. Swap for real ones: services.AddHttpClient<...>().AddStandardResilienceHandler();
        services.AddSingleton<ICarrier>(ExampleCarriers.CreateAcs());
        services.AddSingleton<ICarrier>(ExampleCarriers.CreateBoxNow());
        services.AddSingleton<ICarrier>(ExampleCarriers.CreateSkroutzPoint());
        services.AddSingleton<CarrierRegistry>();
        services.AddSingleton<DeliveryOfferTokens>();
        services.AddSingleton<InMemoryShipmentStore>();
        services.AddSingleton<IDeliveryOffersApi, DeliveryOffersApi>();
        services.AddScoped<ICarriersApi, CarriersApi>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No endpoint for front ends: they ask the Order API for delivery offers (ADR-0013). The only
        // HTTP into the carrier service is the carriers' own callbacks.
        // Machine-to-machine callback: authenticated by shared secret, not by user token.
        // A real carrier integration verifies an HMAC signature over the raw body and a timestamp instead.
        endpoints.MapPost("/webhooks/carriers", HandleWebhook)
            .WithTags("Webhooks")
            .AllowAnonymous();
    }

    // An HTTP trigger is not redelivered by Service Bus. If publishing fails here the answer is a 500,
    // and it is the carrier's own retry of the webhook that repeats the publish (ADR-0012).
    private static async Task<IResult> HandleWebhook(
        CarrierWebhookPayload payload,
        [FromHeader(Name = "X-Webhook-Secret")] string? secret,
        IOptions<CarriersOptions> options,
        IEventPublisher events,
        CancellationToken cancellationToken)
    {
        if (!SecretMatches(options.Value.WebhookSecret, secret))
        {
            return Results.Unauthorized();
        }

        switch (payload.Status)
        {
            case "dispatched" when payload is { Carrier: not null, TrackingNumber: not null }:
                await events.PublishAsync(
                    new ShipmentDispatched(payload.OrderId, payload.Carrier, payload.TrackingNumber).WithIdFor(payload.OrderId),
                    cancellationToken);
                return Results.Accepted();
            case "delivered":
                await events.PublishAsync(new ShipmentDelivered(payload.OrderId).WithIdFor(payload.OrderId), cancellationToken);
                return Results.Accepted();
            case "in_transit" or "out_for_delivery" or "ready_for_collection":
                await events.PublishAsync(
                    new ShipmentTrackingUpdated(payload.OrderId, payload.Carrier, payload.Status, payload.Location)
                        .WithIdFor(payload.OrderId, $"{payload.Status}|{payload.Location}"),
                    cancellationToken);
                return Results.Accepted();
            default:
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unsupported shipment update.");
        }
    }

    private static bool SecretMatches(string? expected, string? provided) =>
        !string.IsNullOrEmpty(expected)
        && provided is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided));
}

internal sealed record CarrierWebhookPayload(
    Guid OrderId, string Status, string? Carrier, string? TrackingNumber, string? Location = null);

/// <summary>Books and cancels shipments with exactly the carrier of the chosen offer.</summary>
internal sealed class CarriersApi(
    CarrierRegistry carriers, DeliveryOfferTokens tokens, InMemoryShipmentStore shipments, IEventPublisher events) : ICarriersApi
{
    public async Task RequestShipmentAsync(ShipmentRequest request, CancellationToken cancellationToken)
    {
        if (shipments.Exists(request.OrderId))
        {
            return;
        }

        VerifiedDeliveryOffer? offer = null;

        if (request.DeliveryOfferToken is not null)
        {
            var read = tokens.Read(request.DeliveryOfferToken, checkExpiry: false);

            if (read.IsFailure)
            {
                await FailAsync(null, read.Error.Message);
                return;
            }

            offer = read.Value;
        }

        var carrier = offer is null ? carriers.Default : carriers.Find(offer.Carrier);

        if (carrier is null)
        {
            await FailAsync(offer?.Carrier, "The carrier is not available");
            return;
        }

        var booking = await carrier.BookAsync(Key(request.OrderId), request, offer, cancellationToken);

        if (!booking.Accepted)
        {
            // No fallback to another carrier: the customer chose and paid for this one (ADR-0006).
            await FailAsync(carrier.Name, booking.RefusalReason ?? "Refused by the carrier");
            return;
        }

        shipments.Record(request.OrderId, carrier.Name);

        Task FailAsync(string? failedCarrier, string reason) =>
            events.PublishAsync(new ShipmentFailed(request.OrderId, failedCarrier, reason).WithIdFor(request.OrderId), cancellationToken);
    }

    public async Task RequestCancellationAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (shipments.TryRemove(orderId, out var name) && carriers.Find(name!) is { } carrier)
        {
            await carrier.CancelAsync(Key(orderId), cancellationToken);
        }
    }

    private static string Key(Guid orderId) => $"order-{orderId:N}";
}
