using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderManagement.Application.Interfaces;
using OrderManagement.Domain.Entities;
using OrderManagement.Infrastructure.Data;

namespace OrderManagement.Application.Services;

/// <summary>
/// Minimal product service for the prototype. Provides seeding (so the API is
/// usable immediately) and read endpoints. Stock mutations are owned by
/// <see cref="OrderService"/> via atomic SQL, not here.
/// </summary>
public class ProductService : IProductService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ProductService> _logger;
    private static readonly object _seedLock = new();
    private static bool _seeded;

    public ProductService(AppDbContext db, ILogger<ProductService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task EnsureProductsSeededAsync(CancellationToken cancellationToken = default)
    {
        // Idempotent seeding guarded by a process-local lock + a DB check, so that
        // concurrent startup / first-request callers don't double-insert.
        if (_seeded) return;
        lock (_seedLock)
        {
            if (_seeded) return;
        }

        if (await _db.Products.AnyAsync(cancellationToken))
        {
            _seeded = true;
            return;
        }

        var seed = new List<Product>
        {
            new() { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Name = "Product X", StockQuantity = 15, Price = 100m },
            new() { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Name = "Product Y", StockQuantity = 50, Price = 25m },
            new() { Id = Guid.Parse("00000000-0000-0000-0000-000000000003"), Name = "Product Z", StockQuantity = 10, Price = 500m },
        };

        _db.Products.AddRange(seed);
        await _db.SaveChangesAsync(cancellationToken);
        _seeded = true;
        _logger.LogInformation("Seeded {Count} products.", seed.Count);
    }

    public async Task<ProductResponse?> GetProductAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var p = await _db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (p == null) return null;
        return Map(p);
    }

    public async Task<List<ProductResponse>> ListProductsAsync(CancellationToken cancellationToken = default)
    {
        var products = await _db.Products.AsNoTracking().ToListAsync(cancellationToken);
        return products.Select(Map).ToList();
    }

    private static ProductResponse Map(Product p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        StockQuantity = p.StockQuantity,
        Price = p.Price
    };
}
