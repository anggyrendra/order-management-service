using Microsoft.EntityFrameworkCore;
using OrderManagement.Api.Extensions;
using OrderManagement.Application.Interfaces;
using OrderManagement.Infrastructure.Data;
using Serilog;

// Bootstrap Serilog first so startup errors are logged.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // --- Services ---
    builder.Host.UseSerilog();
    builder.Services.AddOrderManagementLogging();
    builder.Services.AddOrderManagementData(builder.Configuration);
    builder.Services.AddOrderManagementServices();
    builder.Services.AddOrderManagementSwagger();

    builder.Services.AddControllers();

    var app = builder.Build();

    // --- Ensure database + schema exist (prototype convenience) ---
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();

        // Seed products so the API is usable immediately.
        var productService = scope.ServiceProvider.GetRequiredService<IProductService>();
        await productService.EnsureProductsSeededAsync();
    }

    // --- Pipeline ---
    app.UseOrderManagementPipeline();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
