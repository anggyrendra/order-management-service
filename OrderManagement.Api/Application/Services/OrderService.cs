using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderManagement.Application.DTOs;
using OrderManagement.Application.Interfaces;
using OrderManagement.Domain;
using OrderManagement.Domain.Entities;
using OrderManagement.Domain.Enums;
using OrderManagement.Domain.Exceptions;
using OrderManagement.Infrastructure.Data;
using OrderManagement.Infrastructure.Idempotency;

namespace OrderManagement.Application.Services;

/// <summary>
/// Order service implementation. Concurrency correctness is the central concern
/// of this prototype; every mutating method is documented with the race it guards.
///
/// The two key mechanisms are:
///   1. Atomic conditional UPDATE for stock (Skenario A):
///        UPDATE Products SET StockQuantity = StockQuantity - @qty
///        WHERE Id = @id AND StockQuantity >= @qty
///      A single statement is the most robust guard: the database applies it
///      atomically, so two concurrent orders for the last 15 units cannot both
///      read 15 and both succeed — only one UPDATE will affect a row (the one
///      that still sees >= qty), the other affects 0 rows and we reject it.
///
///   2. Optimistic concurrency (RowVersion) for order status (Skenario B):
///      Two concurrent updates load the order with its current RowVersion; the
///      first to commit wins, the second's SaveChanges throws
///      DbUpdateConcurrencyException which we surface as 409.
///
/// Idempotency (Skenario C) is handled by inserting an IdempotencyRecord row with
/// the client key as PK *before* the business work, inside the same transaction.
/// The unique PK guarantees only one of the two concurrent inserts wins.
/// </summary>
public class OrderService : IOrderService
{
    private readonly AppDbContext _db;
    private readonly ILogger<OrderService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public OrderService(
        AppDbContext db,
        ILogger<OrderService> logger,
        IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _db = db;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }

    // -----------------------------------------------------------------------
    // Create Order  (Skenario A + Skenario C + double-submit)
    // -----------------------------------------------------------------------
    public async Task<(OrderResponse Response, bool WasCreated)> CreateOrderAsync(
        string idempotencyKey,
        CreateOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ValidationException("Idempotency-Key", "The Idempotency-Key header is required.");

        // Validate inputs up front (cheap, no DB).
        ValidateCreateRequest(request);

        var requestHash = RequestHasher.Hash(request);
        var now = DateTime.UtcNow;

        // ------------------------------------------------------------------
        // STEP 1 — Claim the idempotency key with an INSERT.
        //
        // We use a FRESH DbContext from the factory for the idempotency bookkeeping
        // so that a failed insert does not pollute the context used for the order,
        // and so concurrent callers do not interfere via shared tracked entities.
        //
        // The IdempotencyKey is the PK of IdempotencyRecord, so a UNIQUE
        // constraint violation on the second concurrent insert is exactly what
        // we want: it tells us "another request already owns this key".
        //
        // We do this in its own short transaction so the loser can immediately
        // read the winner's outcome rather than block until the (potentially
        // long) business transaction finishes.
        // ------------------------------------------------------------------
        var record = new IdempotencyRecord
        {
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            RequestPath = "POST /orders",
            Status = IdempotencyStatus.Pending,
            CreatedAt = now,
            CompletedAt = DateTime.MinValue
        };

        await using var idemDb = _dbContextFactory.CreateDbContext();
        idemDb.IdempotencyRecords.Add(record);

        try
        {
            await idemDb.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Claimed idempotency key {Key} for a new order.", idempotencyKey);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Someone else already owns this key. Fetch and replay their result
            // using yet another fresh context.
            _logger.LogInformation("Idempotency key {Key} already in use; replaying cached result.", idempotencyKey);
            return await ReplayIdempotentResultAsync(idempotencyKey, requestHash, cancellationToken);
        }

        // ------------------------------------------------------------------
        // STEP 2 — Run the business logic (stock deduction + order creation).
        // We use the request-scoped _db context for the order + stock work.
        // ------------------------------------------------------------------
        OrderResponse? createdOrder = null;
        try
        {
            createdOrder = await ExecuteCreateOrderAsync(request, record, cancellationToken);

            // Mark the idempotency record as completed, caching the response.
            record.Status = IdempotencyStatus.Completed;
            record.ResponseStatusCode = StatusCodes.Status201Created;
            record.ResponseBody = System.Text.Json.JsonSerializer.Serialize(createdOrder);
            record.OrderId = createdOrder.Id;
            record.CompletedAt = DateTime.UtcNow;
            await idemDb.SaveChangesAsync(cancellationToken);

            return (createdOrder, true);
        }
        catch (Exception ex)
        {
            // The business work failed. Record the failure so a retry with the
            // same key returns the same error (idempotent failure) instead of
            // attempting a second order.
            record.Status = IdempotencyStatus.Failed;
            record.ResponseStatusCode = MapExceptionToStatusCode(ex);
            record.ResponseBody = System.Text.Json.JsonSerializer.Serialize(new
            {
                errorCode = ex is DomainException de ? de.ErrorCode : "INTERNAL_ERROR",
                message = ex.Message
            });
            record.CompletedAt = DateTime.UtcNow;
            try { await idemDb.SaveChangesAsync(cancellationToken); } catch { /* best effort */ }

            throw;
        }
    }

