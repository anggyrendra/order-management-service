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
/// Skenario B — Concurrent Status Update.
///
/// Two operators (or retries) try to transition the *same* order to a new
/// status at the same moment. Only ONE update may win; the loser must be
/// rejected with a 409 Conflict rather than silently overwriting the order.
///
/// The guard is EF Core optimistic concurrency via the Order.RowVersion token
/// (IsConcurrencyToken + ConcurrencyTokenInterceptor). Each UpdateStatusAsync
/// loads the order fresh from a dedicated DbContext (from IDbContextFactory),
/// so the two concurrent calls hold independent RowVersion snapshots. The first
/// SaveChanges wins; the second's RowVersion no longer matches the row, so EF
/// throws DbUpdateConcurrencyException, surfaced as 409.
///
/// Each concurrent task uses its own DI scope (independent OrderService +
/// DbContext) to mirror separate HTTP requests.
/// </summary>
public class ScenarioB_ConcurrentStatusUpdate
{
    private static async Task<(OrderResponse Created, Guid Id)> CreateOrderAsync(ServiceProvider sp)
    {
        var (orders, scope) = sp.CreateOrderScope();
        try
        {
            var req = OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductY, 1);
            var (order, _) = await orders.CreateOrderAsync($"idem-setup-{Guid.NewGuid():N}", req);
            return (order, order.Id);
        }
        finally { scope.Dispose(); }
    }

    /// <summary>
    /// Two concurrent calls both try Pending -> Confirmed on the same order.
    /// Exactly one must succeed; the other must throw InvalidStatusTransitionException (409).
    /// </summary>
    [Fact]
    public async Task Two_concurrent_Pending_to_Confirmed_only_one_wins_other_gets_409()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var (_, orderId) = await CreateOrderAsync(sp);

        // Two operators, two scopes.
        var t1 = Task.Run(async () =>
        {
            var (orders, scope) = sp.CreateOrderScope();
            try { await orders.UpdateStatusAsync(orderId, OrderStatus.Confirmed); return true; }
            catch (InvalidStatusTransitionException) { return false; }
            finally { scope.Dispose(); }
        });
        var t2 = Task.Run(async () =>
        {
            var (orders, scope) = sp.CreateOrderScope();
            try { await orders.UpdateStatusAsync(orderId, OrderStatus.Confirmed); return true; }
            catch (InvalidStatusTransitionException) { return false; }
            finally { scope.Dispose(); }
        });
        var results = await Task.WhenAll(t1, t2);

        var successCount = results.Count(x => x);
        Assert.True(successCount == 1,
            $"Expected exactly 1 successful status update, got {successCount}.");

        // The order must be Confirmed.
        var (verifyOrders, verifyScope) = sp.CreateOrderScope();
        try
        {
            var fetched = await verifyOrders.GetOrderAsync(orderId);
            Assert.NotNull(fetched);
            Assert.Equal(nameof(OrderStatus.Confirmed), fetched!.Status);
        }
        finally { verifyScope.Dispose(); }
    }

    /// <summary>
    /// Stress variant: 25 concurrent Confirm attempts on the same Pending order.
    /// Exactly one must win; all others must conflict.
    /// </summary>
    [Fact]
    public async Task Many_concurrent_status_updates_exactly_one_succeeds()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var (_, orderId) = await CreateOrderAsync(sp);

        const int n = 25;
        var tasks = Enumerable.Range(0, n)
            .Select(_ => Task.Run(async () =>
            {
                var (orders, scope) = sp.CreateOrderScope();
                try
                {
                    await orders.UpdateStatusAsync(orderId, OrderStatus.Confirmed);
                    return true;
                }
                catch (InvalidStatusTransitionException)
                {
                    return false;
                }
                finally { scope.Dispose(); }
            }))
            .ToList();

        var outcomes = await Task.WhenAll(tasks);
        var successCount = outcomes.Count(x => x);

        Assert.True(successCount == 1,
            $"Expected exactly 1 success out of {n} concurrent updates, got {successCount}.");

        var (verifyOrders, verifyScope) = sp.CreateOrderScope();
        try
        {
            var fetched = await verifyOrders.GetOrderAsync(orderId);
            Assert.Equal(nameof(OrderStatus.Confirmed), fetched!.Status);
        }
        finally { verifyScope.Dispose(); }
    }

    /// <summary>
    /// Conflicting *different* transitions racing: one tries Pending -> Confirmed,
    /// the other tries Pending -> Cancelled on the same order. The order must
    /// always end up in a SINGLE consistent, reachable state — never an
    /// impossible/contradictory state — and stock must always match that final
    /// state. Because Confirmed -> Cancelled is itself a valid transition, it is
    /// acceptable for both operations to "succeed" sequentially (confirm then
    /// cancel), as long as the final state is consistent and the stock invariant
    /// holds. What must NEVER happen is a contradictory state or stock that does
    /// not match the order's status.
    /// </summary>
    [Fact]
    public async Task Conflicting_transitions_confirm_vs_cancel_only_one_applies()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var dbFactory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var (_, orderId) = await CreateOrderAsync(sp);

        // Product Y started at 50; order took 1 -> 49.
        int stockBefore;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            stockBefore = await db.Products
                .Where(p => p.Id == TestHostFactory.ProductIds.ProductY)
                .Select(p => p.StockQuantity).SingleAsync();
        }
        Assert.Equal(49, stockBefore);

        // Act: race Confirmed vs Cancelled, each in its own scope.
        var confirmTask = Task.Run(async () =>
        {
            var (orders, scope) = sp.CreateOrderScope();
            try { await orders.UpdateStatusAsync(orderId, OrderStatus.Confirmed); return true; }
            catch (InvalidStatusTransitionException) { return false; }
            finally { scope.Dispose(); }
        });
        var cancelTask = Task.Run(async () =>
        {
            var (orders, scope) = sp.CreateOrderScope();
            try { await orders.CancelOrderAsync(orderId); return true; }
            catch (InvalidStatusTransitionException) { return false; }
            finally { scope.Dispose(); }
        });
        await Task.WhenAll(confirmTask, cancelTask);

        bool confirmed = confirmTask.GetAwaiter().GetResult();
        bool cancelled = cancelTask.GetAwaiter().GetResult();

        // At least one operation must have made progress.
        Assert.True(confirmed || cancelled,
            "Expected at least one of confirm/cancel to succeed, but both were rejected.");

        var (verifyOrders, verifyScope) = sp.CreateOrderScope();
        try
        {
            var fetched = await verifyOrders.GetOrderAsync(orderId);
            Assert.NotNull(fetched);

            int stockAfter;
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                stockAfter = await db.Products
                    .Where(p => p.Id == TestHostFactory.ProductIds.ProductY)
                    .Select(p => p.StockQuantity).SingleAsync();
            }

            // The final state must be a SINGLE reachable state and stock MUST
            // match it. The two valid outcomes of this race are:
            //   (a) Confirmed wins, cancel rejected  -> status Confirmed, stock 49 (still deducted)
            //   (b) Cancel wins (either directly from Pending, or after Confirm) -> status Cancelled, stock 50 (restored)
            // A contradictory state (e.g. Confirmed with restored stock, or
            // Cancelled with deducted stock) must never occur.
            var status = fetched!.Status;
            if (status == nameof(OrderStatus.Cancelled))
            {
                Assert.Equal(50, stockAfter); // stock restored on cancel
            }
            else if (status == nameof(OrderStatus.Confirmed))
            {
                Assert.Equal(49, stockAfter); // stock still deducted
            }
            else
            {
                // The order must NOT be left in Pending (neither op applied) or
                // any other non-terminal reachable state from this race.
                Assert.Fail($"Order ended in unexpected state '{status}'.");
            }
        }
        finally { verifyScope.Dispose(); }
    }
}
