namespace TerraBase.Engine.Lattices;

/// <summary>
/// A last-writer-wins register: the greater <c>(Stamp, Replica)</c> wins (a lexicographic pair, delta-state CRDTs,
/// figure 4). Every write needs its own <c>(Stamp, Replica)</c>; two different values under one pair break the laws.
/// </summary>
/// <typeparam name="T">The value.</typeparam>
/// <param name="Stamp">When the value was written.</param>
/// <param name="Replica">The replica that wrote it; breaks ties between equal stamps.</param>
/// <param name="Value">The value.</param>
public readonly record struct LwwRegister<T>(Hlc Stamp, ReplicaId Replica, T Value) : ILattice<LwwRegister<T>>
{
    static LwwRegister<T> ILattice<LwwRegister<T>>.Bottom => default;

    /// <inheritdoc />
    public LwwRegister<T> Join(LwwRegister<T> other) =>
        Stamp.Value > other.Stamp.Value || (Stamp.Value == other.Stamp.Value && Replica.Value >= other.Replica.Value) ? this : other;
}
