using Microsoft.AspNetCore.Mvc;
using OrderManagement.Application.DTOs;
using OrderManagement.Application.Interfaces;
using OrderManagement.Domain.Enums;
using OrderManagement.Domain.Exceptions;

namespace OrderManagement.Api.Controllers;

/// <summary>
/// Order Management endpoints. All concurrency/idempotency concerns are handled
/// in the service layer; controllers stay thin.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orders;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(IOrderService orders, ILogger<OrdersController> logger)
    {
        _orders = orders;
        _logger = logger;
    }

    /// <summary>
    /// Create a new order. Requires an Idempotency-Key header to prevent double
    /// orders from retries / double-clicks. Stock is deducted atomically; if any
    /// product has insufficient stock the whole request is rejected (409).
    /// </summary>
    /// <returns>201 Created with the order, or 200 OK + the cached order if the
    /// idempotency key was already used with the same payload.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateOrder(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // Model binding can't enforce headers; surface a clean 400 here.
            throw new ValidationException("Idempotency-Key", "The 'Idempotency-Key' header is required for POST /orders.");
        }

        var (response, wasCreated) = await _orders.CreateOrderAsync(idempotencyKey, request, cancellationToken);
        return wasCreated
            ? CreatedAtAction(nameof(GetOrder), new { id = response.Id }, response)
            : Ok(response);
    }

    /// <summary>Get a single order by id, including its line items.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrder([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var order = await _orders.GetOrderAsync(id, cancellationToken);
        if (order == null) return NotFound();
        return Ok(order);
    }

    /// <summary>
    /// List orders with optional filters and pagination.
    /// Query params: status, customerId, fromDate, toDate, page (default 1), pageSize (default 20, max 100).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<OrderResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListOrders(
        [FromQuery] OrderStatus? status,
        [FromQuery] string? customerId,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _orders.ListOrdersAsync(status, customerId, fromDate, toDate, page, pageSize, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Update the status of an order. Only valid state-machine transitions are
    /// allowed; concurrent updates are resolved by optimistic locking (last
    /// writer wins, others get 409).
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateStatus(
        [FromRoute] Guid id,
        [FromBody] UpdateOrderStatusRequest request,
        CancellationToken cancellationToken)
    {
        var order = await _orders.UpdateStatusAsync(id, request.Status, cancellationToken);
        return Ok(order);
    }

    /// <summary>
    /// Cancel an order. Only allowed from Pending or Confirmed. Restores stock
    /// atomically. Concurrent cancel vs. status update is guarded by optimistic locking.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelOrder([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var order = await _orders.CancelOrderAsync(id, cancellationToken);
        return Ok(order);
    }
}
