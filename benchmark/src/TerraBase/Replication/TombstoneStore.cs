using System.Collections.ObjectModel;

namespace TerraBase.Replication;

/// <summary>A remove event that keeps an old document value from being resurrected.</summary>
public sealed record DocumentTombstone(
    DocumentKey Key,
    ReplicationDot Dot,
    IReadOnlyDictionary<string, ulong> Context,
    long Hlc,
    DateTimeOffset CreatedAt,
    string? Incarnation = null)
{
    public CausalVersion Version => new(Dot, Context);

    public DocumentTombstone Normalize()
    {
        var context = CausalVersion.CopyContext(Context);
        CausalVersion.Advance(context, Dot);
        return this with { Context = new ReadOnlyDictionary<string, ulong>(context) };
    }
}

/// <summary>
/// Tracks document remove events independently of the materialized document
/// table. A tombstone is retained until every selected replica has acknowledged
/// its dot, preventing a late body from recreating the deleted document.
/// </summary>
public sealed class TombstoneStore
{
    private readonly Dictionary<DocumentKey, DocumentTombstone> tombstones = new();
    private readonly object gate = new();

    /// <summary>Records the remove event, joining with an existing concurrent event.</summary>
    public bool Apply(DocumentTombstone tombstone)
    {
        ArgumentNullException.ThrowIfNull(tombstone);
        var normalized = tombstone.Normalize();
        lock (gate)
        {
            if (!tombstones.TryGetValue(normalized.Key, out var current))
            {
                tombstones[normalized.Key] = normalized;
                return true;
            }

            if (CausalVersion.Contains(current.Context, normalized.Dot))
                return false;
            if (CausalVersion.Contains(normalized.Context, current.Dot))
            {
                tombstones[normalized.Key] = normalized;
                return true;
            }

            // Concurrent delete events represent the same remove-wins policy;
            // retain the deterministic winner while preserving both contexts.
            var left = ToRecord(current);
            var right = ToRecord(normalized);
            var merged = ReplicationLattice.Merge(left, right, RegisterMergePolicy.RemoveWins);
            tombstones[normalized.Key] = new DocumentTombstone(
                normalized.Key,
                merged.Dot,
                merged.Context,
                merged.Hlc,
                current.CreatedAt <= normalized.CreatedAt ? current.CreatedAt : normalized.CreatedAt,
                normalized.Incarnation ?? current.Incarnation).Normalize();
            return true;
        }
    }

    /// <summary>Returns a stable snapshot suitable for anti-entropy or backup.</summary>
    public IReadOnlyList<DocumentTombstone> Snapshot()
    {
        lock (gate)
        {
            return tombstones.Values
                .OrderBy(value => value.Key.Tenant.Value, StringComparer.Ordinal)
                .ThenBy(value => value.Key.Collection.Value, StringComparer.Ordinal)
                .ThenBy(value => value.Key.DocumentId, StringComparer.Ordinal)
                .Select(value => value with { Context = CausalVersion.CopyContext(value.Context) })
                .ToArray();
        }
    }

    /// <summary>Gets a tombstone for a key, when one is present.</summary>
    public DocumentTombstone? Get(DocumentKey key)
    {
        lock (gate)
            return tombstones.TryGetValue(key, out var value) ? value with { Context = CausalVersion.CopyContext(value.Context) } : null;
    }

    /// <summary>
    /// Checks whether an incoming mutation is allowed to recreate the key. A
    /// mutation concurrent with or older than a tombstone remains suppressed.
    /// </summary>
    public bool Allows(ReplicationRecord incoming, string? incarnation = null)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        lock (gate)
        {
            if (!tombstones.TryGetValue(incoming.Key, out var tombstone))
                return true;
            if (incoming.Deleted)
                return true;
            if (incarnation is not null && tombstone.Incarnation is not null && incarnation != tombstone.Incarnation)
                return false;
            return CausalVersion.Contains(incoming.Context, tombstone.Dot);
        }
    }

    /// <summary>Returns true when a key is currently deleted at the known frontier.</summary>
    public bool IsDeleted(DocumentKey key)
    {
        lock (gate) return tombstones.ContainsKey(key);
    }

    /// <summary>
    /// Removes only tombstones acknowledged by the stable frontier. The caller
    /// must supply a retention boundary so a newly-created tombstone is never
    /// collected before peers have had a chance to observe it.
    /// </summary>
    public int Compact(
        IReadOnlyDictionary<string, ulong> stableFrontier,
        DateTimeOffset now,
        TimeSpan minimumRetention)
    {
        ArgumentNullException.ThrowIfNull(stableFrontier);
        if (minimumRetention < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumRetention));
        lock (gate)
        {
            var removable = tombstones
                .Where(pair => now - pair.Value.CreatedAt >= minimumRetention
                    && CausalVersion.Contains(stableFrontier, pair.Value.Dot))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in removable) tombstones.Remove(key);
            return removable.Length;
        }
    }

    private static ReplicationRecord ToRecord(DocumentTombstone tombstone) =>
        new(tombstone.Key, tombstone.Dot, tombstone.Context, tombstone.Hlc, true, null);
}
