using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using OrderManagement.Api.Middleware;
using OrderManagement.Application.Interfaces;
using OrderManagement.Application.Services;
using OrderManagement.Infrastructure.Data;
using Serilog;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OrderManagement.Api.Extensions;

/// <summary>Service-collection extensions for DI, logging and pipeline.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQLite-backed DbContext plus an IDbContextFactory. The
    /// factory is used by status/cancel handlers so that concurrent requests
    /// get independent tracked contexts (realistic optimistic-locking races).
    /// </summary>
    public static IServiceCollection AddOrderManagementData(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("DefaultConnection")
                               ?? "Data Source=ordermanagement.db";

        // The concurrency-token interceptor is added here (at options-build time)
        // so it is present on EVERY context — both the DI-resolved DbContext and
        // the IDbContextFactory-created ones. SQLite has no native rowversion, so
        // we generate the token in app code on every Add/Update (see
        // ConcurrencyTokenInterceptor). Registering it in the options (rather
        // than only in OnConfiguring) is the only way to guarantee the factory-
        // created contexts also get it, because AddDbContextFactory builds the
        // options once and caches them.
        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseSqlite(connectionString);
            options.AddInterceptors(new ConcurrencyTokenInterceptor());
        });

        services.AddDbContextFactory<AppDbContext>(options =>
        {
            options.UseSqlite(connectionString);
            options.AddInterceptors(new ConcurrencyTokenInterceptor());
        }, ServiceLifetime.Scoped);

        return services;
    }

    /// <summary>Registers application services.</summary>
    public static IServiceCollection AddOrderManagementServices(this IServiceCollection services)
    {
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IProductService, ProductService>();
        return services;
    }

    /// <summary>Configures Serilog with console + file sinks and a correlation id enricher.</summary>
    public static IServiceCollection AddOrderManagementLogging(this IServiceCollection services)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "OrderManagement")
            .WriteTo.Console(
                outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3} {CorrelationId}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                "logs/orders-.log",
                rollingInterval: RollingInterval.Day,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        services.AddSerilog(Log.Logger, dispose: true);
        return services;
    }

    /// <summary>Adds Swagger with correlation-id description.</summary>
    public static IServiceCollection AddOrderManagementSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Order Management API",
                Version = "v1",
                Description = "Prototype Order Management service with concurrency-safe stock & status handling."
            });

            c.OperationFilter<IdempotencyHeaderOperationFilter>();
        });
        return services;
    }

    /// <summary>
    /// Configures the middleware pipeline: correlation id + global exception
    /// handling wrap everything else.
    /// </summary>
    public static WebApplication UseOrderManagementPipeline(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<GlobalExceptionMiddleware>();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseAuthorization();
        app.MapControllers();
        return app;
    }
}

/// <summary>
/// Adds the Idempotency-Key header to Swagger UI for POST /orders.
/// </summary>
public class IdempotencyHeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var isCreateOrder = context.MethodInfo.DeclaringType?.Name == "OrdersController"
                            && context.MethodInfo.Name == "CreateOrder";
        if (!isCreateOrder) return;

        operation.Parameters ??= new List<OpenApiParameter>();
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Client-generated unique key to make POST /orders idempotent. " +
                          "Reuse the same key with the same body to safely retry without creating a duplicate order.",
            Schema = new OpenApiSchema { Type = "string" }
        });
    }
}
