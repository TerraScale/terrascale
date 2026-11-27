using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.IO.Hashing;
using System.Text;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed record StrongParticipantTarget(string Region, string NodeId, bool IsLocal);

public sealed class ClusterTopology
{
    private const int MaxOriginCacheEntries = 1024;
    private const int MaxStrongCandidatesCache = 4096;

    private static readonly SearchValues<char> Hex = SearchValues.Create("0123456789abcdefABCDEF");
    private static readonly SearchValues<byte> HexUtf8 = SearchValues.Create("0123456789abcdefABCDEF"u8);

    private readonly ClusterOptions _options;
    private readonly FrozenDictionary<string, PeerNodeInfo> _peers;
    private readonly FrozenDictionary<string, PeerOriginSlot> _peerOrigins;
    private readonly OriginEntry[] _originEntries;
    private readonly FrozenDictionary<string, RegionCandidates> _candidatesByRegion;
    private readonly ConcurrentDictionary<(string NodeId, string Origin), bool> _peerOriginCache = new();
    private readonly ConcurrentDictionary<(string Region, Ulid DatabaseId), StrongParticipantTarget[]> _strongCandidatesCache = new();

    public ClusterTopology(ClusterOptions options)
    {
        _options = options;
        _peers = options.Peers.ToFrozenDictionary(static peer => peer.NodeId, StringComparer.Ordinal);
        var origins = BuildOrigins(options);
        _originEntries = [.. origins.Select(static entry => new OriginEntry(entry.Key.Node, entry.Key.Region, entry.Value, Encoding.UTF8.GetBytes(entry.Value)))];
        _peerOrigins = BuildPeerOrigins(_originEntries);
        RegionPeers = options.RegionPeers;
        RemotePeers = options.RemotePeers;
        Regions = options.Regions;
        _candidatesByRegion = BuildRegionCandidates(options, Regions);
    }

    public string LocalNodeId => _options.NodeId;

    public string LocalRegion => _options.Region;

    public string StorageOrigin => _options.StorageOrigin;

    public IReadOnlyList<PeerNodeInfo> Peers => _options.Peers;

    public IReadOnlyList<PeerNodeInfo> RegionPeers { get; }

    public IReadOnlyList<PeerNodeInfo> RemotePeers { get; }

    public IReadOnlyList<string> Regions { get; }

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

    internal bool IsPeerOrigin(QuicCallContext context, string origin)
    {
        if (!_peerOrigins.TryGetValue(context.RemoteNodeId, out var slot)
            || !string.Equals(slot.Region, context.RemoteRegion, StringComparison.Ordinal))
        {
            return false;
        }

        if (ReferenceEquals(Volatile.Read(ref slot.LastVerifiedOrigin), origin))
        {
            return true;
        }

        var cacheKey = (context.RemoteNodeId, origin);
        if (_peerOriginCache.TryGetValue(cacheKey, out var cached))
        {
            if (cached)
            {
                Volatile.Write(ref slot.LastVerifiedOrigin, origin);
            }

            return cached;
        }

        var allowed = ComputePeerOrigin(context.RemoteNodeId, slot.BaseOrigin, origin);
        if (_peerOriginCache.Count < MaxOriginCacheEntries)
        {
            _ = _peerOriginCache.TryAdd(cacheKey, allowed);
        }

        if (allowed)
        {
            Volatile.Write(ref slot.LastVerifiedOrigin, origin);
        }

        return allowed;
    }

    internal bool IsPeerOrigin(QuicCallContext context, ReadOnlySpan<byte> originUtf8)
    {
        if (!_peerOrigins.TryGetValue(context.RemoteNodeId, out var slot)
            || !string.Equals(slot.Region, context.RemoteRegion, StringComparison.Ordinal)
            || !MatchesOriginUtf8(slot.BaseOriginUtf8, originUtf8))
        {
            return false;
        }

        for (var index = 0; index < _originEntries.Length; index++)
        {
            var entry = _originEntries[index];
            if (!string.Equals(entry.Node, context.RemoteNodeId, StringComparison.Ordinal) && MatchesOriginUtf8(entry.BaseOriginUtf8, originUtf8))
            {
                return false;
            }
        }

        return true;
    }

    internal bool IsRegionOrigin(string region, string origin)
    {
        for (var index = 0; index < _originEntries.Length; index++)
        {
            var entry = _originEntries[index];
            if (string.Equals(region, entry.Region, StringComparison.Ordinal) && MatchesOrigin(entry.BaseOrigin, origin))
            {
                return true;
            }
        }

        return false;
    }

    internal bool IsRegionOrigin(string region, ReadOnlySpan<byte> originUtf8)
    {
        for (var index = 0; index < _originEntries.Length; index++)
        {
            var entry = _originEntries[index];
            if (string.Equals(region, entry.Region, StringComparison.Ordinal) && MatchesOriginUtf8(entry.BaseOriginUtf8, originUtf8))
            {
                return true;
            }
        }

        return false;
    }

    internal static QuicRemoteError PeerForbidden() => new("forbidden", "the authenticated node is not authorized for this cluster operation");

    public IReadOnlyList<StrongParticipantTarget> StrongParticipants(Ulid databaseId)
    {
        var participants = new StrongParticipantTarget[Regions.Count];
        for (var index = 0; index < Regions.Count; index++)
        {
            participants[index] = StrongCandidates(Regions[index], databaseId)[0];
        }

        return participants;
    }

