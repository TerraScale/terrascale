using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using NUlid.Rng;

namespace TerraBase.Primitives;

/// <summary>Canonical entity identifiers, with library-provided monotonic randomness per application clock.</summary>
internal static class EntityIds
{
    private static readonly ConditionalWeakTable<TimeProvider, MonotonicUlidRng> Randomness = [];

    public static Ulid New(TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var random = Randomness.GetValue(clock, static _ => new MonotonicUlidRng());
        lock (random)
        {
            var now = clock.GetUtcNow();
            return Ulid.NewUlid(now, random.GetRandomBytes(now));
        }
    }

    // Crockford base32 as ULIDs print it: uppercase, without I, L, O and U.
    private static readonly SearchValues<char> Canonical = SearchValues.Create("0123456789ABCDEFGHJKMNPQRSTVWXYZ");

    /// <summary>
    /// Whether <paramref name="id"/> is a ULID as it prints: exactly what parsing and printing it back yields, checked without
    /// allocating (it runs on every request).
    /// </summary>
    public static bool IsValid([NotNullWhen(true)] string? id) =>
        id is { Length: 26 } && id[0] is >= '0' and <= '7' && !id.AsSpan().ContainsAnyExcept(Canonical);
}
