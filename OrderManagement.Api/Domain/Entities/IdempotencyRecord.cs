using System.ComponentModel.DataAnnotations;

namespace OrderManagement.Domain.Entities;

/// <summary>
/// Stores the result of an idempotent request keyed by the client-supplied
/// Idempotency-Key. The combination of a UNIQUE index on the key plus an
/// INSERT-then-process pattern guarantees that two identical concurrent requests
/// (Skenario C) produce exactly one order.
///
/// The row is written BEFORE the business logic runs and within the same
/// transaction. If a second request with the same key tries to INSERT, the
/// UNIQUE constraint will reject it, and the loser then reads the winner's result.
/// </summary>
public class IdempotencyRecord : IConcurrencyToken
{
    /// <summary>The client-supplied Idempotency-Key (unique).</summary>
    [MaxLength(255)]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 hash of the request payload. Used to detect key reuse with a
    /// different body (which is a client bug / 409 conflict).
    /// </summary>
    [MaxLength(64)]
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>The HTTP method/path this key was first used with.</summary>
    [MaxLength(50)]
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>
    /// Status of processing: Pending (in-flight), Completed, Failed.
    /// Stored as a separate column rather than inferred from response so that
    /// an in-flight key can be detected even before a response exists.
    /// </summary>
    public IdempotencyStatus Status { get; set; } = IdempotencyStatus.Pending;

    public int ResponseStatusCode { get; set; }

    /// <summary>Serialized response body (the created order, or error).</summary>
    public string? ResponseBody { get; set; }

    /// <summary>The id of the order that was created (if any), for fast lookup.</summary>
    public Guid? OrderId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime CompletedAt { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Prevents a race where two writers try to
    /// flip the same Pending record to Completed simultaneously.
    /// </summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public enum IdempotencyStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2
}
