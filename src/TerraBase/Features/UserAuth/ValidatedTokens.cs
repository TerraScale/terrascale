using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace TerraBase.Features.UserAuth;

/// <summary>
/// Access tokens <see cref="UserAuthenticationHandler"/> already accepted, so a client's every request does not verify
/// the same signature again: verifying was 12 % of the write path's CPU (benchmark/bench/2026-10-12-write-path-cpu.md).
/// An entry is what the handler derived from the token alone, keyed by the token's SHA-256 (never the token). It answers
/// only while the issuer keys it was checked against are still the current list, so a refresh that withdraws a key drops
/// every entry checked with it, and only strictly before the token's expiration, as validation would.
/// Configuration (issuer, audiences) is fixed for the process, so the rest of validation cannot change its answer.
/// </summary>
internal sealed class ValidatedTokens([FromKeyedServices(UserAuthenticationHandler.SchemeName)] TimeProvider? time = null)
{
    // About 1 KiB per entry: 4 MiB at most, then the whole cache starts over.
    private const int Capacity = 4096;

    private readonly ConcurrentDictionary<string, Accepted> _entries = new(StringComparer.Ordinal);

    /// <summary>What the handler accepted: its claims and project-token fields, the keys it checked, exp, and an API key's id and key_gen.</summary>
    internal sealed record Accepted(
        IReadOnlyList<SecurityKey> Keys, DateTime ValidTo, IReadOnlyList<(string Type, string Value)> Claims,
        string? Project, string? Database, string? Scope, IReadOnlyList<IPNetwork[]> Allowlists, (string Id, string Generation)? Key);

    public static string KeyOf(string token)
    {
        // Bound stack use; oversized credentials use scratch that is cleared before returning it.
        var length = Encoding.UTF8.GetByteCount(token);
        byte[]? pooled = null;
        Span<byte> bytes = length <= 2048 ? stackalloc byte[length] : (pooled = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            _ = Encoding.UTF8.GetBytes(token, bytes);
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            _ = SHA256.HashData(bytes, digest);
            return Convert.ToBase64String(digest);
        }
        finally
        {
            if (pooled is null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
            else
            {
                ArrayPool<byte>.Shared.Return(pooled, clearArray: true);
            }
        }
    }

    public bool TryGet(string key, IReadOnlyList<SecurityKey> keys, out Accepted accepted)
    {
        if (_entries.TryGetValue(key, out var found) && ReferenceEquals(found.Keys, keys)
            && (time ?? TimeProvider.System).GetUtcNow().UtcDateTime < found.ValidTo)
        {
            accepted = found;
            return true;
        }

        accepted = null!;
        return false;
    }

    public void Add(string key, Accepted accepted)
    {
        if (_entries.Count >= Capacity)
        {
            _entries.Clear();
        }

        _entries[key] = accepted;
    }
}
