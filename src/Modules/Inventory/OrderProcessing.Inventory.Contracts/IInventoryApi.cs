using OrderProcessing.BuildingBlocks.Application;

namespace OrderProcessing.Inventory.Contracts;

/// <summary>
/// Public API of the inventory service: "inventory as a service". The caller asks to reserve items and
/// never names a warehouse; which source supplies the order is decided here (ADR-0006).
/// Both operations are requests. The outcome of a reservation arrives as <see cref="StockReserved"/> or
/// <see cref="StockReservationFailed"/>. Between services the requests are commands on
/// <see cref="InventoryMessaging.CommandQueue"/>; nothing is called over HTTP (ADR-0003).
/// </summary>
public interface IInventoryApi
{
    /// <summary>
    /// Checks availability and reserves stock for an order in one step, so two concurrent orders cannot
    /// both be promised the last item. All or nothing. Idempotent per <see cref="ReserveStockRequest.OrderId"/>.
    /// </summary>
    Task RequestReservationAsync(ReserveStockRequest request, CancellationToken cancellationToken);

    /// <summary>Releases the reservation of an order. Idempotent; releasing twice or never-reserved is a no-op.</summary>
    Task RequestReleaseAsync(Guid orderId, CancellationToken cancellationToken);
}

public sealed record ReserveStockRequest(Guid OrderId, IReadOnlyList<StockItem> Items);

public sealed record ReleaseStockRequest(Guid OrderId);

public sealed record StockItem(string Sku, int Quantity);

public sealed record StockShortage(string Sku, int Requested, int Available);

/// <param name="Source">The inventory source that holds the stock. Decides where the parcel starts.</param>
public sealed record StockReserved(Guid OrderId, string Source) : IntegrationEvent;

/// <param name="Shortages">What is missing. Empty when no source could be asked at all.</param>
/// <param name="Code"><c>inventory.insufficient_stock</c> or <c>inventory.unavailable</c>.</param>
public sealed record StockReservationFailed(Guid OrderId, string Code, IReadOnlyList<StockShortage> Shortages) : IntegrationEvent;

/// <summary>Where the commands of this service travel between services (ADR-0003).</summary>
public static class InventoryMessaging
{
    public const string CommandQueue = "inventory-commands";
    public const string ReserveStock = nameof(ReserveStock);
    public const string ReleaseStock = nameof(ReleaseStock);
}
