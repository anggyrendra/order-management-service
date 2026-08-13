using OrderManagement.Domain.Enums;
using OrderManagement.Domain.Exceptions;

namespace OrderManagement.Domain;

/// <summary>
/// Encapsulates the valid status transitions for an order, separate from the
/// Order entity itself so the rules are testable in isolation and reusable.
///
/// Valid transitions:
///   Pending    -> Confirmed | Cancelled
///   Confirmed  -> Shipped   | Cancelled
///   Shipped    -> Delivered
///   Delivered  -> (terminal, no further changes)
///   Cancelled  -> (terminal, no further changes)
/// </summary>
public static class OrderStateMachine
{
    private static readonly IReadOnlyDictionary<OrderStatus, IReadOnlySet<OrderStatus>> _transitions =
        new Dictionary<OrderStatus, IReadOnlySet<OrderStatus>>
        {
            [OrderStatus.Pending] = new HashSet<OrderStatus> { OrderStatus.Confirmed, OrderStatus.Cancelled },
            [OrderStatus.Confirmed] = new HashSet<OrderStatus> { OrderStatus.Shipped, OrderStatus.Cancelled },
            [OrderStatus.Shipped] = new HashSet<OrderStatus> { OrderStatus.Delivered },
            [OrderStatus.Delivered] = new HashSet<OrderStatus>(),
            [OrderStatus.Cancelled] = new HashSet<OrderStatus>(),
        };

    /// <summary>True if transitioning from <paramref name="from"/> to <paramref name="to"/> is allowed.</summary>
    public static bool CanTransition(OrderStatus from, OrderStatus to)
    {
        return _transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    /// <summary>
    /// Throws <see cref="InvalidStatusTransitionException"/> if the transition is not allowed.
    /// Centralises the error message so it is consistent everywhere.
    /// </summary>
    public static void EnsureCanTransition(OrderStatus from, OrderStatus to)
    {
        if (CanTransition(from, to))
            return;

        if (from == OrderStatus.Delivered || from == OrderStatus.Cancelled)
            throw new InvalidStatusTransitionException(
                $"Order is in terminal state '{from}' and cannot be changed to '{to}'.");

        throw new InvalidStatusTransitionException(
            $"Invalid status transition from '{from}' to '{to}'.");
    }

    /// <summary>True if the status is a terminal (no further transitions possible).</summary>
    public static bool IsTerminal(OrderStatus status) =>
        status == OrderStatus.Delivered || status == OrderStatus.Cancelled;
}
