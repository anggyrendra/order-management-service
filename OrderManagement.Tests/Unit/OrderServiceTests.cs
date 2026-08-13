using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderManagement.Application.DTOs;
using OrderManagement.Application.Interfaces;
using OrderManagement.Application.Services;
using OrderManagement.Domain.Enums;
using OrderManagement.Domain.Exceptions;
using OrderManagement.Infrastructure.Data;
using OrderManagement.Tests.Helpers;
using Xunit;

namespace OrderManagement.Tests.Unit;

/// <summary>
/// End-to-end service-level tests that exercise the happy paths and the
/// domain validation rules (state machine, stock rules, cancel + restore,
/// pagination). These complement the concurrency tests in Skenario A/B/C by
/// verifying single-threaded correctness.
/// </summary>
public class OrderServiceTests
{
    [Fact]
    public async Task CreateOrder_deducts_stock_and_returns_full_response()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var orders = sp.GetRequiredService<IOrderService>();
        var dbFactory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();

        var req = new CreateOrderRequest
        {
            CustomerId = "CUST-7",
            ShippingAddress = "1 Main St",
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductId = TestHostFactory.ProductIds.ProductX, Quantity = 5 },
                new() { ProductId = TestHostFactory.ProductIds.ProductY, Quantity = 2 }
            }
        };

        var (resp, created) = await orders.CreateOrderAsync($"key-{Guid.NewGuid():N}", req);

        Assert.True(created);
        Assert.Equal("CUST-7", resp.CustomerId);
        Assert.Equal("1 Main St", resp.ShippingAddress);
        Assert.Equal(nameof(OrderStatus.Pending), resp.Status);
        // 5*100 + 2*25 = 550
        Assert.Equal(550m, resp.TotalAmount);
        Assert.Equal(2, resp.Items.Count);

        // Stock: X 15->10, Y 50->48
        await using var db = await dbFactory.CreateDbContextAsync();
        var x = await db.Products.SingleAsync(p => p.Id == TestHostFactory.ProductIds.ProductX);
        var y = await db.Products.SingleAsync(p => p.Id == TestHostFactory.ProductIds.ProductY);
        Assert.Equal(10, x.StockQuantity);
        Assert.Equal(48, y.StockQuantity);
    }

    [Fact]
    public async Task CreateOrder_with_insufficient_stock_is_rejected_and_stock_unchanged()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var orders = sp.GetRequiredService<IOrderService>();
        var dbFactory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();

        // Product X has 15 units; request 20.
        var req = OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductX, 20);

        await Assert.ThrowsAsync<InsufficientStockException>(
            () => orders.CreateOrderAsync($"key-{Guid.NewGuid():N}", req));

        // Stock must be unchanged (no partial deduction).
        await using var db = await dbFactory.CreateDbContextAsync();
        var x = await db.Products.SingleAsync(p => p.Id == TestHostFactory.ProductIds.ProductX);
        Assert.Equal(15, x.StockQuantity);

        // And no order row created.
        var anyOrder = await db.Orders.AnyAsync();
        Assert.False(anyOrder);
    }

    [Fact]
    public async Task CreateOrder_with_unknown_product_returns_NotFound()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var orders = sp.GetRequiredService<IOrderService>();

        var req = OrderRequestFactory.Create(Guid.NewGuid(), 1);

        await Assert.ThrowsAsync<NotFoundException>(
            () => orders.CreateOrderAsync($"key-{Guid.NewGuid():N}", req));
    }

    [Fact]
    public async Task CreateOrder_with_empty_items_is_validation_error()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var orders = sp.GetRequiredService<IOrderService>();

        var req = new CreateOrderRequest
        {
            CustomerId = "CUST-1",
            Items = new List<CreateOrderItemRequest>()
        };

        await Assert.ThrowsAsync<ValidationException>(
            () => orders.CreateOrderAsync($"key-{Guid.NewGuid():N}", req));
    }

    [Fact]
    public async Task State_machine_happy_path_Pending_Confirmed_Shipped_Delivered()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var orders = sp.GetRequiredService<IOrderService>();

        var (order, _) = await orders.CreateOrderAsync($"key-{Guid.NewGuid():N}",
            OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductY, 1));

        order = await orders.UpdateStatusAsync(order.Id, OrderStatus.Confirmed);
        Assert.Equal(nameof(OrderStatus.Confirmed), order.Status);

        order = await orders.UpdateStatusAsync(order.Id, OrderStatus.Shipped);
        Assert.Equal(nameof(OrderStatus.Shipped), order.Status);

        order = await orders.UpdateStatusAsync(order.Id, OrderStatus.Delivered);
        Assert.Equal(nameof(OrderStatus.Delivered), order.Status);

        // Terminal: cannot transition further.
        await Assert.ThrowsAsync<InvalidStatusTransitionException>(
            () => orders.UpdateStatusAsync(order.Id, OrderStatus.Cancelled));
    }

    [Fact]
    public async Task Cancel_from_Confirmed_restores_stock_atomically()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var orders = sp.GetRequiredService<IOrderService>();
        var dbFactory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();

        // Order 4 units of Product Z (stock 10 -> 6).
        var (order, _) = await orders.CreateOrderAsync($"key-{Guid.NewGuid():N}",
            OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductZ, 4));

        await orders.UpdateStatusAsync(order.Id, OrderStatus.Confirmed);

        // Cancel.
        var cancelled = await orders.CancelOrderAsync(order.Id);
        Assert.Equal(nameof(OrderStatus.Cancelled), cancelled.Status);

        // Stock restored to 10.
        await using var db = await dbFactory.CreateDbContextAsync();
        var z = await db.Products.SingleAsync(p => p.Id == TestHostFactory.ProductIds.ProductZ);
        Assert.Equal(10, z.StockQuantity);
    }

    [Fact]
    public async Task Cancel_from_Shipped_is_rejected()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var orders = sp.GetRequiredService<IOrderService>();

        var (order, _) = await orders.CreateOrderAsync($"key-{Guid.NewGuid():N}",
            OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductY, 1));

        await orders.UpdateStatusAsync(order.Id, OrderStatus.Confirmed);
        await orders.UpdateStatusAsync(order.Id, OrderStatus.Shipped);

        await Assert.ThrowsAsync<InvalidStatusTransitionException>(
            () => orders.CancelOrderAsync(order.Id));
    }

    [Fact]
    public async Task GetOrder_returns_null_for_unknown_id()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var orders = sp.GetRequiredService<IOrderService>();

        var result = await orders.GetOrderAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task ListOrders_filters_by_status_and_paginates()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var orders = sp.GetRequiredService<IOrderService>();

        // Create 3 orders.
        for (int i = 0; i < 3; i++)
        {
            await orders.CreateOrderAsync($"key-list-{i}-{Guid.NewGuid():N}",
                OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductY, 1, $"CUST-{i}"));
        }

        // Confirm one of them.
        var allPending = await orders.ListOrdersAsync(status: OrderStatus.Pending);
        var firstId = allPending.Items.First().Id;
        await orders.UpdateStatusAsync(firstId, OrderStatus.Confirmed);

        // Filter by status.
        var pending = await orders.ListOrdersAsync(status: OrderStatus.Pending);
        Assert.Equal(2, pending.Items.Count);
        Assert.All(pending.Items, o => Assert.Equal(nameof(OrderStatus.Pending), o.Status));

        var confirmed = await orders.ListOrdersAsync(status: OrderStatus.Confirmed);
        Assert.Single(confirmed.Items);
        Assert.All(confirmed.Items, o => Assert.Equal(nameof(OrderStatus.Confirmed), o.Status));

        // Pagination.
        var page1 = await orders.ListOrdersAsync(page: 1, pageSize: 2);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(3, page1.TotalCount);
        Assert.True(page1.HasNext);

        var page2 = await orders.ListOrdersAsync(page: 2, pageSize: 2);
        Assert.Single(page2.Items);
        Assert.False(page2.HasNext);
    }

    [Fact]
    public async Task ListOrders_filters_by_customerId()
    {
        await using var sp = await TestHostFactory.BuildAsync();
        var orders = sp.GetRequiredService<IOrderService>();

        await orders.CreateOrderAsync($"k1-{Guid.NewGuid():N}",
            OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductY, 1, "ALPHA"));
        await orders.CreateOrderAsync($"k2-{Guid.NewGuid():N}",
            OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductY, 1, "BETA"));
        await orders.CreateOrderAsync($"k3-{Guid.NewGuid():N}",
            OrderRequestFactory.Create(TestHostFactory.ProductIds.ProductY, 1, "ALPHA"));

        var alpha = await orders.ListOrdersAsync(customerId: "ALPHA");
        Assert.Equal(2, alpha.Items.Count);
        Assert.All(alpha.Items, o => Assert.Equal("ALPHA", o.CustomerId));

        var beta = await orders.ListOrdersAsync(customerId: "BETA");
        Assert.Single(beta.Items);
    }
}
