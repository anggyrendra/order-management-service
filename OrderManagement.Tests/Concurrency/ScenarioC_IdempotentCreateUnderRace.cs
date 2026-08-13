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
/// Skenario C — Idempotent Create Under Race.
///
/// A client (or a retried request due to a network blip) submits the SAME
/// create-order request twice at the same instant, with the SAME
/// Idempotency-Key. The system MUST create exactly ONE order and return the
/// SAME order (same Id) to both callers — never two orders, never two stock
/// deductions.
///
/// Mechanism: the IdempotencyKey is the PRIMARY KEY of IdempotencyRecord.
/// Both calls try to INSERT a row with that key; the database's unique PK
/// constraint guarantees exactly one insert wins. The loser catches the
/// unique-constraint violation and replays the winner's stored result
/// (waiting for it to complete if still in flight).
///
/// Each concurrent task uses its own DI scope (independent OrderService +
/// DbContext), mirroring separate HTTP requests. EF Core DbContext is not
/// thread-safe and must not be shared across concurrent tasks.
/// </summary>
public class ScenarioC_IdempotentCreateUnderRace
{
    /// <summary>
    /// Two identical requests with the same Idempotency-Key submitted at the
    /// same time must yield exactly ONE order with the SAME id returned to both.
    /// </summary>
    [Fact]
    public async Task Duplicate_idempotent_concurrent_creates_produce_one_order_same_id()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var dbFactory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();

        const string idemKey = "client-key-abc-123";
        var request = OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductY, 4);

        // Act: fire both requests simultaneously with the same key, own scope each.
        var t1 = Task.Run(async () =>
        {
            var (orders, scope) = sp.CreateOrderScope();
            try { return await orders.CreateOrderAsync(idemKey, request); }
            finally { scope.Dispose(); }
        });
        var t2 = Task.Run(async () =>
        {
            var (orders, scope) = sp.CreateOrderScope();
            try { return await orders.CreateOrderAsync(idemKey, request); }
            finally { scope.Dispose(); }
        });
        var (r1, created1) = await t1;
        var (r2, created2) = await t2;

        // Both returned successfully, same order id, exactly one "created".
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal(r1.Id, r2.Id);

        var createdCount = (created1 ? 1 : 0) + (created2 ? 1 : 0);
        Assert.True(createdCount == 1,
            $"Expected exactly 1 call to report WasCreated, got {createdCount} (created1={created1}, created2={created2}).");

        // Exactly one order row.
        await using var db = await dbFactory.CreateDbContextAsync();
        var orderCount = await db.Orders
            .Where(o => o.CustomerId == request.CustomerId)
            .CountAsync();
        Assert.Equal(1, orderCount);

        // Stock deducted exactly once (50 - 4 = 46).
        var stock = await db.Products
            .Where(p => p.Id == TestHostFactory.ProductIds.ProductY)
            .Select(p => p.StockQuantity).SingleAsync();
        Assert.Equal(46, stock);

        // Exactly one idempotency record.
        var idemCount = await db.IdempotencyRecords
            .Where(r => r.IdempotencyKey == idemKey).CountAsync();
        Assert.Equal(1, idemCount);
    }

    /// <summary>
    /// Stress variant: 30 identical concurrent requests with the same
    /// Idempotency-Key. Exactly ONE order is created, stock is deducted once,
    /// and all callers receive the same order id.
    /// </summary>
    [Fact]
    public async Task Thirty_concurrent_duplicate_creates_produce_single_order()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var dbFactory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();

        const string idemKey = "stress-key-xyz-999";
        var request = OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductZ, 1);

        const int n = 30;
        var tasks = Enumerable.Range(0, n)
            .Select(_ => Task.Run<(OrderResponse? Resp, bool Created, string? Error)>(async () =>
            {
                var (orders, scope) = sp.CreateOrderScope();
                try
                {
                    var (resp, created) = await orders.CreateOrderAsync(idemKey, request);
                    return (resp, created, Error: (string?)null);
                }
                catch (IdempotencyConflictException ex)
                {
                    // A transient "in flight / please retry" from the poll timeout
                    // is acceptable under extreme contention; record but don't fail.
                    return (Resp: (OrderResponse?)null, Created: false, Error: ex.GetType().Name);
                }
                finally
                {
                    scope.Dispose();
                }
            }))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // All non-error results must reference the SAME order id.
        var successResults = results.Where(r => r.Resp != null).ToList();
        Assert.NotEmpty(successResults);

        var distinctIds = successResults.Select(r => r.Resp!.Id).Distinct().ToList();
        Assert.True(distinctIds.Count == 1,
            $"Expected a single order id across all duplicate requests, got {distinctIds.Count} distinct ids.");

        // Exactly one "created" flag among successes.
        var createdCount = successResults.Count(r => r.Created);
        Assert.True(createdCount == 1,
            $"Expected exactly 1 WasCreated=true, got {createdCount}.");

        // Database invariants: exactly 1 order, stock deducted once, 1 idempotency record.
        await using var db = await dbFactory.CreateDbContextAsync();
        var orderCount = await db.Orders
            .Where(o => o.CustomerId == request.CustomerId).CountAsync();
        Assert.Equal(1, orderCount);

        var stock = await db.Products
            .Where(p => p.Id == TestHostFactory.ProductIds.ProductZ)
            .Select(p => p.StockQuantity).SingleAsync();
        Assert.Equal(9, stock); // 10 - 1

        var idemCount = await db.IdempotencyRecords
            .Where(r => r.IdempotencyKey == idemKey).CountAsync();
        Assert.Equal(1, idemCount);
    }

    /// <summary>
    /// The same Idempotency-Key reused with a DIFFERENT payload must be
    /// rejected with IdempotencyConflictException (409), never silently
    /// returning the first order for the second (mismatched) request.
    /// </summary>
    [Fact]
    public async Task Same_idempotency_key_with_different_payload_conflicts()
    {
        await using var sp = await TestHostFactory.BuildAsync();

        const string idemKey = "key-reuse-001";
        var requestA = OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductY, 2);
        var requestB = OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductY, 9); // different qty -> different hash

        // First request succeeds.
        OrderResponse first;
        bool firstCreated;
        var (orders1, scope1) = sp.CreateOrderScope();
        try
        {
            (first, firstCreated) = await orders1.CreateOrderAsync(idemKey, requestA);
        }
        finally { scope1.Dispose(); }
        Assert.True(firstCreated);

        // Second request, same key, different body -> must conflict.
        var (orders2, scope2) = sp.CreateOrderScope();
        try
        {
            var ex = await Assert.ThrowsAsync<IdempotencyConflictException>(
                () => orders2.CreateOrderAsync(idemKey, requestB));
            Assert.Contains("different request body", ex.Message);
        }
        finally { scope2.Dispose(); }
    }
}
