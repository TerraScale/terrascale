namespace TerraBase.Contracts;

using MessagePack;

/// <summary>Retry identity and digest attached to a mutating inter-node request.</summary>
[MessagePackObject]
public sealed record IdempotencyMetadata
{
    /// <summary>Owning tenant ULID.</summary>
    [Key(0)]
    public string TenantUlid { get; init; } = string.Empty;

    /// <summary>Owning database ULID.</summary>
    [Key(1)]
    public string DatabaseUlid { get; init; } = string.Empty;

    /// <summary>Stable client request ULID.</summary>
    [Key(2)]
    public string ClientRequestUlid { get; init; } = string.Empty;

    /// <summary>BLAKE3-256 digest of the canonical request body.</summary>
    [Key(3)]
    public byte[] RequestDigest { get; init; } = [];
}

/// <summary>Durable state of an idempotent mutation.</summary>
public enum IdempotencyOutcomeState : byte
{
    /// <summary>The operation has been admitted but has not reached a terminal outcome.</summary>
    InProgress = 0,

    /// <summary>The operation committed successfully.</summary>
    Applied = 1,

    /// <summary>The operation was rejected by a precondition or policy.</summary>
    Rejected = 2,

    /// <summary>The operation was canceled before commit.</summary>
    Canceled = 3,

    /// <summary>The request key was reused with another request digest.</summary>
    Conflict = 4,

    /// <summary>The complete response was retained and can be replayed.</summary>
    Replayable = 5,

    /// <summary>The result expired but its commit identity remains discoverable.</summary>
    ResultExpired = 6,

    /// <summary>The request identity aged beyond the admission floor.</summary>
    Expired = 7,
}

/// <summary>Durable idempotency tuple retained by a member.</summary>
[MessagePackObject]
public sealed record IdempotencyRecord
{
    /// <summary>Owning tenant ULID.</summary>
    [Key(0)]
    public string TenantUlid { get; init; } = string.Empty;

    /// <summary>Owning database ULID.</summary>
    [Key(1)]
    public string DatabaseUlid { get; init; } = string.Empty;

    /// <summary>Stable client request ULID.</summary>
    [Key(2)]
    public string ClientRequestUlid { get; init; } = string.Empty;

    /// <summary>Digest of the first canonical request.</summary>
    [Key(3)]
    public byte[] RequestDigest { get; init; } = [];

    /// <summary>Commit ULID, when a commit was minted.</summary>
    [Key(4)]
    public string? CommitUlid { get; init; }

    /// <summary>Terminal or in-progress outcome.</summary>
    [Key(5)]
    public IdempotencyOutcomeState Outcome { get; init; }

    /// <summary>Digest of the replayable response, when retained.</summary>
    [Key(6)]
    public byte[]? ResponseDigest { get; init; }

    /// <summary>Server-controlled retention deadline in Unix epoch milliseconds.</summary>
    [Key(7)]
    public long RetentionDeadlineUnixMilliseconds { get; init; }

    /// <summary>Typed outcome detail safe for a peer to use during retry.</summary>
    [Key(8)]
    public string? OutcomeCode { get; init; }
}

/// <summary>Reads a durable idempotency outcome without executing the mutation.</summary>
[MessagePackObject]
public sealed record IdempotencyLookupRequest : InterNodeRequest
{
    /// <summary>Retry identity being looked up.</summary>
    [Key(0)]
    public IdempotencyMetadata Identity { get; init; } = new();
}

/// <summary>Result of a durable idempotency lookup.</summary>
[MessagePackObject]
public sealed record IdempotencyLookupResponse : InterNodeResponse
{
    /// <summary>The durable record, or <see langword="null"/> when no record exists.</summary>
    [Key(0)]
    public IdempotencyRecord? Record { get; init; }
}