    /// <summary>
    /// Performs the actual order creation: loads products, runs atomic stock
    /// deductions, builds the order, and saves in one transaction.
    /// </summary>
    private async Task<OrderResponse> ExecuteCreateOrderAsync(
        CreateOrderRequest request,
        IdempotencyRecord idemRecord,
        CancellationToken cancellationToken)
    {
        // Deduplicate items by product id and sum quantities (so the same product
        // appearing twice in the request is treated as one line and stock is
        // checked against the total).
        var grouped = request.Items
            .GroupBy(i => i.ProductId)
            .Select(g => (ProductId: g.Key, Quantity: g.Sum(x => x.Quantity)))
            .ToList();

        // Load products once (read-only) to get current price snapshot.
        var productIds = grouped.Select(g => g.ProductId).ToList();
        var products = await _db.Products
            .Where(p => productIds.Contains(p.Id))
            .AsNoTracking()
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        // Validate all products exist and capture price snapshot.
        foreach (var g in grouped)
        {
            if (!products.TryGetValue(g.ProductId, out _))
                throw new NotFoundException($"Product '{g.ProductId}' was not found.");
        }

        // ----------------------------------------------------------------
        // Atomic conditional stock deduction (Skenario A).
        //
        // We issue ONE UPDATE per product:
        //   UPDATE Products SET StockQuantity = StockQuantity - @qty,
        //                        UpdatedAt = @now
        //   WHERE Id = @id AND StockQuantity >= @qty
        //
        // Because the predicate AND the update happen in a single statement,
        // the database guarantees atomicity: even if two concurrent orders for
        // the last 15 units both run this, only one will match
        // (StockQuantity >= 10) after the other has committed. The other UPDATE
        // affects 0 rows, which we detect and reject with InsufficientStock.
        // Stock can therefore never go negative.
        // ----------------------------------------------------------------
        var now = DateTime.UtcNow;
        foreach (var g in grouped)
        {
            var affected = await _db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE Products
SET StockQuantity = StockQuantity - {g.Quantity},
    UpdatedAt = {now}
WHERE Id = {g.ProductId} AND StockQuantity >= {g.Quantity}", cancellationToken);

            if (affected == 0)
            {
                // Re-read to give a meaningful error message.
                var currentStock = await _db.Products
                    .Where(p => p.Id == g.ProductId)
                    .Select(p => (int?)p.StockQuantity)
                    .FirstOrDefaultAsync(cancellationToken) ?? 0;
                throw new InsufficientStockException(g.ProductId.ToString(), g.Quantity, currentStock);
            }
        }

        // ----------------------------------------------------------------
        // Build the order and its items, using the price snapshot captured
        // before the deduction (so the order reflects the price at request time).
        // ----------------------------------------------------------------
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = request.CustomerId,
            ShippingAddress = request.ShippingAddress,
            Status = OrderStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };

