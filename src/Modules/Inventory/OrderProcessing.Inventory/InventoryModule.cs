using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderProcessing.BuildingBlocks.Application;
using OrderProcessing.BuildingBlocks.Infrastructure;
using OrderProcessing.Inventory.Contracts;

namespace OrderProcessing.Inventory;

/// <summary>Registry and routing rules of the inventory service. In Azure these are rows in its own tables.</summary>
public sealed class InventoryOptions
{
    public const string SectionName = "Inventory";

    /// <summary>Enabled sources in default priority order.</summary>
#pragma warning disable CA2227 // Bound from configuration.
    public IList<string> Sources { get; set; } = [ExampleSources.ThirdPartyLogistics, ExampleSources.InHouse];

    /// <summary>Routing rules: SKU prefix to the ordered sources that may supply it. The longest prefix wins.</summary>
    public Dictionary<string, string[]> Routes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore CA2227
}

public sealed class InventoryModule : IModule
{
    public string Name => "Inventory";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<InventoryOptions>(configuration.GetSection(InventoryOptions.SectionName));

        // One adapter per source. Swap for real ones: services.AddHttpClient<...>().AddStandardResilienceHandler();
        services.AddSingleton<IInventorySource>(ExampleSources.CreateThirdPartyLogistics());
        services.AddSingleton<IInventorySource>(ExampleSources.CreateInHouse());
        services.AddSingleton<InventorySourceResolver>();
        services.AddSingleton<InMemoryReservationStore>();
        services.AddScoped<IInventoryApi, InventoryApi>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No public HTTP surface today. Stock-level callbacks from a source would be mapped here.
    }
}

/// <summary>
/// Decides which sources may supply an order, in which order. Launch strategy: one source for the whole
/// order, so a source is a candidate only if the routing rules allow it for every item.
/// </summary>
internal sealed class InventorySourceResolver(IEnumerable<IInventorySource> sources, IOptions<InventoryOptions> options)
{
    private readonly Dictionary<string, IInventorySource> _byName =
        sources.ToDictionary(source => source.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IInventorySource> CandidatesFor(IReadOnlyList<StockItem> items)
    {
        IEnumerable<string> candidates = options.Value.Sources;

        foreach (var item in items)
        {
            var allowed = AllowedFor(item.Sku);
            candidates = candidates.Where(name => allowed.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        return [.. candidates.Where(_byName.ContainsKey).Select(name => _byName[name])];
    }

    public IInventorySource? Find(string name) => _byName.GetValueOrDefault(name);

    private IList<string> AllowedFor(string sku)
    {
        var route = options.Value.Routes
            .Where(rule => sku.StartsWith(rule.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(rule => rule.Key.Length)
            .Select(rule => rule.Value)
            .FirstOrDefault();

        return route ?? options.Value.Sources;
    }
}

/// <summary>
/// Inventory as a service: the caller asks to reserve items and never names a warehouse.
/// Tries the candidate sources in order and reserves the whole order at the first one that has everything.
/// </summary>
internal sealed class InventoryApi(InventorySourceResolver resolver, InMemoryReservationStore reservations, IEventPublisher events)
    : IInventoryApi
{
    public const string InsufficientStock = "inventory.insufficient_stock";
    public const string Unavailable = "inventory.unavailable";

    public async Task RequestReservationAsync(ReserveStockRequest request, CancellationToken cancellationToken)
    {
        // Asked again for an order that already holds stock: say so again, reserve nothing twice.
        if (reservations.TryGet(request.OrderId, out var held))
        {
            await events.PublishAsync(new StockReserved(request.OrderId, held!).WithIdFor(request.OrderId), cancellationToken);
            return;
        }

        IReadOnlyList<StockShortage>? fewestShortages = null;

        foreach (var source in resolver.CandidatesFor(request.Items))
        {
            IReadOnlyList<StockShortage> shortages;

            try
            {
                shortages = await source.TryReserveAsync(ReservationKey(request.OrderId), request.Items, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TimeoutException)
            {
                // This source cannot answer. Another one may.
                continue;
            }

            if (shortages.Count == 0)
            {
                // Record first, publish second: if the publish fails, the redelivered command finds the
                // reservation above and publishes again.
                reservations.Record(request.OrderId, source.Name);
                await events.PublishAsync(new StockReserved(request.OrderId, source.Name).WithIdFor(request.OrderId), cancellationToken);
                return;
            }

            if (fewestShortages is null || shortages.Count < fewestShortages.Count)
            {
                fewestShortages = shortages;
            }
        }

        // Fail closed (ADR-0005): if no source could confirm the stock, the order is not accepted.
        // The adapters have already retried inside their own timeouts by the time we get here.
        await events.PublishAsync(
            (fewestShortages is null
                ? new StockReservationFailed(request.OrderId, Unavailable, [])
                : new StockReservationFailed(request.OrderId, InsufficientStock, fewestShortages)).WithIdFor(request.OrderId),
            cancellationToken);
    }

    public async Task RequestReleaseAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (reservations.TryRemove(orderId, out var sourceName) && resolver.Find(sourceName!) is { } source)
        {
            await source.ReleaseAsync(ReservationKey(orderId), cancellationToken);
        }
    }

    private static string ReservationKey(Guid orderId) => $"order-{orderId:N}";
}
