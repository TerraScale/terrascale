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

    public static bool IsValid([NotNullWhen(true)] string? id) =>
        id is { Length: 26 } && id[0] is >= '0' and <= '7'
        && Ulid.TryParse(id, out var parsed) && parsed.ToString() == id;
}
