using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderManagement.Application.DTOs;
using OrderManagement.Application.Interfaces;
using OrderManagement.Domain.Enums;
using OrderManagement.Domain.Exceptions;
using OrderManagement.Infrastructure.Data;
using OrderManagement.Tests.Helpers;
using Xunit;

namespace OrderManagement.Tests.Concurrency;

/// <summary>
/// Skenario A — Concurrent Stock Deduction.
///
/// Two users submit orders that both require 10 units of Product X while only
/// 15 units remain. The system MUST ensure either only one order succeeds, or
/// that the total stock deducted never exceeds 15 (i.e. never goes negative).
///
/// Because the stock deduction uses a single atomic conditional UPDATE
/// (UPDATE ... WHERE StockQuantity >= @qty), the database is the arbiter: at
/// most one of the two competing UPDATEs will affect a row.
///
/// IMPORTANT: each concurrent task resolves its own DI scope (and therefore its
/// own OrderService + DbContext), exactly like ASP.NET Core does per HTTP
/// request. EF Core's DbContext is NOT thread-safe and must never be shared
/// across concurrent tasks.
/// </summary>
public class ScenarioA_ConcurrentStockDeduction
{
    [Fact]
    public async Task Two_concurrent_orders_for_last_15_units_only_one_succeeds_and_stock_never_negative()
    {
        // Arrange: Product X has exactly 15 units. Two orders each want 10.
        await using var sp = await TestHostFactory.BuildAsync();
        var dbFactory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();

        var request = OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductX, 10);

        // Act: fire both requests at the same time, each in its own scope.
        var t1 = Task.Run(async () =>
        {
            var (orders, scope) = sp.CreateOrderScope();
            try { return (await orders.CreateOrderAsync($"idem-A-{Guid.NewGuid():N}", request), (Exception?)null); }
            catch (Exception ex) { return ((default(OrderResponse), false), ex); }
            finally { scope.Dispose(); }
        });
        var t2 = Task.Run(async () =>
        {
            var (orders, scope) = sp.CreateOrderScope();
            try { return (await orders.CreateOrderAsync($"idem-B-{Guid.NewGuid():N}", request), (Exception?)null); }
            catch (Exception ex) { return ((default(OrderResponse), false), ex); }
            finally { scope.Dispose(); }
        });
        var tasks = new[] { t1, t2 };
        var taskResults = await Task.WhenAll(tasks);

        // Assert: at most one order should have succeeded (15 >= 10, but 15 < 20).
        var successes = new List<OrderResponse>();
        var failures = new List<Exception>();
        foreach (var ((resp, _), err) in taskResults)
        {
            if (err == null && resp is not null) successes.Add(resp);
            else if (err is not null) failures.Add(err);
        }

        Assert.True(successes.Count <= 1,
            $"Expected at most 1 successful order, got {successes.Count}.");

        // Final stock must be 5 (one order) or 15 (none), and never negative.
        await using var db = await dbFactory.CreateDbContextAsync();
        var finalStock = await db.Products
            .Where(p => p.Id == TestHostFactory.ProductIds.ProductX)
            .Select(p => p.StockQuantity)
            .SingleAsync();

        Assert.True(finalStock >= 0, $"Stock went negative: {finalStock}");
        Assert.True(finalStock == 5 || finalStock == 15,
            $"Expected stock 5 (one order) or 15 (none), got {finalStock}.");

        // Any failure must be InsufficientStock (the loser of the atomic UPDATE).
        foreach (var f in failures)
            Assert.True(f is InsufficientStockException,
                $"Expected InsufficientStockException, got {f.GetType().Name}: {f.Message}");
    }

    [Fact]
    public async Task Many_concurrent_orders_never_drive_stock_negative()
    {
        // Stress test: 20 concurrent orders each wanting 2 units of a product
        // with only 10 in stock. At most 5 can succeed (5*2=10). The rest fail.
        // The invariant we care about: stock never < 0.
        await using var sp = await TestHostFactory.BuildAsync();

        // Reset Product X to exactly 10 for this test.
        using (var scope = sp.CreateScope())
        {
            var seedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = await seedDb.Products.SingleAsync(x => x.Id == TestHostFactory.ProductIds.ProductX);
            p.StockQuantity = 10;
            await seedDb.SaveChangesAsync();
        }

        var dbFactory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var request = OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductX, 2);

        // Each task gets its own scope/scope-disposed DbContext.
        var tasks = Enumerable.Range(0, 20)
            .Select(i => Task.Run<OrderResponse?>(async () =>
            {
                var (orders, scope) = sp.CreateOrderScope();
                try
                {
                    var (resp, _) = await orders.CreateOrderAsync($"stress-{i}-{Guid.NewGuid():N}", request);
                    return resp;
                }
                catch (InsufficientStockException)
                {
                    return null; // expected for the losers
                }
                finally
                {
                    scope.Dispose();
                }
            }))
            .ToList();

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r != null);

        await using var db = await dbFactory.CreateDbContextAsync();
        var finalStock = await db.Products
            .Where(p => p.Id == TestHostFactory.ProductIds.ProductX)
            .Select(p => p.StockQuantity)
            .SingleAsync();

        Assert.True(finalStock >= 0, $"Stock went negative: {finalStock}");
        Assert.True(successCount * 2 <= 10,
            $"More stock was deducted than available: {successCount} successes * 2 = {successCount * 2} > 10.");
        Assert.Equal(10 - successCount * 2, finalStock);
    }
}