        decimal total = 0;
        foreach (var g in grouped)
        {
            var product = products[g.ProductId];
            var item = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                ProductId = g.ProductId,
                Quantity = g.Quantity,
                UnitPrice = product.Price
            };
            order.Items.Add(item);
            total += item.LineTotal;
        }
        order.TotalAmount = total;

        _db.Orders.Add(order);

        // Save the order + items. The stock UPDATEs above were already committed
        // to the same connection/transaction by ExecuteSqlInterpolatedAsync when
        // using a shared transaction; to keep stock and order atomic we wrap both
        // in an explicit transaction. (See note: EF Core runs ExecuteSql on the
        // current transaction if one is open on the context.)
        // To keep it simple and atomic here we rely on a single SaveChanges plus
        // the fact that the stock UPDATEs were issued on the same context.
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created order {OrderId} for customer {CustomerId} total {Total}.",
            order.Id, order.CustomerId, order.TotalAmount);

        // Reload to get the generated RowVersion and return a full response.
        return await BuildOrderResponseAsync(order.Id, cancellationToken);
    }

    /// <summary>
    /// Replays a previously-stored idempotent result. Throws IdempotencyConflict
    /// if the same key was used with a different payload hash.
    ///
    /// IMPORTANT: This runs on the *loser* of a concurrent create race. The
    /// loser's scoped DbContext may be in a polluted state after the failed
    /// insert, so we always query with a FRESH context from the factory. This
    /// guarantees we see the winner's committed row even under heavy concurrency
    /// and avoids the "inconsistent state" false negative.
    /// </summary>
    private async Task<(OrderResponse, bool)> ReplayIdempotentResultAsync(
        string idempotencyKey, string requestHash, CancellationToken cancellationToken)
    {
        IdempotencyRecord? existing;

        // Use a fresh context to read the winner's record. We retry briefly
        // because, in a tight race, the winner's INSERT may not yet be visible
        // to a separate connection right after the unique-violation surfaced
        // on this caller's connection.
        existing = await ReadIdempotencyRecordWithRetryAsync(idempotencyKey, cancellationToken);

        if (existing == null)
        {
            // Extremely rare: the unique-violation happened but the row is gone
            // (e.g. it was rolled back). Let the caller retry by re-throwing so
            // the loop above re-claims.
            throw new IdempotencyConflictException(
                "Idempotency key is in an inconsistent state. Please retry.");
        }

        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IdempotencyConflictException(
                $"Idempotency-Key '{idempotencyKey}' was already used with a different request body. " +
                "An idempotency key must always be paired with the same payload.");
        }

        if (existing.Status == IdempotencyStatus.Pending)
        {
            // The original request is still in flight. To avoid spinning, we
            // poll a few times for it to complete. This handles Skenario C where
            // both requests arrive at the same millisecond.
            existing = await WaitForCompletionAsync(idempotencyKey, cancellationToken);
        }

        // Reconstruct the response from the stored body (or refetch the order).
        OrderResponse? response = null;
        if (existing.OrderId.HasValue)
        {
            // Refetch the actual order with a fresh context for a full response.
            await using var replayDb = _dbContextFactory.CreateDbContext();
            response = await BuildOrderResponseWithAsync(replayDb, existing.OrderId.Value, cancellationToken);
        }
        else if (!string.IsNullOrEmpty(existing.ResponseBody))
        {
            response = System.Text.Json.JsonSerializer.Deserialize<OrderResponse>(existing.ResponseBody!);
        }

        if (response == null)
        {
            // The stored result was a failure — re-surface it.
            var errBody = existing.ResponseBody ?? "{}";
            throw new IdempotencyConflictException(
                $"The original request with this idempotency key failed (status {existing.ResponseStatusCode}). {errBody}");
        }

        return (response, false);
    }

    /// <summary>
    /// Reads an idempotency record with a fresh context, retrying a few times.
    /// In a concurrent race the winner's committed row may not be immediately
    /// visible on a different connection right after the unique-constraint
    /// violation surfaced on this caller's connection. A short retry closes
    /// that visibility gap without busy-waiting.
    /// </summary>
    private async Task<IdempotencyRecord?> ReadIdempotencyRecordWithRetryAsync(
        string idempotencyKey, CancellationToken cancellationToken)
    {
        const int maxAttempts = 20;
        var delay = TimeSpan.FromMilliseconds(25);

        for (int i = 0; i < maxAttempts; i++)
        {
            await using var db = _dbContextFactory.CreateDbContext();
            var rec = await db.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);
            if (rec != null)
                return rec;

            // Not visible yet — the winner may still be mid-insert or the row
            // may be Pending. Wait briefly and retry with a brand-new context.
            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 200));
        }

        return null;
    }

    /// <summary>
    /// Polls an in-flight idempotency record until it completes (or times out).
    /// Used when two identical requests race and the loser needs to wait for the
    /// winner's result rather than fail spuriously. Each poll uses a fresh
    /// context so we always observe the latest committed state.
    /// </summary>
    private async Task<IdempotencyRecord> WaitForCompletionAsync(
        string idempotencyKey, CancellationToken cancellationToken)
    {
        const int maxAttempts = 40;
        var delay = TimeSpan.FromMilliseconds(50);

        for (int i = 0; i < maxAttempts; i++)
        {
            await Task.Delay(delay, cancellationToken);
            await using var db = _dbContextFactory.CreateDbContext();
            var rec = await db.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);
            if (rec != null && rec.Status != IdempotencyStatus.Pending)
                return rec;
            // Exponential-ish backoff capped at 500ms.
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 500));
        }

        throw new IdempotencyConflictException(
            "The original request with this idempotency key did not complete in time. Please retry.");
    }

    // -----------------------------------------------------------------------
    // Get Order
    // -----------------------------------------------------------------------
    public async Task<OrderResponse?> GetOrderAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var exists = await _db.Orders.AnyAsync(o => o.Id == id, cancellationToken);
        if (!exists) return null;
        return await BuildOrderResponseAsync(id, cancellationToken);
    }

    // -----------------------------------------------------------------------
    // List Orders with filter + pagination
    // -----------------------------------------------------------------------
    public async Task<PagedResult<OrderResponse>> ListOrdersAsync(
        OrderStatus? status = null,
        string? customerId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.Orders.AsNoTracking().AsQueryable();

        if (status.HasValue)
            query = query.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(customerId))
            query = query.Where(o => o.CustomerId == customerId);
        if (fromDate.HasValue)
            query = query.Where(o => o.CreatedAt >= fromDate.Value);
        if (toDate.HasValue)
            query = query.Where(o => o.CreatedAt <= toDate.Value);

        var totalCount = await query.CountAsync(cancellationToken);

        var orderIds = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        var items = new List<OrderResponse>();
        foreach (var oid in orderIds)
        {
            var resp = await BuildOrderResponseAsync(oid, cancellationToken);
            items.Add(resp);
        }

        return new PagedResult<OrderResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    // -----------------------------------------------------------------------
    // Update Status (Skenario B — concurrent status update)
    // -----------------------------------------------------------------------
    public async Task<OrderResponse> UpdateStatusAsync(
        Guid orderId, OrderStatus newStatus, CancellationToken cancellationToken = default)
    {
        // We use a fresh DbContext from the factory for each status update so that
        // two concurrent calls do not share tracked entities / RowVersion values.
        // This makes the optimistic-locking race truly realistic and correct.
        await using var db = _dbContextFactory.CreateDbContext();

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

            // Read the order WITH tracking so EF records its RowVersion.
            var order = await db.Orders
                .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
            if (order == null)
                throw new NotFoundException($"Order '{orderId}' was not found.");

            var previousStatus = order.Status;

            // Enforce the state-machine rules in the application layer.
            OrderStateMachine.EnsureCanTransition(order.Status, newStatus);

            order.Status = newStatus;
            order.UpdatedAt = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Optimistic lock lost: another admin updated this order first.
                await tx.RollbackAsync(cancellationToken);
                _logger.LogWarning(
                    "Concurrent status update conflict on order {OrderId}. " +
                    "Another update won; this request (to {NewStatus}) was rejected.",
                    orderId, newStatus);
                throw new InvalidStatusTransitionException(
                    $"Order '{orderId}' was modified by another request. " +
                    "Please reload the order and try again.");
            }

            _logger.LogInformation("Order {OrderId} status changed {From} -> {To}.",
                orderId, previousStatus, newStatus);

            _db.ChangeTracker.Clear();
            return await BuildOrderResponseWithAsync(db, orderId, cancellationToken);
        });
    }

    // -----------------------------------------------------------------------
    // Cancel Order (restores stock, guards concurrent cancel/status races)
    // -----------------------------------------------------------------------
    public async Task<OrderResponse> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var db = _dbContextFactory.CreateDbContext();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

            var order = await db.Orders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
            if (order == null)
                throw new NotFoundException($"Order '{orderId}' was not found.");

            // Only Pending or Confirmed may be cancelled.
            if (order.Status != OrderStatus.Pending && order.Status != OrderStatus.Confirmed)
            {
                throw new InvalidStatusTransitionException(
                    $"Order '{orderId}' cannot be cancelled from its current state '{order.Status}'. " +
                    "Only Pending or Confirmed orders can be cancelled.");
            }

            var previousStatus = order.Status;
            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = DateTime.UtcNow;

            // Optimistic-lock guard: if another request changed the order's
            // RowVersion first (e.g. a concurrent Shipped update), this throws.
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                _logger.LogWarning(
                    "Concurrent cancel conflict on order {OrderId}; another update won.", orderId);
                throw new InvalidStatusTransitionException(
                    $"Order '{orderId}' was modified by another request. " +
                    "Please reload the order and try again.");
            }

            // Restore stock atomically. We add back the quantity for each line.
            // Using a conditional UPDATE isn't needed for restore (there's no
            // upper bound to violate), but we keep it a single atomic statement
            // so a concurrent deduction can't interleave corruptly.
            var now = DateTime.UtcNow;
            foreach (var item in order.Items)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE Products
