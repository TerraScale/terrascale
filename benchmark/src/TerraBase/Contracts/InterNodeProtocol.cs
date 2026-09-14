namespace TerraBase.Contracts;

using System;
using MessagePack;

/// <summary>Stable constants for the private TerraBase member-to-member protocol.</summary>
public static class InterNodeProtocol
{
    /// <summary>The major version of the MessagePack contract.</summary>
    public const byte MajorVersion = 1;

    /// <summary>The additive minor version of the MessagePack contract.</summary>
    public const byte MinorVersion = 0;

    /// <summary>The codec identifier carried by protocol diagnostics and capability exchange.</summary>
    public const string Codec = "terrabase-inter-node/1";

    /// <summary>The largest decoded request or response body accepted by the member channel.</summary>
    public const int MaximumFrameBytes = 16 * 1024 * 1024;

    /// <summary>Returns whether a peer can safely decode a contract version.</summary>
    public static bool Supports(byte majorVersion, byte minorVersion) =>
        majorVersion == MajorVersion && minorVersion <= MinorVersion;
}

/// <summary>Operation code for one inter-node RPC envelope.</summary>
public enum InterNodeOperation : byte
{
    /// <summary>Negotiates protocol and node capabilities.</summary>
    Handshake = 1,

    /// <summary>Exchanges liveness and applied-frontier information.</summary>
    Heartbeat = 2,

    /// <summary>Requests cancellation of an in-flight operation.</summary>
    Cancel = 3,

    /// <summary>Looks up a durable idempotency outcome.</summary>
    IdempotencyLookup = 4,

    /// <summary>Requests a member to stop accepting new work.</summary>
    Drain = 5,

    /// <summary>Pulls journal publications after a resumable checkpoint.</summary>
    ReplicationPull = 16,

    /// <summary>Pushes journal publications to a peer.</summary>
    ReplicationPush = 17,

    /// <summary>Starts or continues range-based reconciliation.</summary>
    ReplicationReconcile = 18,

    /// <summary>Transfers a bounded resumable replication payload chunk.</summary>
    ReplicationChunk = 19,
}

/// <summary>Stable state returned by a member for an RPC request.</summary>
public enum InterNodeResponseStatus : byte
{
    /// <summary>The operation completed successfully.</summary>
    Succeeded = 0,

    /// <summary>The operation was accepted but remains in progress.</summary>
    Accepted = 1,

    /// <summary>The operation was rejected before it changed durable state.</summary>
    Rejected = 2,

    /// <summary>The operation was canceled.</summary>
    Canceled = 3,

    /// <summary>The operation exceeded its deadline.</summary>
    DeadlineExceeded = 4,
}

/// <summary>
/// Request envelope. Keys are append-only: a newer minor version may add keys
/// while an older peer continues to decode the required prefix.
/// </summary>
[MessagePackObject]
public sealed record InterNodeRequestEnvelope
{
    /// <summary>The protocol major version.</summary>
    [Key(0)]
    public byte MajorVersion { get; init; } = InterNodeProtocol.MajorVersion;

    /// <summary>The protocol minor version.</summary>
    [Key(1)]
    public byte MinorVersion { get; init; } = InterNodeProtocol.MinorVersion;

    /// <summary>The operation selected by the payload.</summary>
    [Key(2)]
    public InterNodeOperation Operation { get; init; }

    /// <summary>Request identity, deadline, cancellation, and retry metadata.</summary>
    [Key(3)]
    public RpcRequestMetadata Metadata { get; init; } = new();

    /// <summary>The typed operation body.</summary>
    [Key(4)]
    public InterNodeRequest Payload { get; init; } = new EmptyRequest();
}

/// <summary>Response envelope for one inter-node RPC request.</summary>
[MessagePackObject]
public sealed record InterNodeResponseEnvelope
{
    /// <summary>The protocol major version.</summary>
    [Key(0)]
    public byte MajorVersion { get; init; } = InterNodeProtocol.MajorVersion;

    /// <summary>The protocol minor version.</summary>
    [Key(1)]
    public byte MinorVersion { get; init; } = InterNodeProtocol.MinorVersion;

    /// <summary>The operation selected by the payload.</summary>
    [Key(2)]
    public InterNodeOperation Operation { get; init; }

    /// <summary>Response status and the request correlation identity.</summary>
    [Key(3)]
    public RpcResponseMetadata Metadata { get; init; } = new();

    /// <summary>The typed operation body.</summary>
    [Key(4)]
    public InterNodeResponse Payload { get; init; } = new EmptyResponse();
}

