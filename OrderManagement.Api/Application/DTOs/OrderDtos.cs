using System.ComponentModel.DataAnnotations;
using OrderManagement.Domain.Enums;

namespace OrderManagement.Application.DTOs;

/// <summary>Input item for creating an order.</summary>
public class CreateOrderItemRequest
{
    [Required]
    public Guid ProductId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Quantity must be at least 1.")]
    public int Quantity { get; set; }
}

/// <summary>Request body for POST /orders.</summary>
public class CreateOrderRequest
{
    [Required]
    [MaxLength(100)]
    public string CustomerId { get; set; } = string.Empty;

    [Required]
    [MinLength(1, ErrorMessage = "At least one item is required.")]
    public List<CreateOrderItemRequest> Items { get; set; } = new();

    [MaxLength(500)]
    public string ShippingAddress { get; set; } = string.Empty;
}

/// <summary>Request body for PATCH/PUT to update order status.</summary>
public class UpdateOrderStatusRequest
{
    [Required]
    [EnumDataType(typeof(OrderStatus), ErrorMessage = "Invalid order status.")]
    public OrderStatus Status { get; set; }
}

/// <summary>Single order line in a response.</summary>
public class OrderItemResponse
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string? ProductName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}

/// <summary>Full order detail returned by GET /orders/{id} and POST /orders.</summary>
public class OrderResponse
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public string ShippingAddress { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<OrderItemResponse> Items { get; set; } = new();
}

/// <summary>One page of the order list with pagination metadata.</summary>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasNext => Page < TotalPages;
    public bool HasPrevious => Page > 1;
}
