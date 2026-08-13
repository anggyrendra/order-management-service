namespace OrderManagement.Domain.Enums;

/// <summary>
/// Represents the lifecycle state of an order.
/// Terminal states (Delivered, Cancelled) cannot be changed any further.
/// </summary>
public enum OrderStatus
{
    Pending = 0,
    Confirmed = 1,
    Shipped = 2,
    Delivered = 3,
    Cancelled = 4
}
