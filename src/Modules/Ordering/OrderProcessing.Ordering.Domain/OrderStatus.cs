namespace OrderProcessing.Ordering.Domain;

/// <summary>
/// Order lifecycle. Allowed transitions are enforced by <see cref="Order"/> and documented in
/// docs/architecture/02-order-lifecycle.md.
/// </summary>
public enum OrderStatus
{
    /// <summary>Received and being checked: priced, then stock reserved. Not accepted yet.</summary>
    Submitted,

    /// <summary>Accepted: priced and stock reserved. Waiting for payment.</summary>
    Pending,

    /// <summary>Paid. Handed to shipping.</summary>
    Confirmed,

    /// <summary>Handed to the carrier. Can no longer be cancelled.</summary>
    Shipped,

    /// <summary>Terminal.</summary>
    Delivered,

    /// <summary>Terminal. Cancelled on request before shipping.</summary>
    Cancelled,

    /// <summary>Terminal. Could not be completed by the platform (for example payment declined).</summary>
    Rejected,
}
