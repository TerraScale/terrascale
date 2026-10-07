using System.Collections.Immutable;

namespace TerraBase.Engine.Lattices;

/// <summary>
/// A set of <see cref="Dot"/>s, the events a state has seen, kept compact: <see cref="Versions"/> holds each replica's
/// contiguous prefix <c>1..n</c> and <see cref="Cloud"/> the dots beyond it (delta-state CRDTs, section 7.2). Both are
/// canonical, so equal sets have equal representations. The join is the union. A context without a cloud, the usual
/// case, adds and reads dots without allocating beyond the new version vector.
/// </summary>
public sealed record CausalContext : ILattice<CausalContext>
{
    /// <summary>The empty context, the bottom.</summary>
    public CausalContext() : this([], [])
    {
    }

    private CausalContext(ImmutableDictionary<ReplicaId, ulong> versions, ImmutableHashSet<Dot> cloud)
    {
        Versions = versions;
        Cloud = cloud;
    }

    static CausalContext ILattice<CausalContext>.Bottom => new();

    /// <summary>Each replica's contiguous prefix: replica <c>r</c> maps to <c>n</c> when dots <c>(r, 1..n)</c> are all present. Never holds zero.</summary>
    public ImmutableDictionary<ReplicaId, ulong> Versions { get; }

    /// <summary>The dots past their replica's prefix.</summary>
    public ImmutableHashSet<Dot> Cloud { get; }

    /// <summary>The context holding exactly <paramref name="dots"/>.</summary>
    public static CausalContext Of(IEnumerable<Dot> dots) => Compact([], [.. dots]);

    /// <summary>Whether <paramref name="dot"/> is in the context.</summary>
    public bool Contains(Dot dot) =>
        dot.Counter <= Versions.GetValueOrDefault(dot.Replica) || (!Cloud.IsEmpty && Cloud.Contains(dot));

    /// <summary>The greatest counter of <paramref name="replica"/> in the context, or zero.</summary>
    public ulong Max(ReplicaId replica)
    {
        var max = Versions.GetValueOrDefault(replica);
        foreach (var dot in Cloud)
        {
            if (dot.Replica == replica && dot.Counter > max)
            {
                max = dot.Counter;
            }
        }

        return max;
    }

    /// <summary>The context with <paramref name="dot"/> added.</summary>
    public CausalContext Add(Dot dot)
    {
        var prefix = Versions.GetValueOrDefault(dot.Replica);
        if (Contains(dot))
        {
            return this;
        }

        return dot.Counter == prefix + 1 && Cloud.IsEmpty
            ? new(Versions.SetItem(dot.Replica, dot.Counter), Cloud)
            : Compact(Versions, Cloud.Add(dot));
    }

    /// <inheritdoc />
    public CausalContext Join(CausalContext other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var versions = Versions;
        foreach (var (replica, counter) in other.Versions)
        {
            if (counter > versions.GetValueOrDefault(replica))
            {
                versions = versions.SetItem(replica, counter);
            }
        }

        var cloud = Cloud.IsEmpty ? other.Cloud : other.Cloud.IsEmpty ? Cloud : Cloud.Union(other.Cloud);
        if (!cloud.IsEmpty)
        {
            return Compact(versions, cloud);
        }

        return ReferenceEquals(versions, Versions) ? this : new(versions, cloud);
    }

    /// <inheritdoc />
    public bool Equals(CausalContext? other) =>
        other is not null
        && Versions.Count == other.Versions.Count
        && Versions.All(entry => other.Versions.TryGetValue(entry.Key, out var counter) && counter == entry.Value)
        && Cloud.SetEquals(other.Cloud);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Versions.Count, Cloud.Count);

    // Moves cloud dots into their replica's prefix while they extend it, and drops those the prefix already covers.
    private static CausalContext Compact(ImmutableDictionary<ReplicaId, ulong> versions, ImmutableHashSet<Dot> cloud)
    {
        if (cloud.IsEmpty)
        {
            return new(versions, cloud);
        }

        var prefixes = versions.ToBuilder();
        var rest = cloud.ToBuilder();
        foreach (var dot in cloud)
        {
            var prefix = prefixes.GetValueOrDefault(dot.Replica);
            if (dot.Counter <= prefix)
            {
                rest.Remove(dot);
                continue;
            }

            while (rest.Remove(new Dot(dot.Replica, prefix + 1)))
            {
                prefix++;
            }

            if (prefix > 0)
            {
                prefixes[dot.Replica] = prefix;
            }
        }

        return new(prefixes.ToImmutable(), rest.ToImmutable());
    }
}
