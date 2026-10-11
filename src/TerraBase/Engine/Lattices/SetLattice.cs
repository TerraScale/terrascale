using System.Collections.Immutable;

namespace TerraBase.Engine.Lattices;

/// <summary>A grow-only set: the join is the union (delta-state CRDTs, figure 5).</summary>
/// <typeparam name="T">The element.</typeparam>
/// <param name="Items">The elements.</param>
public sealed record SetLattice<T>(ImmutableHashSet<T> Items) : ILattice<SetLattice<T>> where T : notnull
{
    /// <summary>The empty set, the bottom.</summary>
    public SetLattice() : this([])
    {
    }

    static SetLattice<T> ILattice<SetLattice<T>>.Bottom => new();

    /// <inheritdoc />
    public SetLattice<T> Join(SetLattice<T> other) => new(Items.Union(other.Items));

    /// <summary>The delta that adds <paramref name="item"/>.</summary>
    public SetLattice<T> AddDelta(T item) => new([item]);

    /// <inheritdoc />
    public bool Equals(SetLattice<T>? other) => other is not null && Items.SetEquals(other.Items);

    /// <inheritdoc />
    public override int GetHashCode() => Items.Count;
}
