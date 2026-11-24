using System.Buffers;
using System.Collections.Frozen;
using System.IO.Hashing;
using System.Text;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed record StrongParticipantTarget(string Region, string NodeId, bool IsLocal);

public sealed class ClusterTopology(ClusterOptions options)
{
    private static readonly SearchValues<char> Hex = SearchValues.Create("0123456789abcdefABCDEF");

    private readonly FrozenDictionary<string, PeerNodeInfo> _peers = options.Peers.ToFrozenDictionary(static peer => peer.NodeId, StringComparer.Ordinal);
    private readonly FrozenDictionary<(string Node, string Region), string> _origins = Origins(options);

    public string LocalNodeId => options.NodeId;

    public string LocalRegion => options.Region;

    public string StorageOrigin => options.StorageOrigin;

    public IReadOnlyList<PeerNodeInfo> Peers => options.Peers;

    public IReadOnlyList<PeerNodeInfo> RegionPeers { get; } = options.RegionPeers;

    public IReadOnlyList<PeerNodeInfo> RemotePeers { get; } = options.RemotePeers;

    public IReadOnlyList<string> Regions { get; } = options.Regions;

    public int RegionQuorum => (Regions.Count / 2) + 1;

    public PeerNodeInfo? Find(string nodeId) =>
        _peers.GetValueOrDefault(nodeId);

    public bool IsPeer(QuicCallContext context) =>
        !context.IsPlatform && Find(context.RemoteNodeId) is { } peer
        && string.Equals(peer.Region, context.RemoteRegion, StringComparison.Ordinal);

    internal bool IsPeer(QuicCallContext context, string nodeId) =>
        IsPeer(context) && string.Equals(context.RemoteNodeId, nodeId, StringComparison.Ordinal);

    internal bool IsRegionPeer(QuicCallContext context) =>
        IsPeer(context) && string.Equals(context.RemoteRegion, LocalRegion, StringComparison.Ordinal);

    internal bool IsPeerOrigin(QuicCallContext context, string origin) =>
        _origins.TryGetValue((context.RemoteNodeId, context.RemoteRegion), out var expected) && MatchesOrigin(expected, origin)
        && !_origins.Any(entry => !string.Equals(entry.Key.Node, context.RemoteNodeId, StringComparison.Ordinal) && MatchesOrigin(entry.Value, origin));

    internal bool IsRegionOrigin(string region, string origin) =>
        _origins.Any(entry => string.Equals(region, entry.Key.Region, StringComparison.Ordinal) && MatchesOrigin(entry.Value, origin));

    internal static QuicRemoteError PeerForbidden() => new("forbidden", "the authenticated node is not authorized for this cluster operation");

    private static FrozenDictionary<(string Node, string Region), string> Origins(ClusterOptions options) =>
        options.Peers.Select(static peer => (Node: peer.NodeId, peer.Region))
            .Append((Node: options.NodeId, options.Region))
            .GroupBy(static member => member.Region, StringComparer.Ordinal)
            .SelectMany(static region => region.Select(member => KeyValuePair.Create(member, region.Count() == 1 ? region.Key : ClusterOptions.OriginFor(member.Node))))
            .ToFrozenDictionary();

    private static bool MatchesOrigin(string baseOrigin, string origin)
    {
        if (string.Equals(origin, baseOrigin, StringComparison.Ordinal))
        {
            return true;
        }

        var separator = origin.LastIndexOf('-');
        if (separator <= 0 || origin.Length - separator - 1 is < 8 or > 16 || origin.Length > 63)
        {
            return false;
        }

        var maxBaseLength = 63 - (origin.Length - separator);
        return origin.AsSpan(0, separator).SequenceEqual(baseOrigin.AsSpan(0, Math.Min(maxBaseLength, baseOrigin.Length)))
            && !origin.AsSpan(separator + 1).ContainsAnyExcept(Hex);
    }

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
