using Serilog.Context;

namespace OrderManagement.Api.Middleware;

/// <summary>
/// Assigns (or honours an incoming) X-Correlation-Id and pushes it into the
/// Serilog log context so every log line for a request carries the same id.
/// This is the "every request ideally has a correlation ID that can be traced
/// across logs" requirement.
/// </summary>
public class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    public const string LogPropertyName = "CorrelationId";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var incoming) && !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()
            : Guid.NewGuid().ToString("N");

        // Store on the HttpContext for downstream code (e.g. error responses).
        context.Items[LogPropertyName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty(LogPropertyName, correlationId))
        {
            await _next(context);
        }
    }
}
