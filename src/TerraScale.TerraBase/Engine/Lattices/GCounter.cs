namespace TerraBase.Engine.Lattices;

/// <summary>A grow-only counter: each replica counts its own increments, the join keeps each replica's maximum (delta-state CRDTs, figure 2).</summary>
/// <param name="Counts">Each replica's increments.</param>
public sealed record GCounter(MapLattice<ReplicaId, MaxLattice> Counts) : ILattice<GCounter>
{
    /// <summary>Zero, the bottom.</summary>
    public GCounter() : this(new MapLattice<ReplicaId, MaxLattice>())
    {
    }

    static GCounter ILattice<GCounter>.Bottom => new();

    /// <summary>The sum of every replica's increments.</summary>
    public ulong Value => Counts.Entries.Values.Aggregate(0UL, (sum, count) => checked(sum + count.Value));

    /// <inheritdoc />
    public GCounter Join(GCounter other) => new(Counts.Join(other.Counts));

    /// <summary>The delta that adds <paramref name="amount"/> to <paramref name="replica"/>'s count.</summary>
    public GCounter IncrementDelta(ReplicaId replica, ulong amount)
    {
        var current = Counts.Entries.TryGetValue(replica, out var count) ? count.Value : 0;
        return new(Counts.UpdateDelta(replica, new MaxLattice(checked(current + amount))));
    }
}
