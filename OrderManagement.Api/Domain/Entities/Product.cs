using System.ComponentModel.DataAnnotations;
using OrderManagement.Domain.Enums;

namespace OrderManagement.Domain.Entities;

/// <summary>
/// Simple product used as the inventory unit for the prototype.
/// StockQuantity is protected by optimistic concurrency (RowVersion) and by an
/// atomic conditional UPDATE so it can never go negative under concurrent orders.
/// </summary>
public class Product : IConcurrencyToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Available stock. Deductions happen via atomic conditional SQL so that
    /// concurrent orders cannot drive this below zero.
    /// </summary>
    public int StockQuantity { get; set; }

    public decimal Price { get; set; }

    /// <summary>
    /// EF Core optimistic concurrency token (application-managed via
    /// <c>ConcurrencyTokenInterceptor</c> because SQLite has no native
    /// rowversion type). Regenerated on every write so a concurrent update
    /// changes it and makes the loser's SaveChanges throw
    /// DbUpdateConcurrencyException. Combined with the atomic conditional
    /// UPDATE this gives us both optimistic locking and a guaranteed-not-negative
    /// invariant.
    /// </summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
