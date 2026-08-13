using OrderManagement.Domain;
using OrderManagement.Domain.Enums;
using OrderManagement.Domain.Exceptions;
using Xunit;

namespace OrderManagement.Tests.Unit;

/// <summary>
/// Unit tests for the order state machine. Pure logic, no DB — fast and deterministic.
/// </summary>
public class OrderStateMachineTests
{
    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Confirmed, true)]
    [InlineData(OrderStatus.Pending, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Shipped, true)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Delivered, true)]
    public void Valid_transitions_are_allowed(OrderStatus from, OrderStatus to, bool expected)
    {
        Assert.Equal(expected, OrderStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Shipped)]
    [InlineData(OrderStatus.Pending, OrderStatus.Delivered)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Delivered)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Pending)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Shipped, OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Delivered, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Delivered, OrderStatus.Shipped)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Pending)]
    public void Invalid_transitions_are_rejected(OrderStatus from, OrderStatus to)
    {
        Assert.False(OrderStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidStatusTransitionException>(
            () => OrderStateMachine.EnsureCanTransition(from, to));
    }

    [Fact]
    public void Terminal_states_are_terminal()
    {
        Assert.True(OrderStateMachine.IsTerminal(OrderStatus.Delivered));
        Assert.True(OrderStateMachine.IsTerminal(OrderStatus.Cancelled));
        Assert.False(OrderStateMachine.IsTerminal(OrderStatus.Pending));
        Assert.False(OrderStateMachine.IsTerminal(OrderStatus.Confirmed));
        Assert.False(OrderStateMachine.IsTerminal(OrderStatus.Shipped));
    }

    [Fact]
    public void Transition_from_terminal_throws_terminal_message()
    {
        var ex = Assert.Throws<InvalidStatusTransitionException>(
            () => OrderStateMachine.EnsureCanTransition(OrderStatus.Delivered, OrderStatus.Cancelled));
        Assert.Contains("terminal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
