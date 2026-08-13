using System.ComponentModel.DataAnnotations;
using OrderManagement.Domain.Enums;

namespace OrderManagement.Domain.Entities;

/// <summary>
/// Aggregates a customer's order. Status transitions are governed by a state
/// machine (see OrderStateMachine). Optimistic concurrency via RowVersion ensures
/// two concurrent status updates cannot both succeed (Skenario B).
/// </summary>
public class Order : IConcurrencyToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    public string CustomerId { get; set; } = string.Empty;

    [MaxLength(500)]
    public string ShippingAddress { get; set; } = string.Empty;

    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    public decimal TotalAmount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// EF Core optimistic concurrency token. When two concurrent updates target
    /// the same order, the second SaveChanges will fail with a DbUpdateConcurrencyException
    /// because the RowVersion it read no longer matches the one in the database.
    /// </summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public List<OrderItem> Items { get; set; } = new();

    // Computed helper - true once the order has reached a terminal state.
    public bool IsTerminal =>
        Status == OrderStatus.Delivered || Status == OrderStatus.Cancelled;
}
