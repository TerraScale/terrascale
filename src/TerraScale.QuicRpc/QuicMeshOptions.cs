using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace TerraScale.QuicRpc;

public sealed record QuicPeer(string NodeId, string Region, IPEndPoint EndPoint)
{
    public QuicPeer Validate()
    {
        QuicMeshIdentity.EnsureValidSegment(NodeId, nameof(NodeId));
        QuicMeshIdentity.EnsureValidSegment(Region, nameof(Region));
        ArgumentNullException.ThrowIfNull(EndPoint);
        return this;
    }
}

public sealed record QuicCallContext(string RemoteNodeId, string RemoteRegion, ushort MessageTypeId, ulong RequestId);

internal static class QuicMeshLimits
{
    public const int MaxInboundBidirectionalStreams = 1024;
    public const int MaxInboundUnidirectionalStreams = 1024;
    public const int OrderedPushQueueCapacity = 4096;

    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan OrderedChannelIdleTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ConnectFailureBackoff = TimeSpan.FromMilliseconds(250);
}

public sealed record QuicMeshOptions
{
    public required string NodeId { get; init; }

    public required string Region { get; init; }

    public required IPEndPoint ListenEndPoint { get; init; }

    public required X509Certificate2 Certificate { get; init; }

    public required IReadOnlyList<X509Certificate2> TrustedAuthorities { get; init; }

    public IReadOnlyList<X509Certificate2> IntermediateCertificates { get; init; } = [];

    public Func<TimeProvider, QuicMeshCertificates>? CertificateRotator { get; init; }

    public QuicMeshOptions Validate(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        QuicMeshIdentity.EnsureValidSegment(NodeId, nameof(NodeId));
        QuicMeshIdentity.EnsureValidSegment(Region, nameof(Region));
        ArgumentNullException.ThrowIfNull(ListenEndPoint);
        ArgumentNullException.ThrowIfNull(Certificate);
        ArgumentNullException.ThrowIfNull(TrustedAuthorities);
        ArgumentNullException.ThrowIfNull(IntermediateCertificates);
        ArgumentOutOfRangeException.ThrowIfZero(TrustedAuthorities.Count, nameof(TrustedAuthorities));
        if (!Certificate.HasPrivateKey)
        {
            throw new ArgumentException("The mesh certificate must carry its private key.");
        }

        var validator = new QuicCertificateValidator(TrustedAuthorities, timeProvider);
        if (validator.Validate(Certificate, IntermediateCertificates) is not { } identity
            || !string.Equals(identity.NodeId, NodeId, StringComparison.Ordinal)
            || !string.Equals(identity.Region, Region, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The mesh certificate must chain to a trusted authority, be valid now, carry common name '{NodeId}' and the URI SAN '{QuicMeshIdentity.UriFor(NodeId, Region)}'.");
        }

        return this;
    }
}
