using System.Collections.Immutable;

namespace TerraBase.Engine.Lattices;

/// <summary>
/// A map whose values are lattices: the join takes the union of the keys and joins the values of common keys (Anna,
/// section VI-B). An actor's state, a delta-group and a delta-interval are map lattices of document states.
/// </summary>
/// <typeparam name="TKey">The key.</typeparam>
/// <typeparam name="TValue">The value lattice.</typeparam>
/// <param name="Entries">The entries.</param>
public sealed record MapLattice<TKey, TValue>(ImmutableDictionary<TKey, TValue> Entries) : ILattice<MapLattice<TKey, TValue>>
    where TKey : notnull
    where TValue : ILattice<TValue>
{
    /// <summary>The empty map, the bottom.</summary>
    public MapLattice() : this([])
    {
    }

    static MapLattice<TKey, TValue> ILattice<MapLattice<TKey, TValue>>.Bottom => new();

    /// <inheritdoc />
    public MapLattice<TKey, TValue> Join(MapLattice<TKey, TValue> other)
    {
        var entries = Entries.ToBuilder();
        foreach (var (key, value) in other.Entries)
        {
            entries[key] = entries.TryGetValue(key, out var mine) ? mine.Join(value) : value;
        }

        return new(entries.ToImmutable());
    }

    /// <summary>The delta that joins <paramref name="delta"/> into the value of <paramref name="key"/>.</summary>
    public MapLattice<TKey, TValue> UpdateDelta(TKey key, TValue delta) => new(ImmutableDictionary<TKey, TValue>.Empty.Add(key, delta));

    /// <inheritdoc />
    public bool Equals(MapLattice<TKey, TValue>? other) =>
        other is not null
        && Entries.Count == other.Entries.Count
        && Entries.All(entry => other.Entries.TryGetValue(entry.Key, out var value) && EqualityComparer<TValue>.Default.Equals(entry.Value, value));

    /// <inheritdoc />
    public override int GetHashCode() => Entries.Count;
}
