using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrderManagement.Infrastructure.Idempotency;

/// <summary>
/// Computes a deterministic SHA-256 hash of the request payload so we can detect
/// when a client reuses an Idempotency-Key with a *different* body (which is a
/// client bug and should be rejected with 409, not silently served from cache).
/// </summary>
public static class RequestHasher
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Returns a hex-encoded SHA-256 of the canonicalised payload.
    /// Canonicalisation uses camelCase JSON so the hash is stable regardless of
    /// property declaration order in the DTO.
    /// </summary>
    public static string Hash<T>(T payload)
    {
        var json = payload is string s
            ? s
            : JsonSerializer.Serialize(payload, _jsonOptions);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Hash a raw string/byte payload.</summary>
    public static string HashRaw(string payload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
