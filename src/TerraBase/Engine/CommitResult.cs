namespace TerraBase.Engine;

/// <summary>A committed client transaction's replication position in one database incarnation.</summary>
/// <param name="Incarnation">The database file whose writer committed the transaction.</param>
/// <param name="ChangeId">The transaction's own final CDC commit row, read before releasing its writer.</param>
public readonly record struct CommitMarker(Ulid Incarnation, long ChangeId);

/// <summary>A committed transaction's offset and, when it changed client data, its own replication marker.</summary>
/// <param name="Offset">The existing database offset, also used by system transactions.</param>
/// <param name="Marker">Null for nonmutating work, refused work, and transactions outside the client change stream.</param>
public readonly record struct CommitResult(long Offset, CommitMarker? Marker);
