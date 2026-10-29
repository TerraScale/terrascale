using TerraBase.Engine.Lattices;

namespace TerraBase.Engine;

internal sealed record LocalReplica(ReplicaId Id, MemberClock Clock);