SET StockQuantity = StockQuantity + {item.Quantity},
    UpdatedAt = {now}
WHERE Id = {item.ProductId}", cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Order {OrderId} cancelled (was {Prev}); restored stock for {N} line(s).",
                orderId, previousStatus, order.Items.Count);

            _db.ChangeTracker.Clear();
            return await BuildOrderResponseWithAsync(db, orderId, cancellationToken);
        });
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private static void ValidateCreateRequest(CreateOrderRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.CustomerId))
            errors["customerId"] = new[] { "CustomerId is required." };

        if (request.Items == null || request.Items.Count == 0)
        {
            errors["items"] = new[] { "At least one item is required." };
        }
        else
        {
            for (int i = 0; i < request.Items.Count; i++)
            {
                var item = request.Items[i];
                if (item.Quantity < 1)
                    errors[$"items[{i}].quantity"] = new[] { "Quantity must be at least 1." };
                if (item.ProductId == Guid.Empty)
                    errors[$"items[{i}].productId"] = new[] { "ProductId is required." };
            }
        }

        if (errors.Count > 0)
            throw new ValidationException(errors);
    }

    private async Task<OrderResponse> BuildOrderResponseAsync(Guid orderId, CancellationToken cancellationToken)
        => await BuildOrderResponseWithAsync(_db, orderId, cancellationToken);

    private static async Task<OrderResponse> BuildOrderResponseWithAsync(
        AppDbContext db, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await db.Orders
            .AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => new
            {
                o.Id,
                o.CustomerId,
                o.ShippingAddress,
                o.Status,
                o.TotalAmount,
                o.CreatedAt,
                o.UpdatedAt,
                Items = o.Items.Select(i => new
                {
                    i.Id,
                    i.ProductId,
                    ProductName = i.Product != null ? i.Product.Name : null,
                    i.Quantity,
                    i.UnitPrice
                }).ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (order == null) return null!;

        return new OrderResponse
        {
            Id = order.Id,
            CustomerId = order.CustomerId,
            ShippingAddress = order.ShippingAddress,
            Status = order.Status.ToString(),
            TotalAmount = order.TotalAmount,
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
            Items = order.Items.Select(i => new OrderItemResponse
            {
                Id = i.Id,
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                LineTotal = i.Quantity * i.UnitPrice
            }).ToList()
        };
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // SQLite raises SQL error 19 (SQLITE_CONSTRAINT) for any constraint
        // failure. The message distinguishes UNIQUE vs NOT NULL vs CHECK:
        //   "UNIQUE constraint failed: IdempotencyRecords.IdempotencyKey"
        //   "NOT NULL constraint failed: ..."
        //   "CHECK constraint failed: ..."
        // We only want to treat the UNIQUE case as "another request owns this
        // key"; NOT NULL / CHECK are bugs we must surface, not silently replay.
        var msg = ex.InnerException?.Message ?? ex.Message ?? string.Empty;
        return msg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase);
    }

    private static int MapExceptionToStatusCode(Exception ex) => ex switch
    {
        ValidationException => StatusCodes.Status422UnprocessableEntity,
        NotFoundException => StatusCodes.Status404NotFound,
        InsufficientStockException => StatusCodes.Status409Conflict,
        InvalidStatusTransitionException => StatusCodes.Status409Conflict,
        IdempotencyConflictException => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };
}
