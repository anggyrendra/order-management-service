using System.Collections.ObjectModel;

namespace OrderManagement.Domain.Exceptions;

/// <summary>
/// Base class for all domain-level business rule violations.
/// Allows the exception middleware to map them to the correct HTTP status code.
/// </summary>
public abstract class DomainException : Exception
{
    public string ErrorCode { get; }

    protected DomainException(string message, string errorCode) : base(message)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Thrown when a resource (order, product) cannot be found.
/// Maps to HTTP 404 Not Found.
/// </summary>
public class NotFoundException : DomainException
{
    public NotFoundException(string message) : base(message, "NOT_FOUND") { }
}

/// <summary>
/// Thrown when an order status transition is invalid per the state machine.
/// Maps to HTTP 409 Conflict.
/// </summary>
public class InvalidStatusTransitionException : DomainException
{
    public InvalidStatusTransitionException(string message) : base(message, "INVALID_STATUS_TRANSITION") { }
}

/// <summary>
/// Thrown when there is not enough stock to fulfil an order line.
/// Maps to HTTP 409 Conflict (business rule conflict, not a 422 validation error,
/// because the request is structurally valid; it conflicts with current stock state).
/// </summary>
public class InsufficientStockException : DomainException
{
    public string ProductId { get; }
    public int Requested { get; }
    public int Available { get; }

    public InsufficientStockException(string productId, int requested, int available)
        : base($"Insufficient stock for product '{productId}': requested {requested}, available {available}.", "INSUFFICIENT_STOCK")
    {
        ProductId = productId;
        Requested = requested;
        Available = available;
    }
}

/// <summary>
/// Thrown when a duplicate Idempotency-Key is detected for a different payload,
/// or when the idempotent record is in an inconsistent state.
/// Maps to HTTP 409 Conflict.
/// </summary>
public class IdempotencyConflictException : DomainException
{
    public IdempotencyConflictException(string message) : base(message, "IDEMPOTENCY_CONFLICT") { }
}

/// <summary>
/// Thrown when the request payload fails domain validation beyond simple model binding
/// (e.g. empty items list, non-positive quantity). Maps to HTTP 422 Unprocessable Entity.
/// </summary>
public class ValidationException : DomainException
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationException(IDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.", "VALIDATION_ERROR")
    {
        Errors = new ReadOnlyDictionary<string, string[]>(errors);
    }

    public ValidationException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = new[] { error } }) { }
}
