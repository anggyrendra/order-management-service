using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderManagement.Infrastructure.Data;

/// <summary>
/// Design-time factory so `dotnet ef migrations add` can construct the DbContext
/// without running the application's dependency-injection pipeline.
/// Uses SQLite by default (matches production config for this prototype).
/// </summary>
public class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=ordermanagement_design.db")
            .Options;

        return new AppDbContext(options);
    }
}
