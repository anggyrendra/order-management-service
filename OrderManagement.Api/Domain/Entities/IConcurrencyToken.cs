namespace OrderManagement.Domain.Entities;

/// <summary>
/// Marker interface for entities that carry an application-managed optimistic
/// concurrency token (<see cref="RowVersion"/>). The <c>RowVersion</c> is a
/// <c>byte[]</c> regenerated on every write by
/// <c>ConcurrencyTokenInterceptor</c>, so a concurrent update changes the token
/// and causes the loser's <c>SaveChanges</c> to throw
/// <c>DbUpdateConcurrencyException</c>.
/// </summary>
public interface IConcurrencyToken
{
    byte[] RowVersion { get; set; }
}
