using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TerraScale.QuicRpc;

namespace TerraBase.Cluster;

public sealed record ClusterConfigurationError(string Message, string Code);

public readonly union ClusterOptionsOutcome(ClusterOptions, ClusterConfigurationError);

public sealed record ClusterSettings(string NodeId, string Region, string DataDirectory, int QuicPort, int InternalPort, string? PeersFile, string? PeerSecret);

public sealed record ClusterOptions
{
    public const string PeerSecretVariable = "TERRABASE_PEER_SECRET";
    public const string MeshCertificateVariable = "TERRABASE_MESH_CERT_FILE";
    public const string MeshKeyVariable = "TERRABASE_MESH_KEY_FILE";
    public const string MeshAuthorityVariable = "TERRABASE_MESH_CA_FILE";

    public required string NodeId { get; init; }

    public required string Region { get; init; }

    public required string DataDirectory { get; init; }

    public required IPEndPoint ListenEndPoint { get; init; }

    public required IReadOnlyList<PeerNodeInfo> Peers { get; init; }

    public required QuicMeshCertificates Certificates { get; init; }

    public int RaftShardCount { get; init; } = 16;

    public TimeSpan RaftElectionTimeoutLower { get; init; } = TimeSpan.FromMilliseconds(300);

    public TimeSpan RaftElectionTimeoutUpper { get; init; } = TimeSpan.FromMilliseconds(600);

