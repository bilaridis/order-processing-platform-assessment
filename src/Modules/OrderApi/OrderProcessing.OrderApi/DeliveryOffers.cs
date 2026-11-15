using System.Collections.Concurrent;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Domain;
using OrderProcessing.Carriers.Contracts;
using OrderProcessing.Ordering.Contracts;

namespace OrderProcessing.OrderApi;

// Delivery offers at checkout, through the Order API like everything else a front end does (ADR-0013).
// The front end never calls the carrier service: the Order API sends the request as a command, the
// carrier service answers with an event, and the projector below writes the answer into a view that
// the front end reads. Only carriers call the carrier service, through their webhooks.

/// <param name="Status"><c>Requested</c> until the carrier service has answered, then <c>Ready</c>.</param>
public sealed record DeliveryOffersView(
    Guid RequestId, string CustomerId, string Status, IReadOnlyList<DeliveryOffer> Offers, DateTimeOffset RequestedAt);

/// <summary>
/// Where the answers wait to be read. In Azure a table of the Order API, PartitionKey = customer id,
/// with a short time to live: an offer expires within minutes anyway.
/// </summary>
internal interface IDeliveryOffersViewStore
{
    Task<DeliveryOffersView?> FindAsync(string customerId, Guid requestId, CancellationToken cancellationToken);

    Task UpsertAsync(DeliveryOffersView view, CancellationToken cancellationToken);
}

/// <summary>SKELETON PLACEHOLDER for the Table storage table of the delivery offers views.</summary>
internal sealed class InMemoryDeliveryOffersViewStore : IDeliveryOffersViewStore
{
    private readonly ConcurrentDictionary<(string, Guid), DeliveryOffersView> _views = new();

    public Task<DeliveryOffersView?> FindAsync(string customerId, Guid requestId, CancellationToken cancellationToken) =>
        Task.FromResult(_views.GetValueOrDefault((customerId, requestId)));

    public Task UpsertAsync(DeliveryOffersView view, CancellationToken cancellationToken)
    {
        _views[(view.CustomerId, view.RequestId)] = view;
        return Task.CompletedTask;
    }
}

public sealed record RequestDeliveryOffers(
    string CustomerId, string DestinationCountry, string PostalCode, IReadOnlyList<OrderSubmittedItem> Items)
    : ICommand<Result<DeliveryOffersView>>;

/// <summary>
/// Writes the view as <c>Requested</c> first, so the front end can poll it at once, then sends the request
/// to the carrier service. It answers with the view as it is then: in the local host the answer may
/// already be there, in Azure it arrives a moment later.
/// </summary>
internal sealed class RequestDeliveryOffersHandler(
    IDeliveryOffersApi carriers, IDeliveryOffersViewStore views, TimeProvider clock)
    : ICommandHandler<RequestDeliveryOffers, Result<DeliveryOffersView>>
{
    public static readonly Error Invalid =
        Error.Validation("shipping.invalid_request", "Items and a 2-letter destination country are required.");

    public async Task<Result<DeliveryOffersView>> HandleAsync(RequestDeliveryOffers command, CancellationToken cancellationToken)
    {
        if (command.Items is not { Count: > 0 }
            || command.Items.Any(item => item.Quantity <= 0)
            || command.DestinationCountry is not { Length: 2 })
        {
            return Invalid;
        }

        var requestId = Guid.NewGuid();
        await views.UpsertAsync(
            new DeliveryOffersView(requestId, command.CustomerId, "Requested", [], clock.GetUtcNow()), cancellationToken);

        await carriers.RequestOffersAsync(
            new DeliveryOffersQuoteRequest(
                requestId,
                command.CustomerId,
                new DeliveryOffersRequest(
                    command.DestinationCountry,
                    command.PostalCode,
                    [.. command.Items.Select(item => new ShipmentItem(item.Sku, item.Quantity))])),
            cancellationToken);

        return (await views.FindAsync(command.CustomerId, requestId, cancellationToken))!;
    }
}

public sealed record GetDeliveryOffers(string CustomerId, Guid RequestId) : IQuery<Result<DeliveryOffersView>>;

/// <summary>Reads the view and nothing else. Another customer's request is not found.</summary>
internal sealed class GetDeliveryOffersHandler(IDeliveryOffersViewStore views)
    : IQueryHandler<GetDeliveryOffers, Result<DeliveryOffersView>>
{
    public static readonly Error NotFound = Error.NotFound("shipping.offers_not_found", "The delivery offers request was not found.");

    public async Task<Result<DeliveryOffersView>> HandleAsync(GetDeliveryOffers query, CancellationToken cancellationToken) =>
        await views.FindAsync(query.CustomerId, query.RequestId, cancellationToken) is { } view ? view : NotFound;
}

/// <summary>Writes the carrier service's answer into the view of the customer who asked. Safe to run twice.</summary>
internal sealed class DeliveryOffersProjector(IDeliveryOffersViewStore views, TimeProvider clock)
    : IIntegrationEventHandler<DeliveryOffersQuoted>
{
    public async Task HandleAsync(DeliveryOffersQuoted integrationEvent, CancellationToken cancellationToken)
    {
        var existing = await views.FindAsync(integrationEvent.CustomerId, integrationEvent.RequestId, cancellationToken);

        await views.UpsertAsync(
            new DeliveryOffersView(
                integrationEvent.RequestId,
                integrationEvent.CustomerId,
                "Ready",
                integrationEvent.Offers,
                existing?.RequestedAt ?? clock.GetUtcNow()),
            cancellationToken);
    }
}
