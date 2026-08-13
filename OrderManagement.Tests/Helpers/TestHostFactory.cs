using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderManagement.Api.Extensions;
using OrderManagement.Application.DTOs;
using OrderManagement.Application.Interfaces;
using OrderManagement.Application.Services;
using OrderManagement.Domain.Entities;
using OrderManagement.Domain.Enums;
using OrderManagement.Infrastructure.Data;

namespace OrderManagement.Tests.Helpers;

/// <summary>
/// Shared helpers for building an isolated SQLite-backed test stack.
/// Each test gets its own unique in-memory SQLite database (shared connection
/// kept open for the test lifetime) so concurrency tests run against a real
/// relational engine with real transactions and real unique constraints —
/// which is essential for validating Skenario A/B/C.
/// </summary>
public static class TestHostFactory
{
    /// <summary>
    /// Builds a ServiceProvider wired with a fresh SQLite DB (file-backed,
    /// unique per call) so tests are fully isolated.
    /// </summary>
    public static async Task<ServiceProvider> BuildAsync(string? dbName = null)
    {
        dbName ??= $"test_{Guid.NewGuid():N}.db";
        var dbPath = Path.Combine(Path.GetTempPath(), dbName);
        if (File.Exists(dbPath)) File.Delete(dbPath);
        var connectionString = $"Data Source={dbPath}";

        var services = new ServiceCollection();
        services.AddLogging();

        // Add the concurrency-token interceptor in the options so factory-created
        // contexts also get a fresh RowVersion on every save (SQLite has no
        // native rowversion; the interceptor generates it in app code).
        services.AddDbContext<AppDbContext>(
            o => { o.UseSqlite(connectionString); o.AddInterceptors(new ConcurrencyTokenInterceptor()); },
            ServiceLifetime.Scoped);
        services.AddDbContextFactory<AppDbContext>(
            o => { o.UseSqlite(connectionString); o.AddInterceptors(new ConcurrencyTokenInterceptor()); },
            ServiceLifetime.Scoped);

        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IProductService, ProductService>();

        var sp = services.BuildServiceProvider();

        // Create schema + seed.
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            await SeedProductsAsync(db);
        }

        return sp;
    }

    /// <summary>Seeds the standard Product X (15), Y (50), Z (10).</summary>
    public static async Task SeedProductsAsync(AppDbContext db)
    {
        if (await db.Products.AnyAsync()) return;
        db.Products.AddRange(
            new Product { Id = ProductIds.ProductX, Name = "Product X", StockQuantity = 15, Price = 100m },
            new Product { Id = ProductIds.ProductY, Name = "Product Y", StockQuantity = 50, Price = 25m },
            new Product { Id = ProductIds.ProductZ, Name = "Product Z", StockQuantity = 10, Price = 500m }
        );
        await db.SaveChangesAsync();
    }

    public static class ProductIds
    {
        public static readonly Guid ProductX = Guid.Parse("00000000-0000-0000-0000-000000000001");
        public static readonly Guid ProductY = Guid.Parse("00000000-0000-0000-0000-000000000002");
        public static readonly Guid ProductZ = Guid.Parse("00000000-0000-0000-0000-000000000003");
    }
}

/// <summary>Convenience factory for a CreateOrderRequest.</summary>
public static class OrderRequestFactory
{
    public static CreateOrderRequest Create(Guid productId, int qty, string? customer = null) => new()
    {
        CustomerId = customer ?? "CUST-1",
        ShippingAddress = "123 Test Street",
        Items = new List<CreateOrderItemRequest>
        {
            new() { ProductId = productId, Quantity = qty }
        }
    };
}

/// <summary>
/// Extension methods for resolving a fresh DI scope (and thus a fresh
/// OrderService + DbContext) per concurrent task. This mirrors how ASP.NET
/// Core gives each HTTP request its own scope, so concurrency tests exercise
/// truly independent contexts — EF Core's DbContext is not thread-safe and must
/// not be shared across concurrent tasks.
/// </summary>
public static class TestHostFactoryScopeExtensions
{
    /// <summary>
    /// Creates a new DI scope and resolves an <see cref="IOrderService"/> from it.
    /// Dispose the returned scope when the task is done.
    /// </summary>
    public static (IOrderService Orders, IServiceScope Scope) CreateOrderScope(this ServiceProvider sp)
    {
        var scope = sp.CreateScope();
        var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
        return (orders, scope);
    }
}
