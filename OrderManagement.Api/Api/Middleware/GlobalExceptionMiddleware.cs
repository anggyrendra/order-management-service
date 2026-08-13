using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderManagement.Domain.Exceptions;

namespace OrderManagement.Api.Middleware;

/// <summary>
/// Global exception handler that maps every exception type to a consistent
/// JSON error envelope and the correct HTTP status code. Keeps controllers thin
/// and guarantees uniform error formatting across all endpoints.
///
/// Envelope shape:
/// {
///   "correlationId": "...",
///   "errorCode": "INSUFFICIENT_STOCK",
///   "message": "...",
///   "details": { ... },     // present only for validation errors
///   "timestamp": "..."
/// }
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception ex)
    {
        var correlationId = context.Items[CorrelationIdMiddleware.LogPropertyName]?.ToString()
                            ?? Guid.NewGuid().ToString("N");

        var (status, code, message, details) = MapException(ex);

        // Log at the appropriate level: client errors as warnings, server errors as errors.
        if (status >= 500)
            _logger.LogError(ex, "Unhandled exception. CorrelationId={CorrelationId} Path={Path}",
                correlationId, context.Request.Path);
        else
            _logger.LogWarning(ex, "Request failed with {Status} {Code}. CorrelationId={CorrelationId} Path={Path}",
                status, code, correlationId, context.Request.Path);

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";

        var body = new ErrorResponse
        {
            CorrelationId = correlationId,
            ErrorCode = code,
            Message = message,
            Details = details,
            Timestamp = DateTime.UtcNow
        };

        await JsonSerializer.SerializeAsync(context.Response.Body, body, _jsonOptions);
    }

    private static (int status, string code, string message, object? details) MapException(Exception ex) => ex switch
    {
        ValidationException ve => (
            StatusCodes.Status422UnprocessableEntity,
            ve.ErrorCode,
            ve.Message,
            ve.Errors.ToDictionary(kv => kv.Key, kv => kv.Value)),

        NotFoundException nf => (
            StatusCodes.Status404NotFound,
            nf.ErrorCode,
            nf.Message,
            null),

        InsufficientStockException ise => (
            StatusCodes.Status409Conflict,
            ise.ErrorCode,
            ise.Message,
            new { productId = ise.ProductId, requested = ise.Requested, available = ise.Available }),

        InvalidStatusTransitionException ist => (
            StatusCodes.Status409Conflict,
            ist.ErrorCode,
            ist.Message,
            null),

        IdempotencyConflictException ic => (
            StatusCodes.Status409Conflict,
            ic.ErrorCode,
            ic.Message,
            null),

        DbUpdateConcurrencyException => (
            StatusCodes.Status409Conflict,
            "CONCURRENCY_CONFLICT",
            "The resource was modified by another request. Please reload and retry.",
            null),

        DbUpdateException due when IsUniqueConstraintViolation(due) => (
            StatusCodes.Status409Conflict,
            "DUPLICATE_REQUEST",
            "A request with the same key is already being processed or has been completed.",
            null),

        OperationCanceledException => (
            StatusCodes.Status499ClientClosedRequest,
            "REQUEST_CANCELLED",
            "The request was cancelled.",
            null),

        _ => (
            StatusCodes.Status500InternalServerError,
            "INTERNAL_ERROR",
            "An unexpected error occurred. Please contact support with the correlation id.",
            null)
    };

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message ?? string.Empty;
        return msg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("constraint failed", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ErrorResponse
    {
        public string CorrelationId { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public object? Details { get; set; }
        public DateTime Timestamp { get; set; }
    }
}

/// <summary>Custom status code for client-closed requests (NGINX convention).</summary>
internal static class StatusCodes
{
    public const int Status422UnprocessableEntity = 422;
    public const int Status404NotFound = 404;
    public const int Status409Conflict = 409;
    public const int Status500InternalServerError = 500;
    public const int Status201Created = 201;
    public const int Status499ClientClosedRequest = 499;
}
