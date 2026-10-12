namespace TerraBase.Features.Replication;

/// <summary>
/// Limits of the peer listener's gRPC streams (the catalog feed, forwarding and usage). Every payload travels in an LZ4
/// <see cref="ApplicationEnvelope"/>; gRPC transport compression stays off.
/// </summary>
public static class ReplicationProtocol
{
    /// <summary>Largest gRPC message in either direction: one application batch plus Protobuf framing.</summary>
    public const int MaxMessageBytes = MaxApplicationBytes + (64 * 1024);

    /// <summary>Largest envelope and largest decoded change group.</summary>
    public const int MaxApplicationBytes = 4 * 1024 * 1024;

    /// <summary>A sender abandons a stream on which nothing it waits for arrives for this long.</summary>
    public static TimeSpan ProgressTimeout { get; } = TimeSpan.FromSeconds(15);
}
