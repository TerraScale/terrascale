namespace TerraBase.Engine.Lattices;

/// <summary>A number that only grows: the join is the maximum. Sequence numbers, acknowledged positions, counters.</summary>
/// <param name="Value">The current maximum; the bottom is zero.</param>
public readonly record struct MaxLattice(ulong Value) : ILattice<MaxLattice>
{
    /// <inheritdoc />
    public static MaxLattice Bottom => default;

    /// <inheritdoc />
    public MaxLattice Join(MaxLattice other) => Value >= other.Value ? this : other;
}
