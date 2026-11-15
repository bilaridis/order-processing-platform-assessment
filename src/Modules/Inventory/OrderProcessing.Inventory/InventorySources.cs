using System.Collections.Concurrent;
using OrderProcessing.Inventory.Contracts;

namespace OrderProcessing.Inventory;

/// <summary>
/// Port to one inventory source: our own warehouse system, a 3PL provider, a partner (ADR-0006).
/// One adapter per source. The adapter translates that source's model and errors into ours and owns its
/// timeouts and retries. Callers of the Inventory contract never see which source was used.
/// </summary>
internal interface IInventorySource
{
    /// <summary>Key of the source in the registry and in the routing rules.</summary>
    string Name { get; }

    /// <summary>All or nothing for the given items. Must be idempotent for the same <paramref name="reservationKey"/>.</summary>
    Task<IReadOnlyList<StockShortage>> TryReserveAsync(
        string reservationKey, IReadOnlyList<StockItem> items, CancellationToken cancellationToken);

    Task ReleaseAsync(string reservationKey, CancellationToken cancellationToken);
}

/// <summary>
/// SKELETON PLACEHOLDER for a real source adapter (typed HttpClient + resilience pipeline).
/// Stock is whatever <paramref name="stockOf"/> says for a SKU, minus what is currently reserved.
/// </summary>
internal sealed class FakeInventorySource(string name, Func<string, int> stockOf) : IInventorySource
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _reservedBySku = [];
    private readonly ConcurrentDictionary<string, IReadOnlyList<StockItem>> _reservations = new();

    public string Name => name;

    public Task<IReadOnlyList<StockShortage>> TryReserveAsync(
        string reservationKey, IReadOnlyList<StockItem> items, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_reservations.ContainsKey(reservationKey))
            {
                return Task.FromResult<IReadOnlyList<StockShortage>>([]);
            }

            var shortages = items
                .Select(item => (item, available: stockOf(item.Sku) - _reservedBySku.GetValueOrDefault(item.Sku)))
                .Where(x => x.available < x.item.Quantity)
                .Select(x => new StockShortage(x.item.Sku, x.item.Quantity, Math.Max(0, x.available)))
                .ToList();

            if (shortages.Count == 0)
            {
                foreach (var item in items)
                {
                    _reservedBySku[item.Sku] = _reservedBySku.GetValueOrDefault(item.Sku) + item.Quantity;
                }

                _reservations[reservationKey] = items;
            }

            return Task.FromResult<IReadOnlyList<StockShortage>>(shortages);
        }
    }

    public Task ReleaseAsync(string reservationKey, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_reservations.TryRemove(reservationKey, out var items))
            {
                foreach (var item in items)
                {
                    _reservedBySku[item.Sku] -= item.Quantity;
                }
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>The two example sources of the skeleton (docs/architecture/06-integration-services.md).</summary>
internal static class ExampleSources
{
    public const string ThirdPartyLogistics = "3pl";
    public const string InHouse = "in-house";

    /// <summary>A 3PL warehouse: 100 of everything, except "OUT-" SKUs (nowhere) and "INH-" SKUs (in-house only).</summary>
    public static FakeInventorySource CreateThirdPartyLogistics() => new(
        ThirdPartyLogistics,
        sku => sku.StartsWith("OUT-", StringComparison.OrdinalIgnoreCase)
            || sku.StartsWith("INH-", StringComparison.OrdinalIgnoreCase) ? 0 : 100);

    /// <summary>Our own warehouse: 100 of everything, except "OUT-" SKUs.</summary>
    public static FakeInventorySource CreateInHouse() => new(
        InHouse,
        sku => sku.StartsWith("OUT-", StringComparison.OrdinalIgnoreCase) ? 0 : 100);
}

/// <summary>SKELETON PLACEHOLDER for the service's Reservations table (PartitionKey = order id).</summary>
internal sealed class InMemoryReservationStore
{
    private readonly ConcurrentDictionary<Guid, string> _sourceByOrder = new();

    public bool TryGet(Guid orderId, out string? source) => _sourceByOrder.TryGetValue(orderId, out source);

    public void Record(Guid orderId, string source) => _sourceByOrder[orderId] = source;

    public bool TryRemove(Guid orderId, out string? source) => _sourceByOrder.TryRemove(orderId, out source);
}
