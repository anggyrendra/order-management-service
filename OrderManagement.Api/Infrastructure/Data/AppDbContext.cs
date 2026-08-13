using Microsoft.EntityFrameworkCore;
using OrderManagement.Domain.Entities;

namespace OrderManagement.Infrastructure.Data;

/// <summary>
/// EF Core DbContext for the Order Management prototype.
///
/// Concurrency strategy notes (see README for full justification):
///  - Product.RowVersion and Order.RowVersion are application-managed
///    optimistic concurrency tokens (IsConcurrencyToken + a SaveChanges
///    interceptor that regenerates the byte[] on every write). SQLite has no
///    native rowversion type, so we manage the token in app code; this is
///    portable to SQL Server/PostgreSQL where a native token could replace it.
///  - IdempotencyRecord.IdempotencyKey has a UNIQUE index (it is the primary key)
///    so that two concurrent inserts with the same key cannot both succeed.
///  - A CHECK constraint on Products(StockQuantity >= 0) is added via raw SQL in
///    OnModelCreating to provide a final hard guarantee that stock can never be
///    negative, complementing the atomic conditional UPDATE.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    /// <summary>
    /// Registers the application-managed concurrency-token interceptor so that
    /// every Add/Update of an <see cref="IConcurrencyToken"/>/> entity gets a
    /// fresh RowVersion. This is added here (rather than only at DI time) as a
    /// safety net so it also applies to contexts created by
    /// <c>IDbContextFactory</c> and the design-time factory even if the caller
    /// forgot to register it in the options. EF Core merges interceptors from
    /// <c>OnConfiguring</c> with those from the options, so registering it both
    /// places is harmless.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(new ConcurrencyTokenInterceptor());
        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ---- Product ----
        modelBuilder.Entity<Product>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Name).IsRequired().HasMaxLength(200);
            b.Property(p => p.StockQuantity).IsRequired();
            b.Property(p => p.Price).HasColumnType("decimal(18,2)");
            b.Property(p => p.RowVersion)
             .IsConcurrencyToken() // application-managed (see ConcurrencyTokenInterceptor); portable to SQL Server/Postgres
             .HasColumnName("RowVersion");
            b.Property(p => p.CreatedAt).IsRequired();
            b.Property(p => p.UpdatedAt).IsRequired();

            b.HasIndex(p => p.Name);
        });

        // ---- Order ----
        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(o => o.Id);
            b.Property(o => o.CustomerId).IsRequired().HasMaxLength(100);
            b.Property(o => o.ShippingAddress).HasMaxLength(500);
            b.Property(o => o.Status).HasConversion<int>().IsRequired();
            b.Property(o => o.TotalAmount).HasColumnType("decimal(18,2)");
            b.Property(o => o.RowVersion)
             .IsConcurrencyToken() // application-managed (see ConcurrencyTokenInterceptor); portable to SQL Server/Postgres
             .HasColumnName("RowVersion");
            b.Property(o => o.CreatedAt).IsRequired();
            b.Property(o => o.UpdatedAt).IsRequired();

            b.HasIndex(o => o.CustomerId);
            b.HasIndex(o => o.Status);
            b.HasIndex(o => o.CreatedAt);
            // Composite index supports the list filter (status, customerId, date) efficiently.
            b.HasIndex(o => new { o.Status, o.CustomerId, o.CreatedAt });
        });

        // ---- OrderItem ----
        modelBuilder.Entity<OrderItem>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.Quantity).IsRequired();
            b.Property(i => i.UnitPrice).HasColumnType("decimal(18,2)");
            b.HasOne(i => i.Order)
             .WithMany(o => o.Items)
             .HasForeignKey(i => i.OrderId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(i => i.Product)
             .WithMany()
             .HasForeignKey(i => i.ProductId)
             .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(i => i.OrderId);
            b.HasIndex(i => i.ProductId);
        });

        // ---- IdempotencyRecord ----
        // The IdempotencyKey is the primary key, which gives us the UNIQUE guarantee
        // required to make Skenario C (concurrent identical create) safe: only one
        // INSERT can win, the other gets a unique-constraint violation.
        modelBuilder.Entity<IdempotencyRecord>(b =>
        {
            b.HasKey(r => r.IdempotencyKey);
            b.Property(r => r.IdempotencyKey).IsRequired().HasMaxLength(255);
            b.Property(r => r.RequestHash).IsRequired().HasMaxLength(64);
            b.Property(r => r.RequestPath).IsRequired().HasMaxLength(50);
            b.Property(r => r.Status).HasConversion<int>().IsRequired();
            b.Property(r => r.ResponseBody);
            b.Property(r => r.OrderId);
            b.Property(r => r.CreatedAt).IsRequired();
            b.Property(r => r.CompletedAt).IsRequired();
            b.Property(r => r.RowVersion).IsConcurrencyToken().HasColumnName("RowVersion");
        });

        // Hard DB-level guarantee: stock can never be negative.
        // This is a backstop on top of the atomic conditional UPDATE; even if a
        // bug bypassed the application logic, the database would reject the write.
        modelBuilder.Entity<Product>()
                    .ToTable(tb => tb.HasCheckConstraint("CK_Product_StockQuantity_NonNegative", "[StockQuantity] >= 0"));
    }
}