    public TimeSpan RaftRequestTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan RaftCommitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan StrongPrepareTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan StrongLockLease { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan WoundTimeout { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(1);

    public int UnreachableAfterFailures { get; init; } = 3;

    public TimeSpan AntiEntropyInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan CatchUpTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int CatchUpPageSize { get; init; } = 512;

    public int MaxReplicationParallelism { get; init; } = 8;

    public string? StorageIncarnation { get; init; }

    public IReadOnlyList<string> Regions =>
        [.. Peers.Select(static peer => peer.Region).Append(Region).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public IReadOnlyList<PeerNodeInfo> RegionPeers => [.. Peers.Where(peer => string.Equals(peer.Region, Region, StringComparison.Ordinal))];

    public IReadOnlyList<PeerNodeInfo> RemotePeers => [.. Peers.Where(peer => !string.Equals(peer.Region, Region, StringComparison.Ordinal))];

    public string StorageOrigin
    {
        get
        {
            var baseOrigin = RegionPeers.Count == 0 ? Region : OriginFor(NodeId);
            if (string.IsNullOrEmpty(StorageIncarnation))
            {
                return baseOrigin;
            }

            var maxBase = Math.Max(1, 63 - StorageIncarnation.Length - 1);
            var trimmed = baseOrigin.Length > maxBase ? baseOrigin[..maxBase] : baseOrigin;
            return string.Concat(trimmed, "-", StorageIncarnation);
        }
    }

    public bool IsRegionBootstrapNode => !RegionPeers.Any(peer => string.CompareOrdinal(peer.NodeId, NodeId) < 0);

    public bool IsBootstrapNode => !Peers.Any(peer => string.CompareOrdinal(peer.Region, Region) switch
    {
        < 0 => true,
        0 => string.CompareOrdinal(peer.NodeId, NodeId) < 0,
        > 0 => false,
    });

    public QuicMeshOptions ToMeshOptions() => new()
    {
        NodeId = NodeId,
        Region = Region,
        ListenEndPoint = ListenEndPoint,
        Certificate = Certificates.Certificate,
        TrustedAuthorities = Certificates.TrustedAuthorities,
        IntermediateCertificates = Certificates.Intermediates,
        CertificateRotator = Certificates.Rotator,
    };

    public ClusterOptions Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(NodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Region);
        ArgumentException.ThrowIfNullOrWhiteSpace(DataDirectory);
        ArgumentNullException.ThrowIfNull(ListenEndPoint);
        ArgumentNullException.ThrowIfNull(Peers);
        ArgumentNullException.ThrowIfNull(Certificates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RaftShardCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RaftShardCount, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CatchUpPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxReplicationParallelism);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(UnreachableAfterFailures);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StrongLockLease, StrongPrepareTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RaftElectionTimeoutUpper, RaftElectionTimeoutLower);
        if (Peers.Any(peer => string.Equals(peer.NodeId, NodeId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Peers must not contain the local node.", nameof(Peers));
        }

        if (Peers.Select(static peer => peer.NodeId).Distinct(StringComparer.Ordinal).Count() != Peers.Count)
        {
            throw new ArgumentException("Peer node ids must be unique.", nameof(Peers));
        }

        return this;
    }

    public static string OriginFor(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        var builder = new StringBuilder(nodeId.Length);
        foreach (var character in nodeId)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? char.ToLowerInvariant(character) : '-');
        }

        return builder.ToString();
    }

    public static ClusterOptionsOutcome Create(ClusterSettings settings, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var discovered = PeerMeshDiscovery.Discover(settings.PeersFile, settings.QuicPort, settings.InternalPort);
        return discovered switch
        {
            IReadOnlyList<PeerNodeInfo> all => Assemble(settings, all, MeshCertificates(settings, all.Count == 0, timeProvider)),
            ClusterConfigurationError invalid => invalid,
        };
    }

    private static ClusterOptionsOutcome Assemble(ClusterSettings settings, IReadOnlyList<PeerNodeInfo> all, MeshPkiOutcome certificates) => certificates switch
    {
        QuicMeshCertificates loaded => Build(settings, all, loaded),
        MeshPkiInvalid invalid => new ClusterConfigurationError(invalid.Message, invalid.Code),
    };

    private static ClusterOptions Build(ClusterSettings settings, IReadOnlyList<PeerNodeInfo> all, QuicMeshCertificates loaded)
    {
        PeerNodeInfo[] peers = [.. all.Where(peer => !string.Equals(peer.NodeId, settings.NodeId, StringComparison.Ordinal))];
        return new ClusterOptions
        {
            NodeId = settings.NodeId,
            Region = settings.Region,
            DataDirectory = Path.Combine(settings.DataDirectory, "cluster"),
            ListenEndPoint = ListenFor(all.FirstOrDefault(peer => string.Equals(peer.NodeId, settings.NodeId, StringComparison.Ordinal)), all.Count == 0, settings.QuicPort),
            Peers = peers,
            Certificates = loaded,
            StorageIncarnation = LoadOrCreateIncarnation(settings.DataDirectory, peers.Length),
        };
    }

    private static string? LoadOrCreateIncarnation(string dataDirectory, int peerCount)
    {
        if (peerCount == 0)
        {
            return null;
        }

        var path = Path.Combine(dataDirectory, "storage-incarnation");
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length is >= 8 and <= 16 && existing.All(static character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'))
                {
                    return existing;
                }
            }

            _ = Directory.CreateDirectory(dataDirectory);
            var created = RandomNumberGenerator.GetHexString(8, lowercase: true);
            File.WriteAllText(path, created);
            return created;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IPEndPoint ListenFor(PeerNodeInfo? self, bool standalone, int quicPort)
    {
        if (self is not null && IPAddress.TryParse(self.Host, out var address))
        {
            return new IPEndPoint(address, self.QuicPort);
        }

        return new IPEndPoint(standalone ? IPAddress.Loopback : IPAddress.Any, self?.QuicPort ?? quicPort);
    }

    private static MeshPkiOutcome MeshCertificates(ClusterSettings settings, bool standalone, TimeProvider timeProvider)
    {
        var certificate = Environment.GetEnvironmentVariable(MeshCertificateVariable);
        var key = Environment.GetEnvironmentVariable(MeshKeyVariable);
        var authority = Environment.GetEnvironmentVariable(MeshAuthorityVariable);
        if (!string.IsNullOrWhiteSpace(certificate) || !string.IsNullOrWhiteSpace(key) || !string.IsNullOrWhiteSpace(authority))
        {
            return string.IsNullOrWhiteSpace(certificate) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(authority)
                ? new MeshPkiInvalid(string.Concat(MeshCertificateVariable, ", ", MeshKeyVariable, " and ", MeshAuthorityVariable, " must be set together."), "cluster_mesh_pem")
                : QuicMeshPki.LoadPem(certificate, key, authority, settings.NodeId, settings.Region, timeProvider);
        }

        if (settings.PeerSecret is { } secret)
        {
            return QuicMeshPki.DeriveNode(QuicMeshPki.DecodeSecret(secret), settings.NodeId, settings.Region, timeProvider);
        }

        return standalone
            ? QuicMeshPki.DeriveNode(RandomNumberGenerator.GetBytes(32), settings.NodeId, settings.Region, timeProvider)
            : new MeshPkiInvalid(string.Concat(PeerSecretVariable, " (or ", MeshCertificateVariable, ") is required for the QUIC mesh."), "cluster_peer_secret");
    }
}
