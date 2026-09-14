namespace TerraBase.Contracts;

using System.Collections.Generic;
using MessagePack;

/// <summary>A resumable position in one database's publication journal.</summary>
[MessagePackObject]
public sealed record ReplicationCheckpoint
{
    /// <summary>Database identity.</summary>
    [Key(0)]
    public string DatabaseUlid { get; init; } = string.Empty;

    /// <summary>Source member identity that minted the checkpoint.</summary>
    [Key(1)]
    public string SourceNodeId { get; init; } = string.Empty;

    /// <summary>Source replica epoch that fences stale cursors.</summary>
    [Key(2)]
    public ulong SourceReplicaEpoch { get; init; }

    /// <summary>Highest contiguous sequence by publication origin.</summary>
    [Key(3)]
    public Dictionary<string, ulong> Frontier { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Opaque source cursor for a partial leaf or chunk.</summary>
    [Key(4)]
    public string? Cursor { get; init; }

    /// <summary>Digest of the canonical checkpoint fields.</summary>
    [Key(5)]
    public byte[]? Digest { get; init; }
}

/// <summary>One canonical publication shipped between members.</summary>
[MessagePackObject]
public sealed record ReplicationPublication
{
    /// <summary>Owning tenant ULID.</summary>
    [Key(0)]
    public string TenantUlid { get; init; } = string.Empty;

    /// <summary>Collection identity.</summary>
    [Key(1)]
    public string Collection { get; init; } = string.Empty;

    /// <summary>Document identity in the collection.</summary>
    [Key(2)]
    public string DocumentId { get; init; } = string.Empty;

    /// <summary>Origin member or actor identity.</summary>
    [Key(3)]
    public string Origin { get; init; } = string.Empty;

    /// <summary>Monotonic sequence in the origin journal.</summary>
    [Key(4)]
    public ulong Sequence { get; init; }

    /// <summary>Dotted causal frontier required before applying this publication.</summary>
    [Key(5)]
    public Dictionary<string, ulong> Context { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Hybrid logical clock value.</summary>
    [Key(6)]
    public long Hlc { get; init; }

    /// <summary>Whether this publication is a tombstone.</summary>
    [Key(7)]
    public bool Deleted { get; init; }

    /// <summary>Canonical JSON or native document bytes; absent for tombstones.</summary>
    [Key(8)]
    public byte[]? CanonicalBody { get; init; }

    /// <summary>Stable commit identity, when one exists.</summary>
    [Key(9)]
    public string? CommitUlid { get; init; }
}

/// <summary>Requests journal publications after a resumable checkpoint.</summary>
[MessagePackObject]
public sealed record ReplicationPullRequest : InterNodeRequest
{
    /// <summary>Database to read.</summary>
    [Key(0)]
    public string DatabaseUlid { get; init; } = string.Empty;

    /// <summary>Last confirmed position; null starts at the source's retained floor.</summary>
    [Key(1)]
    public ReplicationCheckpoint? ResumeFrom { get; init; }

    /// <summary>Maximum number of publications in this response.</summary>
    [Key(2)]
    public int MaxPublications { get; init; } = 1_000;

    /// <summary>Maximum decoded body bytes in this response.</summary>
    [Key(3)]
    public int MaxBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Whether tombstone publications should be included.</summary>
    [Key(4)]
    public bool IncludeTombstones { get; init; } = true;
}

/// <summary>Response containing an ordered, resumable publication batch.</summary>
[MessagePackObject]
public sealed record ReplicationPullResponse : InterNodeResponse
{
    /// <summary>Publications in origin and sequence order.</summary>
    [Key(0)]
    public ReplicationPublication[] Publications { get; init; } = [];

    /// <summary>Checkpoint that becomes durable only after all publications are accepted.</summary>
    [Key(1)]
    public ReplicationCheckpoint NextCheckpoint { get; init; } = new();

    /// <summary>Whether the retained journal has no more rows at this cut.</summary>
    [Key(2)]
    public bool EndOfStream { get; init; }

    /// <summary>Rows deferred because their causal dependencies are absent.</summary>
    [Key(3)]
    public ReplicationRepairHint[] Deferred { get; init; } = [];
}

/// <summary>Pushes publications to a receiving member.</summary>
[MessagePackObject]
public sealed record ReplicationPushRequest : InterNodeRequest
{
    /// <summary>Database receiving the publications.</summary>
    [Key(0)]
    public string DatabaseUlid { get; init; } = string.Empty;

    /// <summary>Publications to apply in this bounded batch.</summary>
    [Key(1)]
    public ReplicationPublication[] Publications { get; init; } = [];

    /// <summary>Last source checkpoint the sender has durably confirmed.</summary>
    [Key(2)]
    public ReplicationCheckpoint? ResumeFrom { get; init; }

    /// <summary>Whether this is the final batch at the current source cut.</summary>
    [Key(3)]
    public bool EndOfBatch { get; init; }
}

/// <summary>Outcome for one pushed publication.</summary>
public enum ReplicationApplyOutcome : byte
{
    /// <summary>The publication was applied.</summary>
    Applied = 0,

    /// <summary>The publication caused a deterministic merge.</summary>
    Merged = 1,

    /// <summary>The publication was already observed.</summary>
    Duplicate = 2,

    /// <summary>The publication awaits a missing causal dependency.</summary>
    Deferred = 3,
}

/// <summary>Result of applying one publication.</summary>
[MessagePackObject]
public sealed record ReplicationPublicationResult
{
    /// <summary>Application outcome.</summary>
    [Key(0)]
    public ReplicationApplyOutcome Outcome { get; init; }

    /// <summary>Publication origin.</summary>
    [Key(1)]
    public string Origin { get; init; } = string.Empty;

