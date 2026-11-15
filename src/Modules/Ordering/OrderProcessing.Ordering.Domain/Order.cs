using OrderProcessing.BuildingBlocks.Domain;

namespace OrderProcessing.Ordering.Domain;

/// <summary>
/// The Order aggregate. It owns the lifecycle rules: every status change goes through one of the methods
/// below, is recorded in <see cref="History"/> and raises an <see cref="OrderStatusChanged"/> domain event.
/// Transition methods are idempotent (repeating the transition that already happened is a successful no-op)
/// because the events that drive them are delivered at least once.
/// </summary>
public sealed class Order : AggregateRoot<OrderId>
{
    private readonly List<OrderItem> _items;
    private readonly List<OrderStatusChange> _history = [];
    private List<OrderLine> _lines = [];

    private Order(
        OrderId id,
        string customerId,
        string currency,
        List<OrderItem> items,
        ShippingAddress shippingAddress,
        string paymentMethod,
        string? deliveryOfferToken,
        ContactDetails? contact,
        string? paymentMethodToken)
        : base(id)
    {
        CustomerId = customerId;
        Currency = currency;
        _items = items;
        ShippingAddress = shippingAddress;
        PaymentMethod = paymentMethod;
        PaymentMethodToken = paymentMethodToken;
        DeliveryOfferToken = deliveryOfferToken;
        Contact = contact;
    }

    public string CustomerId { get; }

    public string Currency { get; }

    /// <summary>What was asked for. Known from the start.</summary>
    public IReadOnlyList<OrderItem> Items => _items;

    /// <summary>The priced lines. Empty until Pricing has answered.</summary>
    public IReadOnlyList<OrderLine> Lines => _lines;

    /// <summary>The price the customer agreed to. Null while the order is being checked.</summary>
    public OrderPrice? Price { get; private set; }

    public ShippingAddress ShippingAddress { get; }

    /// <summary>How the customer pays: card, wallet. It selects the payment provider.</summary>
    public string PaymentMethod { get; }

    /// <summary>
    /// Optional and opaque: a provider or wallet token for a method that is already authenticated or
    /// saved. It holds no card number. Without it the customer pays on the provider's page (ADR-0014).
    /// </summary>
    public string? PaymentMethodToken { get; }

    /// <summary>
    /// The delivery offer the customer chose, as signed by the carrier service. Opaque here: Ordering
    /// stores it and hands it back when it requests the shipment, so the chosen carrier is the one booked.
    /// </summary>
    public string? DeliveryOfferToken { get; }

    /// <summary>How the customer wants to be told about this order. Carried to the notification service, not used here.</summary>
    public ContactDetails? Contact { get; }

    public OrderStatus Status { get; private set; }

    public string? PaymentReference { get; private set; }

    public ShipmentInfo? Shipment { get; private set; }

    public IReadOnlyList<OrderStatusChange> History => _history;

    public DateTimeOffset SubmittedAt => _history[0].OccurredAt;

    public bool CanBeCancelled => Status is OrderStatus.Submitted or OrderStatus.Pending or OrderStatus.Confirmed;

    /// <summary>
    /// Records an order as received. Nothing has been checked yet: the price and the stock are requested
    /// from their services afterwards, and only then is the order accepted (<see cref="Accept"/>).
    /// </summary>
    public static Result<Order> Submit(
        OrderId id,
        string customerId,
        string currency,
        IEnumerable<OrderItem> items,
        ShippingAddress shippingAddress,
        string paymentMethod,
        DateTimeOffset now,
        string? deliveryOfferToken = null,
        ContactDetails? contact = null,
        string? paymentMethodToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

        var orderItems = items.ToList();

        if (orderItems.Count == 0)
        {
            return OrderErrors.NoLines;
        }

        if (orderItems.Any(item => item.Quantity <= 0))
        {
            return OrderErrors.InvalidQuantity;
        }

        if (orderItems.DistinctBy(item => item.Sku, StringComparer.OrdinalIgnoreCase).Count() != orderItems.Count)
        {
            return OrderErrors.DuplicateSku;
        }

        var order = new Order(id, customerId, currency, orderItems, shippingAddress, paymentMethod, deliveryOfferToken, contact, paymentMethodToken);
        order.MoveTo(OrderStatus.Submitted, null, now, isInitial: true);
        return order;
    }

