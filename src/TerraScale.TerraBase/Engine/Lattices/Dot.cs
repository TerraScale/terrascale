namespace TerraBase.Engine.Lattices;

/// <summary>One event: the <paramref name="Counter"/>th write of <paramref name="Replica"/> (delta-state CRDTs, section 7.2). Counters start at 1.</summary>
/// <param name="Replica">The replica that made the event.</param>
/// <param name="Counter">Its sequence number at that replica.</param>
public readonly record struct Dot(ReplicaId Replica, ulong Counter);
