using System.IO.Hashing;
using System.Text;

namespace TerraBase.Cluster;

public sealed record StrongParticipantTarget(string Region, string NodeId, bool IsLocal);

public sealed class ClusterTopology(ClusterOptions options)
{
    public string LocalNodeId => options.NodeId;

    public string LocalRegion => options.Region;

    public string StorageOrigin => options.StorageOrigin;

    public IReadOnlyList<PeerNodeInfo> Peers => options.Peers;

    public IReadOnlyList<PeerNodeInfo> RegionPeers { get; } = options.RegionPeers;

    public IReadOnlyList<PeerNodeInfo> RemotePeers { get; } = options.RemotePeers;

    public IReadOnlyList<string> Regions { get; } = options.Regions;

    public int RegionQuorum => (Regions.Count / 2) + 1;

    public PeerNodeInfo? Find(string nodeId) =>
        options.Peers.FirstOrDefault(peer => string.Equals(peer.NodeId, nodeId, StringComparison.Ordinal));

    public IReadOnlyList<StrongParticipantTarget> StrongParticipants(Ulid databaseId) =>
        [.. Regions.Select(region => StrongCandidates(region, databaseId)[0])];

    public IReadOnlyList<StrongParticipantTarget> StrongCandidates(string region, Ulid databaseId)
    {
        var candidates = options.Peers.Where(peer => string.Equals(peer.Region, region, StringComparison.Ordinal)).Select(static peer => peer.NodeId);
        if (string.Equals(region, options.Region, StringComparison.Ordinal))
        {
            candidates = candidates.Append(options.NodeId);
        }

        StrongParticipantTarget[] ordered = [.. candidates
            .OrderByDescending(nodeId => Weight(databaseId, nodeId))
            .Select(nodeId => new StrongParticipantTarget(region, nodeId, string.Equals(nodeId, options.NodeId, StringComparison.Ordinal)))];
        return ordered.Length > 0 ? ordered : [new StrongParticipantTarget(region, options.NodeId, true)];
    }

    private static ulong Weight(Ulid databaseId, string nodeId)
    {
        var node = Encoding.UTF8.GetBytes(nodeId);
        Span<byte> buffer = stackalloc byte[16 + node.Length];
        _ = databaseId.TryWriteBytes(buffer);
        node.CopyTo(buffer[16..]);
        return XxHash64.HashToUInt64(buffer);
    }
}
