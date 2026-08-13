using System.ComponentModel.DataAnnotations;
using OrderManagement.Domain.Enums;

namespace OrderManagement.Domain.Entities;

/// <summary>
/// A single line item belonging to an order. Captures the product reference and
/// the (immutable) quantity and unit price snapshot at the time of ordering.
/// </summary>
public class OrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrderId { get; set; }
    public Order? Order { get; set; }

    public Guid ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>Quantity ordered. Must be positive.</summary>
    public int Quantity { get; set; }

    /// <summary>Unit price captured at order creation time (price snapshot).</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Quantity * UnitPrice, persisted for audit/reporting.</summary>
    public decimal LineTotal => Quantity * UnitPrice;
}
