using TerraBase.Engine.Lattices;

namespace TerraBase.Engine.Sqlite;

/// <summary>What a client database's local writes mint with: this member's replica id and its hybrid clock.</summary>
/// <param name="Id">The replica id of this process (<see cref="ReplicaIdentityFile"/>).</param>
/// <param name="Clock">The member's clock.</param>
internal sealed record LocalReplica(ReplicaId Id, MemberClock Clock);
