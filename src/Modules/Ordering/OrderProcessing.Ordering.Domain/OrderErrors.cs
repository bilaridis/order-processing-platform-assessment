using OrderProcessing.BuildingBlocks.Domain;

namespace OrderProcessing.Ordering.Domain;

public static class OrderErrors
{
    public static readonly Error NoLines =
        Error.Validation("order.no_lines", "An order must contain at least one product.");

    public static readonly Error InvalidQuantity =
        Error.Validation("order.invalid_quantity", "Quantities must be greater than zero.");

    public static readonly Error DuplicateSku =
        Error.Validation("order.duplicate_sku", "Each product may appear only once in an order.");

    public static readonly Error InconsistentPrice =
        Error.Validation("order.inconsistent_price", "The order price does not add up.");

    public static readonly Error NotPriced =
        Error.Conflict("order.not_priced", "An order cannot be accepted before it has a price.");

    public static readonly Error NotFound =
        Error.NotFound("order.not_found", "The order does not exist.");

    public static Error CannotCancel(OrderStatus status) =>
        Error.Conflict("order.cannot_cancel", $"An order that is {status} can no longer be cancelled.");

    public static Error InvalidTransition(OrderStatus from, OrderStatus to) =>
        Error.Conflict("order.invalid_transition", $"An order cannot go from {from} to {to}.");
}