    public IReadOnlyList<StrongParticipantTarget> StrongCandidates(string region, Ulid databaseId)
    {
        if (!_candidatesByRegion.TryGetValue(region, out var group) || group.Nodes.Length == 0)
        {
            return [new StrongParticipantTarget(region, _options.NodeId, true)];
        }

        if (group.SingleTarget is { } single)
        {
            return single;
        }

        var cacheKey = (region, databaseId);
        if (_strongCandidatesCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var count = group.Nodes.Length;
        Span<byte> dbBytes = stackalloc byte[16];
        _ = databaseId.TryWriteBytes(dbBytes);
        var ordered = new StrongParticipantTarget[count];
        var weights = count <= 16 ? stackalloc ulong[count] : new ulong[count];
        for (var index = 0; index < count; index++)
        {
            var node = group.Nodes[index];
            ordered[index] = node.Target;
            weights[index] = Weight(dbBytes, node.NodeIdUtf8);
        }

        for (var index = 1; index < count; index++)
        {
            var keyWeight = weights[index];
            var keyTarget = ordered[index];
            var position = index - 1;
            while (position >= 0 && weights[position] < keyWeight)
            {
                weights[position + 1] = weights[position];
                ordered[position + 1] = ordered[position];
                position--;
            }

            weights[position + 1] = keyWeight;
            ordered[position + 1] = keyTarget;
        }

        if (_strongCandidatesCache.Count >= MaxStrongCandidatesCache)
        {
            _strongCandidatesCache.Clear();
        }

        return _strongCandidatesCache.GetOrAdd(cacheKey, ordered);
    }

    private bool ComputePeerOrigin(string remoteNodeId, string expectedBaseOrigin, string origin)
    {
        if (!MatchesOrigin(expectedBaseOrigin, origin))
        {
            return false;
        }

        for (var index = 0; index < _originEntries.Length; index++)
        {
            var entry = _originEntries[index];
            if (!string.Equals(entry.Node, remoteNodeId, StringComparison.Ordinal) && MatchesOrigin(entry.BaseOrigin, origin))
            {
                return false;
            }
        }

        return true;
    }

    private static FrozenDictionary<(string Node, string Region), string> BuildOrigins(ClusterOptions options) =>
        options.Peers.Select(static peer => (Node: peer.NodeId, peer.Region))
            .Append((Node: options.NodeId, options.Region))
            .GroupBy(static member => member.Region, StringComparer.Ordinal)
            .SelectMany(static region => region.Select(member => KeyValuePair.Create(member, region.Count() == 1 ? region.Key : ClusterOptions.OriginFor(member.Node))))
            .ToFrozenDictionary();

    private static FrozenDictionary<string, PeerOriginSlot> BuildPeerOrigins(OriginEntry[] entries)
    {
        var map = new Dictionary<string, PeerOriginSlot>(entries.Length, StringComparer.Ordinal);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            map[entry.Node] = new PeerOriginSlot(entry.Region, entry.BaseOrigin, entry.BaseOriginUtf8);
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, RegionCandidates> BuildRegionCandidates(ClusterOptions options, IReadOnlyList<string> regions)
    {
        var map = new Dictionary<string, RegionCandidates>(regions.Count, StringComparer.Ordinal);
        for (var index = 0; index < regions.Count; index++)
        {
            var region = regions[index];
            List<CandidateNode> nodes = [];
            for (var peerIndex = 0; peerIndex < options.Peers.Count; peerIndex++)
            {
                var peer = options.Peers[peerIndex];
                if (string.Equals(peer.Region, region, StringComparison.Ordinal))
                {
                    var target = new StrongParticipantTarget(region, peer.NodeId, false);
                    nodes.Add(new CandidateNode(target, Encoding.UTF8.GetBytes(peer.NodeId)));
                }
            }

            if (string.Equals(region, options.Region, StringComparison.Ordinal))
            {
                var target = new StrongParticipantTarget(region, options.NodeId, true);
                nodes.Add(new CandidateNode(target, Encoding.UTF8.GetBytes(options.NodeId)));
            }

            var array = nodes.ToArray();
            StrongParticipantTarget[]? single = array.Length == 1 ? [array[0].Target] : null;
            map[region] = new RegionCandidates(array, single);
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

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

    private static bool MatchesOriginUtf8(ReadOnlySpan<byte> baseOrigin, ReadOnlySpan<byte> origin)
    {
        if (origin.SequenceEqual(baseOrigin))
        {
            return true;
        }

        var separator = origin.LastIndexOf((byte)'-');
        if (separator <= 0 || origin.Length - separator - 1 is < 8 or > 16 || origin.Length > 63)
        {
            return false;
        }

        var maxBaseLength = 63 - (origin.Length - separator);
        return origin[..separator].SequenceEqual(baseOrigin[..Math.Min(maxBaseLength, baseOrigin.Length)])
            && !origin[(separator + 1)..].ContainsAnyExcept(HexUtf8);
    }

    private static ulong Weight(ReadOnlySpan<byte> dbBytes, byte[] nodeUtf8)
    {
        var total = 16 + nodeUtf8.Length;
        Span<byte> buffer = total <= 128 ? stackalloc byte[total] : new byte[total];
        dbBytes.CopyTo(buffer);
        nodeUtf8.CopyTo(buffer[16..]);
        return XxHash64.HashToUInt64(buffer);
    }

    private sealed class PeerOriginSlot(string region, string baseOrigin, byte[] baseOriginUtf8)
    {
        public string Region { get; } = region;

        public string BaseOrigin { get; } = baseOrigin;

        public byte[] BaseOriginUtf8 { get; } = baseOriginUtf8;

        public string? LastVerifiedOrigin;
    }

    private readonly record struct OriginEntry(string Node, string Region, string BaseOrigin, byte[] BaseOriginUtf8);

    private readonly record struct CandidateNode(StrongParticipantTarget Target, byte[] NodeIdUtf8);

    private sealed record RegionCandidates(CandidateNode[] Nodes, StrongParticipantTarget[]? SingleTarget);
}