    /// <summary>
    /// Stores the quote of the Pricing service as the order's price snapshot. Not a status change.
    /// The aggregate checks only what it can know by itself: that the quote adds up.
    /// </summary>
    public Result ApplyQuote(IEnumerable<OrderLine> lines, OrderPrice price)
    {
        ArgumentNullException.ThrowIfNull(price);

        if (Price is not null)
        {
            return Result.Success();
        }

        if (Status != OrderStatus.Submitted)
        {
            return OrderErrors.InvalidTransition(Status, OrderStatus.Pending);
        }

        var orderLines = lines.ToList();
        var subtotal = orderLines.Sum(line => line.LineTotal);
        var total = subtotal + price.Components.Sum(component => component.Amount);

        if (subtotal != price.Subtotal || total != price.Total || price.Total < 0)
        {
            return OrderErrors.InconsistentPrice;
        }

        _lines = orderLines;
        Price = price;
        return Result.Success();
    }

    /// <summary>Priced and stock reserved: the order is accepted. Submitted → Pending.</summary>
    public Result Accept(DateTimeOffset now)
    {
        if (Status == OrderStatus.Pending)
        {
            return Result.Success();
        }

        if (Status != OrderStatus.Submitted)
        {
            return OrderErrors.InvalidTransition(Status, OrderStatus.Pending);
        }

        if (Price is null)
        {
            return OrderErrors.NotPriced;
        }

        MoveTo(OrderStatus.Pending, null, now);
        return Result.Success();
    }

    /// <summary>Payment was authorized. Pending → Confirmed.</summary>
    public Result Confirm(string paymentReference, DateTimeOffset now)
    {
        if (Status == OrderStatus.Confirmed)
        {
            return Result.Success();
        }

        if (Status != OrderStatus.Pending)
        {
            return OrderErrors.InvalidTransition(Status, OrderStatus.Confirmed);
        }

        PaymentReference = paymentReference;
        MoveTo(OrderStatus.Confirmed, null, now);
        return Result.Success();
    }

    /// <summary>The platform could not accept or complete the order. Submitted or Pending → Rejected.</summary>
    public Result Reject(string reason, DateTimeOffset now)
    {
        if (Status == OrderStatus.Rejected)
        {
            return Result.Success();
        }

        if (Status is not (OrderStatus.Submitted or OrderStatus.Pending))
        {
            return OrderErrors.InvalidTransition(Status, OrderStatus.Rejected);
        }

        MoveTo(OrderStatus.Rejected, reason, now);
        return Result.Success();
    }

    /// <summary>Cancellation on request. Permitted until the order is handed to the carrier.</summary>
    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status == OrderStatus.Cancelled)
        {
            return Result.Success();
        }

        if (!CanBeCancelled)
        {
            return OrderErrors.CannotCancel(Status);
        }

        MoveTo(OrderStatus.Cancelled, reason, now);
        return Result.Success();
    }

    /// <summary>The carrier has the parcel. Confirmed → Shipped.</summary>
    public Result MarkShipped(ShipmentInfo shipment, DateTimeOffset now)
    {
        if (Status == OrderStatus.Shipped)
        {
            return Result.Success();
        }

        if (Status != OrderStatus.Confirmed)
        {
            return OrderErrors.InvalidTransition(Status, OrderStatus.Shipped);
        }

        Shipment = shipment;
        MoveTo(OrderStatus.Shipped, null, now);
        return Result.Success();
    }

    /// <summary>Shipped → Delivered.</summary>
    public Result MarkDelivered(DateTimeOffset now)
    {
        if (Status == OrderStatus.Delivered)
        {
            return Result.Success();
        }

        if (Status != OrderStatus.Shipped)
        {
            return OrderErrors.InvalidTransition(Status, OrderStatus.Delivered);
        }

        MoveTo(OrderStatus.Delivered, null, now);
        return Result.Success();
    }

    private void MoveTo(OrderStatus status, string? reason, DateTimeOffset now, bool isInitial = false)
    {
        OrderStatus? previous = isInitial ? null : Status;
        Status = status;
        _history.Add(new OrderStatusChange(status, now, reason));
        Raise(new OrderStatusChanged(Id, previous, status, reason, now));
    }
}