    /// <summary>Publication sequence.</summary>
    [Key(2)]
    public ulong Sequence { get; init; }
}

/// <summary>Response to a pushed batch.</summary>
[MessagePackObject]
public sealed record ReplicationPushResponse : InterNodeResponse
{
    /// <summary>Per-publication application outcomes.</summary>
    [Key(0)]
    public ReplicationPublicationResult[] Results { get; init; } = [];

    /// <summary>Receiver checkpoint after durable acceptance.</summary>
    [Key(1)]
    public ReplicationCheckpoint Checkpoint { get; init; } = new();

    /// <summary>Receiver frontier by origin.</summary>
    [Key(2)]
    public Dictionary<string, ulong> AppliedFrontier { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Repair hints for deferred dependencies.</summary>
    [Key(3)]
    public ReplicationRepairHint[] Deferred { get; init; } = [];
}

/// <summary>One sequence interval considered by range reconciliation.</summary>
[MessagePackObject]
public sealed record ReplicationRange
{
    /// <summary>Publication origin.</summary>
    [Key(0)]
    public string Origin { get; init; } = string.Empty;

    /// <summary>Inclusive lower sequence bound.</summary>
    [Key(1)]
    public ulong StartInclusive { get; init; }

    /// <summary>Exclusive upper sequence bound; zero means an open end.</summary>
    [Key(2)]
    public ulong EndExclusive { get; init; }
}

/// <summary>Range fingerprint exchanged by anti-entropy.</summary>
[MessagePackObject]
public sealed record ReplicationFingerprint
{
    /// <summary>Range summarized by the fingerprint.</summary>
    [Key(0)]
    public ReplicationRange Range { get; init; } = new();

    /// <summary>Additive 128-bit fingerprint in little-endian bytes.</summary>
    [Key(1)]
    public byte[] Value { get; init; } = [];

    /// <summary>Number of publications represented by the range.</summary>
    [Key(2)]
    public ulong Count { get; init; }
}

/// <summary>Identifies a publication missing from a receiver.</summary>
[MessagePackObject]
public sealed record ReplicationRepairHint
{
    /// <summary>Publication origin.</summary>
    [Key(0)]
    public string Origin { get; init; } = string.Empty;

    /// <summary>Missing sequence.</summary>
    [Key(1)]
    public ulong Sequence { get; init; }
}

/// <summary>Starts or continues range-based set reconciliation.</summary>
[MessagePackObject]
public sealed record ReplicationReconcileRequest : InterNodeRequest
{
    /// <summary>Database being reconciled.</summary>
    [Key(0)]
    public string DatabaseUlid { get; init; } = string.Empty;

    /// <summary>Ranges to fingerprint or refine.</summary>
    [Key(1)]
    public ReplicationRange[] Ranges { get; init; } = [];

    /// <summary>Last confirmed checkpoint for resumable refinement.</summary>
    [Key(2)]
    public ReplicationCheckpoint? ResumeFrom { get; init; }

    /// <summary>Maximum number of range entries in the response.</summary>
    [Key(3)]
    public int MaxRanges { get; init; } = 256;
}

/// <summary>Range fingerprints and leaf repair hints.</summary>
[MessagePackObject]
public sealed record ReplicationReconcileResponse : InterNodeResponse
{
    /// <summary>Fingerprints for requested ranges.</summary>
    [Key(0)]
    public ReplicationFingerprint[] Fingerprints { get; init; } = [];

    /// <summary>Leaf identities that differ and need publication transfer.</summary>
    [Key(1)]
    public ReplicationRepairHint[] Missing { get; init; } = [];

    /// <summary>Checkpoint for the next reconciliation request.</summary>
    [Key(2)]
    public ReplicationCheckpoint NextCheckpoint { get; init; } = new();

    /// <summary>Whether all requested ranges matched.</summary>
    [Key(3)]
    public bool Equal { get; init; }
}

/// <summary>Requests a bounded payload chunk at a resumable byte offset.</summary>
[MessagePackObject]
public sealed record ReplicationChunkRequest : InterNodeRequest
{
    /// <summary>Database being transferred.</summary>
    [Key(0)]
    public string DatabaseUlid { get; init; } = string.Empty;

    /// <summary>Transfer identity.</summary>
    [Key(1)]
    public string TransferId { get; init; } = string.Empty;

    /// <summary>Byte offset already durably written by the receiver.</summary>
    [Key(2)]
    public long Offset { get; init; }

    /// <summary>Maximum bytes requested for this chunk.</summary>
    [Key(3)]
    public int MaxBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Checkpoint associated with the transfer.</summary>
    [Key(4)]
    public ReplicationCheckpoint? ResumeFrom { get; init; }
}

/// <summary>Bounded transfer chunk with an explicit checksum and next cursor.</summary>
[MessagePackObject]
public sealed record ReplicationChunkResponse : InterNodeResponse
{
    /// <summary>Transfer identity.</summary>
    [Key(0)]
    public string TransferId { get; init; } = string.Empty;

    /// <summary>Byte offset represented by <see cref="Payload"/>.</summary>
    [Key(1)]
    public long Offset { get; init; }

    /// <summary>Chunk bytes.</summary>
    [Key(2)]
    public byte[] Payload { get; init; } = [];

    /// <summary>BLAKE3-256 digest of the chunk bytes.</summary>
    [Key(3)]
    public byte[] Digest { get; init; } = [];

    /// <summary>Offset requested for the next chunk.</summary>
    [Key(4)]
    public long NextOffset { get; init; }

    /// <summary>Whether this chunk completes the transfer.</summary>
    [Key(5)]
    public bool Completed { get; init; }

    /// <summary>Checkpoint to persist after accepting this chunk.</summary>
    [Key(6)]
    public ReplicationCheckpoint? Checkpoint { get; init; }
}
