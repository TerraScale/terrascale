namespace TerraBase.Contracts;

using System.Collections.Generic;
using MessagePack;

/// <summary>Control-plane request used to negotiate a peer connection.</summary>
[MessagePackObject]
public sealed record ControlHandshakeRequest : InterNodeRequest
{
    /// <summary>Member identity presenting the handshake.</summary>
    [Key(0)]
    public string NodeId { get; init; } = string.Empty;

    /// <summary>Region identity presenting the handshake.</summary>
    [Key(1)]
    public string RegionCode { get; init; } = string.Empty;

    /// <summary>Current replica epoch; changes fence stale senders.</summary>
    [Key(2)]
    public ulong ReplicaEpoch { get; init; }

    /// <summary>Highest supported protocol minor version.</summary>
    [Key(3)]
    public byte MaximumMinorVersion { get; init; } = InterNodeProtocol.MinorVersion;

    /// <summary>Capabilities understood by this member.</summary>
    [Key(4)]
    public string[] Capabilities { get; init; } = [];
}

/// <summary>Control-plane response to a handshake.</summary>
[MessagePackObject]
public sealed record ControlHandshakeResponse : InterNodeResponse
{
    /// <summary>Whether this peer accepted the connection.</summary>
    [Key(0)]
    public bool Accepted { get; init; }

    /// <summary>Negotiated protocol minor version.</summary>
    [Key(1)]
    public byte NegotiatedMinorVersion { get; init; }

    /// <summary>Remote member identity.</summary>
    [Key(2)]
    public string NodeId { get; init; } = string.Empty;

    /// <summary>Remote replica epoch.</summary>
    [Key(3)]
    public ulong ReplicaEpoch { get; init; }

    /// <summary>Capabilities selected for this connection.</summary>
    [Key(4)]
    public string[] Capabilities { get; init; } = [];

    /// <summary>Machine-readable rejection reason.</summary>
    [Key(5)]
    public string? RejectionCode { get; init; }
}

/// <summary>Control-plane liveness and frontier exchange.</summary>
[MessagePackObject]
public sealed record ControlHeartbeatRequest : InterNodeRequest
{
    /// <summary>Member identity sending the heartbeat.</summary>
    [Key(0)]
    public string NodeId { get; init; } = string.Empty;

    /// <summary>Current replica epoch.</summary>
    [Key(1)]
    public ulong ReplicaEpoch { get; init; }

    /// <summary>Highest contiguous journal sequence by origin.</summary>
    [Key(2)]
    public Dictionary<string, ulong> AppliedFrontier { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Unix epoch milliseconds at the sender.</summary>
    [Key(3)]
    public long SentAtUnixMilliseconds { get; init; }
}

/// <summary>Control-plane heartbeat response.</summary>
[MessagePackObject]
public sealed record ControlHeartbeatResponse : InterNodeResponse
{
    /// <summary>Whether the sender remains an admitted peer.</summary>
    [Key(0)]
    public bool Accepted { get; init; }

    /// <summary>Current replica epoch.</summary>
    [Key(1)]
    public ulong ReplicaEpoch { get; init; }

    /// <summary>Receiver frontier by origin.</summary>
    [Key(2)]
    public Dictionary<string, ulong> AppliedFrontier { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Optional reason for fencing a peer.</summary>
    [Key(3)]
    public string? RejectionCode { get; init; }
}

/// <summary>Requests cancellation of a previously admitted operation.</summary>
[MessagePackObject]
public sealed record ControlCancelRequest : InterNodeRequest
{
    /// <summary>Request identity to cancel.</summary>
    [Key(0)]
    public string TargetRequestId { get; init; } = string.Empty;

    /// <summary>Stable cancellation identity.</summary>
    [Key(1)]
    public string CancellationId { get; init; } = string.Empty;

    /// <summary>Reason supplied by the caller.</summary>
    [Key(2)]
    public CancellationReason Reason { get; init; }
}

/// <summary>Result of a cancellation request.</summary>
[MessagePackObject]
public sealed record ControlCancelResponse : InterNodeResponse
{
    /// <summary>Request identity targeted by the cancellation.</summary>
    [Key(0)]
    public string TargetRequestId { get; init; } = string.Empty;

    /// <summary>Whether cancellation was registered by the owner.</summary>
    [Key(1)]
    public bool Accepted { get; init; }

    /// <summary>Whether the operation had already reached a terminal state.</summary>
    [Key(2)]
    public bool AlreadyTerminal { get; init; }
}

/// <summary>Asks a peer to stop admitting new work and drain existing work.</summary>
[MessagePackObject]
public sealed record ControlDrainRequest : InterNodeRequest
{
    /// <summary>Whether the peer should reject new requests immediately.</summary>
    [Key(0)]
    public bool RejectNewRequests { get; init; } = true;

    /// <summary>Optional Unix epoch deadline by which the drain should finish.</summary>
    [Key(1)]
    public long? DrainDeadlineUnixMilliseconds { get; init; }
}

/// <summary>Result of a drain request.</summary>
[MessagePackObject]
public sealed record ControlDrainResponse : InterNodeResponse
{
    /// <summary>Whether the member entered draining mode.</summary>
    [Key(0)]
    public bool Accepted { get; init; }

    /// <summary>Number of operations still in flight at acknowledgement.</summary>
    [Key(1)]
    public int InFlightOperations { get; init; }
}

/// <summary>Typed fail-closed error returned by an inter-node RPC.</summary>
[MessagePackObject]
public sealed record InterNodeErrorResponse : InterNodeResponse
{
    /// <summary>Stable machine-readable error code.</summary>
    [Key(0)]
    public string Code { get; init; } = string.Empty;

    /// <summary>Whether retrying the same request identity is safe.</summary>
    [Key(1)]
    public bool Retryable { get; init; }

    /// <summary>Whether the owner may have committed before the failure was observed.</summary>
    [Key(2)]
    public bool UnknownOutcome { get; init; }

    /// <summary>Optional safe diagnostic detail.</summary>
    [Key(3)]
    public string? Detail { get; init; }
}
