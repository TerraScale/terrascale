using TerraBase.Engine.Cluster;

namespace TerraBase.Features.Replication;

/// <summary>The actual configured outbound links, including disconnected peers; excludes self.</summary>
internal interface IRuntimePeerTopology
{
    /// <summary>Copies immutable identities without refreshing discovery or changing links.</summary>
    IReadOnlyList<ClusterMember> ConfiguredPeers();
}
