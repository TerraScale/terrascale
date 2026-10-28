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

    // Ordinary tokens encode on the stack; a longer one (or one that expands) takes the allocating path. Same bytes either way.
    private const int StackBytes = 1024;

    public static string KeyOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> utf8 = stackalloc byte[StackBytes];
        if (Encoding.UTF8.TryGetBytes(token, utf8, out var written))
        {
            SHA256.HashData(utf8[..written], hash);
        }
        else
        {
            SHA256.HashData(Encoding.UTF8.GetBytes(token), hash);
        }

        return Convert.ToBase64String(hash);
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