/// <summary>Base type for all private inter-node request bodies.</summary>
[Union(0, typeof(EmptyRequest))]
[Union(1, typeof(ControlHandshakeRequest))]
[Union(2, typeof(ControlHeartbeatRequest))]
[Union(3, typeof(ControlCancelRequest))]
[Union(4, typeof(ControlDrainRequest))]
[Union(5, typeof(IdempotencyLookupRequest))]
[Union(16, typeof(ReplicationPullRequest))]
[Union(17, typeof(ReplicationPushRequest))]
[Union(18, typeof(ReplicationReconcileRequest))]
[Union(19, typeof(ReplicationChunkRequest))]
public abstract record InterNodeRequest;

/// <summary>Base type for all private inter-node response bodies.</summary>
[Union(0, typeof(EmptyResponse))]
[Union(1, typeof(ControlHandshakeResponse))]
[Union(2, typeof(ControlHeartbeatResponse))]
[Union(3, typeof(ControlCancelResponse))]
[Union(4, typeof(ControlDrainResponse))]
[Union(5, typeof(IdempotencyLookupResponse))]
[Union(16, typeof(ReplicationPullResponse))]
[Union(17, typeof(ReplicationPushResponse))]
[Union(18, typeof(ReplicationReconcileResponse))]
[Union(19, typeof(ReplicationChunkResponse))]
[Union(255, typeof(InterNodeErrorResponse))]
public abstract record InterNodeResponse;

/// <summary>Empty body used by extensible control acknowledgements.</summary>
[MessagePackObject]
public sealed record EmptyRequest : InterNodeRequest;

/// <summary>Empty response body.</summary>
[MessagePackObject]
public sealed record EmptyResponse : InterNodeResponse;

/// <summary>Metadata shared by all request envelopes.</summary>
[MessagePackObject]
public sealed record RpcRequestMetadata
{
    /// <summary>Unique request identity used for correlation and cancellation.</summary>
    [Key(0)]
    public string RequestId { get; init; } = string.Empty;

    /// <summary>Authenticated source member identity.</summary>
    [Key(1)]
    public string SourceNodeId { get; init; } = string.Empty;

    /// <summary>Absolute Unix epoch deadline in milliseconds; zero means no deadline.</summary>
    [Key(2)]
    public long DeadlineUnixMilliseconds { get; init; }

    /// <summary>Optional cancellation identity registered for this request.</summary>
    [Key(3)]
    public string? CancellationId { get; init; }

    /// <summary>Mutation retry identity and canonical request digest.</summary>
    [Key(4)]
    public IdempotencyMetadata? Idempotency { get; init; }

    /// <summary>Optional W3C trace context bytes; no customer data belongs here.</summary>
    [Key(5)]
    public byte[]? TraceContext { get; init; }

    /// <summary>Whether the deadline has elapsed at the supplied instant.</summary>
    public bool IsExpired(DateTimeOffset now) =>
        DeadlineUnixMilliseconds != 0
        && now.ToUnixTimeMilliseconds() >= DeadlineUnixMilliseconds;

    /// <summary>Returns the deadline, or <see langword="null"/> when unbounded.</summary>
    public DateTimeOffset? Deadline =>
        DeadlineUnixMilliseconds == 0
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(DeadlineUnixMilliseconds);
}

/// <summary>Metadata shared by all response envelopes.</summary>
[MessagePackObject]
public sealed record RpcResponseMetadata
{
    /// <summary>Request identity being answered.</summary>
    [Key(0)]
    public string RequestId { get; init; } = string.Empty;

    /// <summary>Response status.</summary>
    [Key(1)]
    public InterNodeResponseStatus Status { get; init; }

    /// <summary>Member time at which the response was produced.</summary>
    [Key(2)]
    public long RespondedAtUnixMilliseconds { get; init; }

    /// <summary>Whether cancellation was observed while producing the response.</summary>
    [Key(3)]
    public bool CancellationObserved { get; init; }

    /// <summary>Optional retry delay in milliseconds for a transient rejection.</summary>
    [Key(4)]
    public long? RetryAfterMilliseconds { get; init; }
}

/// <summary>Wire-level cancellation reason.</summary>
public enum CancellationReason : byte
{
    /// <summary>The caller explicitly canceled the request.</summary>
    CallerRequested = 0,

    /// <summary>The request deadline elapsed.</summary>
    DeadlineExceeded = 1,

    /// <summary>The member is shutting down or draining.</summary>
    MemberStopping = 2,

    /// <summary>The request was superseded by a newer operation.</summary>
    Superseded = 3,
}
