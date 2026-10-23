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

    /// <summary>
    /// The newest peer stream protocol this build speaks, sent in <c>Hello</c> and <c>Welcome</c>; a stream uses the lower of
    /// the two, and a peer that sends none speaks 1. 1: one batch per database in flight. 2: up to <see cref="Window"/>
    /// batches per database in flight, chained by <c>Batch.after</c>.
    /// </summary>
    public const uint Version = 2;

    /// <summary>Batches of one database in flight on a protocol 2 stream (each still acked only once durable remotely).</summary>
    public const int Window = 8;

    /// <summary>The protocol a stream uses with a peer that announced <paramref name="announced"/> (0: none).</summary>
    public static uint Negotiate(uint announced) => Math.Min(Version, Math.Max(1, announced));

    /// <summary>A sender abandons a stream on which nothing it waits for arrives for this long.</summary>
    public static TimeSpan ProgressTimeout { get; } = TimeSpan.FromSeconds(15);
}
