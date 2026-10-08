namespace TerraBase.Engine.Lattices;

/// <summary>One event: the <paramref name="Counter"/>th write of <paramref name="Replica"/> (delta-state CRDTs, section 7.2). Counters start at 1.</summary>
/// <param name="Replica">The replica that made the event.</param>
/// <param name="Counter">Its sequence number at that replica.</param>
public readonly record struct Dot(ReplicaId Replica, ulong Counter)
{
    /// <inheritdoc />
    // Both halves of each value: ulong's own hash XORs them, so crafted counters would all collide in decoded sets.
    public override int GetHashCode() => HashCode.Combine((uint)Replica.Value, (uint)(Replica.Value >> 32), (uint)Counter, (uint)(Counter >> 32));
}
