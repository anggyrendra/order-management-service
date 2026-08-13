using OrderManagement.Application.DTOs;
using OrderManagement.Domain.Enums;
using OrderManagement.Domain.Exceptions;

namespace OrderManagement.Application.Interfaces;

/// <summary>
/// Order management service. All methods that mutate state are concurrency-safe
/// (see implementation and README). Each method is documented with the race
/// conditions it guards against.
/// </summary>
public interface IOrderService
{
    /// <summary>
    /// Creates an order atomically with stock deduction and idempotency.
    /// Guards: Skenario A (concurrent stock deduction), Skenario C (idempotent
    /// create under race), double-submit.
    /// </summary>
    /// <param name="idempotencyKey">Client-supplied idempotency key (required).</param>
    /// <param name="request">The order payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created (or previously-created) order plus a flag indicating
    /// whether this call actually created it vs. returned the cached idempotent result.</returns>
    Task<(OrderResponse Response, bool WasCreated)> CreateOrderAsync(
        string idempotencyKey,
        CreateOrderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Fetch a single order by id, including its items.</summary>
    Task<OrderResponse?> GetOrderAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// List orders with optional filters and pagination.
    /// </summary>
    Task<PagedResult<OrderResponse>> ListOrdersAsync(
        OrderStatus? status = null,
        string? customerId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transition an order to a new status. Guards Skenario B (concurrent status
    /// update) via optimistic locking on the order's RowVersion.
    /// </summary>
    Task<OrderResponse> UpdateStatusAsync(Guid orderId, OrderStatus newStatus, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel an order. Only allowed from Pending or Confirmed. Restores stock
    /// atomically. Guards against concurrent cancel/status-update races via
    /// optimistic locking.
    /// </summary>
    Task<OrderResponse> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Simple product/inventory service. Kept minimal because the brief says a
/// separate inventory service is not required for the prototype.
/// </summary>
public interface IProductService
{
    Task EnsureProductsSeededAsync(CancellationToken cancellationToken = default);
    Task<ProductResponse?> GetProductAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<ProductResponse>> ListProductsAsync(CancellationToken cancellationToken = default);
}

public class ProductResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int StockQuantity { get; set; }
    public decimal Price { get; set; }
}
