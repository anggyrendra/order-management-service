using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderManagement.Domain.Entities;

namespace OrderManagement.Infrastructure.Data;

/// <summary>
/// Application-managed optimistic concurrency token generator for SQLite.
///
/// SQLite has no native auto-updating <c>rowversion</c>/<c>xmin</c> type, so
/// per the EF Core docs we manage the token in application code. This interceptor
/// assigns a fresh <see cref="IConcurrencyToken.RowVersion"/> (a random
/// 16-byte value) on every INSERT and UPDATE of an entity that implements
/// <see cref="IConcurrencyToken"/> and is configured with
/// <c>IsConcurrencyToken()</c>.
///
/// Because the token changes on every write, EF Core's UPDATE statement
/// (which includes <c>WHERE ... AND RowVersion = @original</c>) will affect
/// zero rows if another request modified the row in the meantime, causing EF
/// to throw <see cref="DbUpdateConcurrencyException"/> — exactly the
/// Skenario B guard we need.
///
/// On SQL Server / PostgreSQL this interceptor is harmless (the value is
/// simply overwritten by the provider's native rowversion/xmin), so the same
/// code is portable across providers.
/// </summary>
public sealed class ConcurrencyTokenInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        AssignTokens(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        AssignTokens(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void AssignTokens(DbContext? context)
    {
        if (context is null) return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
                continue;

            if (entry.Entity is IConcurrencyToken tracked)
            {
                tracked.RowVersion = Guid.NewGuid().ToByteArray();
            }
        }
    }
}
