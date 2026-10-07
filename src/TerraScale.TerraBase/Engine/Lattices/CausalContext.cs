using System.Collections.Immutable;

namespace TerraBase.Engine.Lattices;

/// <summary>
/// A set of <see cref="Dot"/>s, the events a state has seen, kept compact: <see cref="Versions"/> holds each replica's
/// contiguous prefix <c>1..n</c> and <see cref="Cloud"/> the dots beyond it (delta-state CRDTs, section 7.2). Both are
/// canonical, so equal sets have equal representations. The join is the union.
/// </summary>
public sealed record CausalContext : ILattice<CausalContext>
{
    /// <summary>The empty context, the bottom.</summary>
    public CausalContext() : this([], [])
    {
    }

    private CausalContext(ImmutableDictionary<ulong, ulong> versions, ImmutableHashSet<Dot> cloud)
    {
        Versions = versions;
        Cloud = cloud;
    }

    static CausalContext ILattice<CausalContext>.Bottom => new();

    /// <summary>Each replica's contiguous prefix: replica <c>r</c> maps to <c>n</c> when dots <c>(r, 1..n)</c> are all present. Never holds zero.</summary>
    public ImmutableDictionary<ulong, ulong> Versions { get; }

    /// <summary>The dots past their replica's prefix.</summary>
    public ImmutableHashSet<Dot> Cloud { get; }

    /// <summary>The context holding exactly <paramref name="dots"/>.</summary>
    public static CausalContext Of(IEnumerable<Dot> dots) =>
        Compact(ImmutableDictionary.CreateBuilder<ulong, ulong>(), [.. dots]);

    /// <summary>Whether <paramref name="dot"/> is in the context.</summary>
    public bool Contains(Dot dot) =>
        dot.Counter <= Versions.GetValueOrDefault(dot.Replica) || Cloud.Contains(dot);

    /// <summary>The greatest counter of <paramref name="replica"/> in the context, or zero.</summary>
    public ulong Max(ulong replica) =>
        Cloud.Where(dot => dot.Replica == replica).Select(dot => dot.Counter).DefaultIfEmpty(Versions.GetValueOrDefault(replica)).Max();

    /// <summary>The context with <paramref name="dot"/> added.</summary>
    public CausalContext Add(Dot dot) => Contains(dot) ? this : Compact(Versions.ToBuilder(), Cloud.Add(dot));

    /// <inheritdoc />
    public CausalContext Join(CausalContext other)
    {
        var versions = Versions.ToBuilder();
        foreach (var (replica, counter) in other.Versions)
        {
            versions[replica] = Math.Max(counter, versions.GetValueOrDefault(replica));
        }

        return Compact(versions, Cloud.Union(other.Cloud));
    }

    /// <inheritdoc />
    public bool Equals(CausalContext? other) =>
        other is not null
        && Versions.Count == other.Versions.Count
        && Versions.All(entry => other.Versions.TryGetValue(entry.Key, out var counter) && counter == entry.Value)
        && Cloud.SetEquals(other.Cloud);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Versions.Count, Cloud.Count);

    private static CausalContext Compact(ImmutableDictionary<ulong, ulong>.Builder versions, ImmutableHashSet<Dot> cloud)
    {
        var rest = ImmutableHashSet.CreateBuilder<Dot>();
        foreach (var dot in cloud.OrderBy(dot => dot.Replica).ThenBy(dot => dot.Counter))
        {
            var prefix = versions.GetValueOrDefault(dot.Replica);
            if (dot.Counter == prefix + 1)
            {
                versions[dot.Replica] = dot.Counter;
            }
            else if (dot.Counter > prefix)
            {
                rest.Add(dot);
            }
        }

        return new(versions.ToImmutable(), rest.ToImmutable());
    }
}
