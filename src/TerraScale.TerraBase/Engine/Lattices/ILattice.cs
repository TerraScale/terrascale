namespace TerraBase.Engine.Lattices;

/// <summary>
/// A join-semilattice: <see cref="Join"/> is associative, commutative and idempotent, and <see cref="Bottom"/> is its
/// identity (Anna, ICDE 2018, section III). Delta-mutators (methods ending in <c>Delta</c>) return a state of the same
/// lattice that the caller joins into the current state and ships to other replicas (delta-state CRDTs, Definition 1).
/// </summary>
/// <typeparam name="TSelf">The implementing lattice.</typeparam>
public interface ILattice<TSelf> where TSelf : ILattice<TSelf>
{
    /// <summary>The least element.</summary>
    static abstract TSelf Bottom { get; }

    /// <summary>The least upper bound of this state and <paramref name="other"/>; neither changes.</summary>
    TSelf Join(TSelf other);
}
