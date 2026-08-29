using System.Buffers.Binary;
using System.Text;

namespace TerraScale.DotNet;

/// <summary>
/// The typed rejection of the <c>terrascale.sync.v2</c> codec. The wire
/// names are the closed vocabulary the golden vectors record:
/// <c>unsupported_version</c>, <c>malformed_frame</c>,
/// <c>idempotency_pairing</c>, and <c>invalid_idempotency_key</c>.
/// </summary>
public enum TerraScaleSyncV2ProtocolError
{
    /// <summary>The frame's leading version byte is not the v2 wire version.</summary>
    UnsupportedVersion,

    /// <summary>The frame is structurally invalid: truncation, trailing bytes, length disagreement, unknown kinds, violated limits.</summary>
    MalformedFrame,

    /// <summary>The idempotency-key pairing is wrong: a mutation frame without a key, or a non-mutation frame with one.</summary>
    IdempotencyPairing,

    /// <summary>An idempotency key is not constructible (empty, whitespace-only, control characters, or over its bound).</summary>
    InvalidIdempotencyKey,
}

/// <summary>
/// The fail-closed rejection of the <c>terrascale.sync.v2</c> codec.
/// </summary>
public sealed class TerraScaleSyncV2ProtocolException : Exception
{
    /// <summary>Initializes the rejection.</summary>
    /// <param name="error">The typed error.</param>
    /// <param name="reason">The exact reason text.</param>
    public TerraScaleSyncV2ProtocolException(TerraScaleSyncV2ProtocolError error, string reason)
        : base(reason)
    {
        Error = error;
        Reason = reason;
    }

    /// <summary>The typed error.</summary>
    public TerraScaleSyncV2ProtocolError Error { get; }

    /// <summary>The exact reason text.</summary>
    public string Reason { get; }

    /// <summary>The stable wire name of the typed error (the name the golden vectors record).</summary>
    public string WireName => Error switch
    {
        TerraScaleSyncV2ProtocolError.UnsupportedVersion => "unsupported_version",
        TerraScaleSyncV2ProtocolError.MalformedFrame => "malformed_frame",
        TerraScaleSyncV2ProtocolError.IdempotencyPairing => "idempotency_pairing",
        TerraScaleSyncV2ProtocolError.InvalidIdempotencyKey => "invalid_idempotency_key",
        _ => "malformed_frame",
    };
}

/// <summary>
/// The <c>terrascale.sync.v2</c> wire-protocol constants (SE-04 wire
/// spec): contract identity, wire version, the fixed envelope cost, the
/// decode and field limits, and the written per-class bytes-on-wire
/// budgets (PD-2026-08-26-13 D1). The budget numbers are written
/// definitions, never measured claims.
/// </summary>
public static class TerraScaleSyncV2Protocol
{
    /// <summary>The contract identity of the protocol.</summary>
    public const string Contract = "terrascale.sync.v2";

    /// <summary>The wire version: the leading byte of every frame. Every other value fails closed, including v1.</summary>
    public const byte WireVersion = 2;

    /// <summary>The fixed per-frame envelope overhead in bytes: version, kind, correlation, causation, presence, digest, body length, and the body's kind tag.</summary>
    public const int FixedEnvelopeBytes = 38;

    /// <summary>The largest frame the decoder accepts; larger inputs are rejected before any field is parsed.</summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    /// <summary>The largest encoded payload one delivery frame may carry.</summary>
    public const int MaxPayloadBytes = MaxFrameBytes - FixedEnvelopeBytes;

    /// <summary>The longest idempotency key or operation identity, in UTF-8 bytes.</summary>
    public const int MaxKeyBytes = 128;

    /// <summary>The longest identifier string a frame carries, in UTF-8 bytes.</summary>
    public const int MaxIdentBytes = 256;

    /// <summary>The longest trigger collection or operation identity, in UTF-8 bytes.</summary>
    public const int MaxTriggerIdentBytes = 64;

    /// <summary>The most entries one collection list carries.</summary>
    public const int MaxCollections = 256;

    /// <summary>The most fields one mutation body carries.</summary>
    public const int MaxFields = 1024;

    /// <summary>The longest error detail string, in UTF-8 bytes.</summary>
    public const int MaxDetailBytes = 256;

    /// <summary>The written budget of a frame class, in wire bytes.</summary>
    /// <param name="frameClass">The frame class.</param>
    /// <returns>The written budget in bytes.</returns>
    public static int BudgetBytes(TerraScaleSyncV2FrameClass frameClass) => frameClass switch
    {
        TerraScaleSyncV2FrameClass.Session => 4096,
        TerraScaleSyncV2FrameClass.Subscription => 2048,
        TerraScaleSyncV2FrameClass.Acknowledgement => 1024,
        TerraScaleSyncV2FrameClass.Heartbeat => 64,
        TerraScaleSyncV2FrameClass.Delivery => 64 * 1024,
        TerraScaleSyncV2FrameClass.Trigger => 256,
        TerraScaleSyncV2FrameClass.Control => 256,
        TerraScaleSyncV2FrameClass.Mutation => 64 * 1024,
        _ => throw new ArgumentOutOfRangeException(nameof(frameClass)),
    };
}

/// <summary>The registry identity of one frame kind. The codes are stable wire values; unknown codes fail closed on decode.</summary>
public enum TerraScaleSyncV2FrameKind
{
    /// <summary>Client: open a session, fresh or resuming from a checkpoint. Wire code <c>0x0001</c>.</summary>
    SessionSetup = 0x0001,

    /// <summary>Server: the session is accepted; states the server checkpoint. Wire code <c>0x0002</c>.</summary>
    SessionAccepted = 0x0002,

    /// <summary>Client: modify the session's collection subscriptions. Wire code <c>0x0003</c>.</summary>
    SubscriptionModify = 0x0003,

    /// <summary>Server: the subscription set now in force. Wire code <c>0x0004</c>.</summary>
    SubscriptionChanged = 0x0004,

    /// <summary>Client: acknowledge the session's durable resumption position. Wire code <c>0x0005</c>.</summary>
    CheckpointAck = 0x0005,

    /// <summary>Either direction: liveness probe with no payload. Wire code <c>0x0006</c>.</summary>
    Heartbeat = 0x0006,

    /// <summary>Server: one byte-budgeted delta batch. Wire code <c>0x0007</c>.</summary>
    DeltaBatch = 0x0007,

    /// <summary>Server: one full-state answer (initial sync or forced resync). Wire code <c>0x0008</c>.</summary>
    SnapshotAnswer = 0x0008,

    /// <summary>Server: a content-free trigger notification (push the hint, pull the delta). Wire code <c>0x0009</c>.</summary>
    Trigger = 0x0009,

    /// <summary>Server: the session fell behind the retained floor; resync. Wire code <c>0x000A</c>.</summary>
    ResyncRequired = 0x000A,

    /// <summary>Server: the serving region changed; re-resolve and resume. Wire code <c>0x000B</c>.</summary>
    RegionRouteChange = 0x000B,

    /// <summary>Server: a fatal error from the closed error vocabulary. Wire code <c>0x000C</c>.</summary>
    Error = 0x000C,

    /// <summary>Client: a typed mutation request carrying an idempotency key. Wire code <c>0x000D</c>.</summary>
    MutationRequest = 0x000D,

    /// <summary>Server: the acknowledgement of a mutation (the only answer). Wire code <c>0x000E</c>.</summary>
    MutationAck = 0x000E,
}

/// <summary>The payload codec identity of a delivery frame: the only places byte payloads ride the protocol.</summary>
public enum TerraScaleSyncV2PayloadCodec
{
    /// <summary>A native update-range delta export, codec version 1 (<c>terrabase-native/1</c>).</summary>
    NativeDelta1 = 3,

    /// <summary>A native snapshot export, codec version 1 (<c>terrabase-snapshot/1</c>).</summary>
    NativeSnapshot1 = 4,
}

/// <summary>The closed fatal-error vocabulary shared by error frames and mutation rejections.</summary>
public enum TerraScaleSyncV2ErrorCode
{
    /// <summary>The peer's protocol version is not spoken here.</summary>
    UnsupportedVersion = 1,

    /// <summary>A frame did not decode.</summary>
    MalformedFrame = 2,

    /// <summary>A presented checkpoint was refused (foreign peer, ahead of the server).</summary>
    CheckpointRefused = 3,

    /// <summary>A named scope or collection was refused.</summary>
    ScopeRefused = 4,

    /// <summary>The session is ending or has ended.</summary>
    SessionClosed = 5,

    /// <summary>The server is shedding load; retry after re-resolving.</summary>
    Overloaded = 6,
}

/// <summary>Why the server demanded a resync.</summary>
public enum TerraScaleSyncV2ResyncReason
{
    /// <summary>The session's checkpoint is older than the retained floor.</summary>
    BehindRetainedFloor = 1,

    /// <summary>History compaction raised the floor past the session.</summary>
    HistoryCompacted = 2,
}

/// <summary>The frame class: the unit the written bytes-on-wire budget definitions attach to.</summary>
public enum TerraScaleSyncV2FrameClass
{
    /// <summary>Session setup and acceptance.</summary>
    Session,

    /// <summary>Subscription modification.</summary>
    Subscription,

    /// <summary>Checkpoint and mutation acknowledgements.</summary>
    Acknowledgement,

    /// <summary>Heartbeats.</summary>
    Heartbeat,

    /// <summary>Delta batches and snapshot answers.</summary>
    Delivery,

    /// <summary>Trigger notifications.</summary>
    Trigger,

    /// <summary>Resync, region route, and error frames.</summary>
    Control,

    /// <summary>Mutation requests.</summary>
    Mutation,
}

/// <summary>The direction a frame kind travels on the client channel.</summary>
public enum TerraScaleSyncV2Direction
{
    /// <summary>Client to server.</summary>
    ClientToServer,

    /// <summary>Server to client.</summary>
    ServerToClient,

    /// <summary>Either direction (heartbeats).</summary>
    Both,
}

/// <summary>
/// The idempotency key: the stable retry identity of one mutation,
/// carried by exactly the mutation-family frames.
/// </summary>
public readonly struct TerraScaleSyncV2IdempotencyKey : IEquatable<TerraScaleSyncV2IdempotencyKey>
{
    /// <summary>Initializes and validates the key.</summary>
    /// <param name="value">The key text.</param>
    /// <exception cref="TerraScaleSyncV2ProtocolException">The key is empty, whitespace-only, carries control characters, or exceeds its bound.</exception>
    public TerraScaleSyncV2IdempotencyKey(string value)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(value))
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.InvalidIdempotencyKey,
                "the key is empty");
        }

        if (Encoding.UTF8.GetByteCount(value) > TerraScaleSyncV2Protocol.MaxKeyBytes)
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.InvalidIdempotencyKey,
                "the key exceeds its bound");
        }

        if (value.Any(char.IsControl))
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.InvalidIdempotencyKey,
                "the key carries control characters");
        }

        Value = value;
    }

    /// <summary>The key's text (already validated at construction).</summary>
    public string Value { get; }

    /// <inheritdoc />
    public bool Equals(TerraScaleSyncV2IdempotencyKey other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TerraScaleSyncV2IdempotencyKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>One native causal-position end pair of a checkpoint.</summary>
/// <param name="Peer">The writer peer the pair names.</param>
/// <param name="End">The end counter the session holds for the peer.</param>
public readonly record struct TerraScaleSyncV2VersionEnd(ulong Peer, ulong End);

/// <summary>
/// The durable resumption identity of one sync session: the version-vector
/// end pairs plus the storage sequence the session has absorbed. The
/// encoding is versioned (byte <c>2</c>), little-endian, and fails closed
/// on unknown versions and truncation.
/// </summary>
public sealed record TerraScaleSyncV2Checkpoint(
    IReadOnlyList<TerraScaleSyncV2VersionEnd> Ends,
    ulong StorageSequence) : IComparable<TerraScaleSyncV2Checkpoint>
{
    /// <summary>The initial checkpoint: nothing absorbed.</summary>
    public static TerraScaleSyncV2Checkpoint Initial { get; } = new([], 0);

    /// <summary>
    /// Builds a checkpoint from explicit end pairs, canonicalized to ascending peer order with duplicates removed.
    /// </summary>
    /// <param name="ends">The end pairs.</param>
    /// <param name="storageSequence">The absorbed storage sequence.</param>
    /// <returns>The canonical checkpoint.</returns>
    public static TerraScaleSyncV2Checkpoint Create(IEnumerable<(ulong Peer, int End)> ends, ulong storageSequence) => new(
        ends.Select(end => new TerraScaleSyncV2VersionEnd(end.Peer, checked((ulong)end.End)))
            .OrderBy(end => end.Peer)
            .DistinctBy(end => end.Peer)
            .ToArray(),
        storageSequence);

    /// <summary>The end entry for a peer, or <c>null</c> when the checkpoint holds no operation of that peer.</summary>
    /// <param name="peer">The peer.</param>
    /// <returns>The end counter, or <c>null</c>.</returns>
    public ulong? EndFor(ulong peer)
    {
        foreach (var end in Ends)
        {
            if (end.Peer == peer)
            {
                return end.End;
            }
        }

        return null;
    }

    /// <summary>
    /// Compares two checkpoints by storage sequence, then version-vector
    /// coverage. With the single server writer the storage sequence is
    /// the journal order, so this is the client-side currency comparison.
    /// </summary>
    /// <param name="other">The other checkpoint.</param>
    /// <returns>The comparison.</returns>
    public int CompareTo(TerraScaleSyncV2Checkpoint? other)
    {
        if (other is null)
        {
            return 1;
        }

        var sequence = StorageSequence.CompareTo(other.StorageSequence);
        if (sequence != 0)
        {
            return sequence;
        }

        return TotalEnd(Ends).CompareTo(TotalEnd(other.Ends));

        static ulong TotalEnd(IReadOnlyList<TerraScaleSyncV2VersionEnd> ends)
        {
            var total = 0UL;
            foreach (var end in ends)
            {
                total += end.End;
            }

            return total;
        }
    }

    /// <summary>Whether this checkpoint is at or past another's position.</summary>
    /// <param name="other">The other checkpoint.</param>
    /// <returns>The comparison result.</returns>
    public bool IsAtLeast(TerraScaleSyncV2Checkpoint other) => CompareTo(other) >= 0;

    /// <summary>The versioned byte encoding: version byte, storage sequence, pair count, then (peer, end) pairs, little-endian.</summary>
    /// <returns>The encoded checkpoint.</returns>
    public byte[] ToBytes()
    {
        var bytes = new byte[13 + (Ends.Count * 16)];
        bytes[0] = 2;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1, 8), StorageSequence);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(9, 4), (uint)Ends.Count);
        var offset = 13;
        foreach (var end in Ends)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), end.Peer);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 8, 8), end.End);
            offset += 16;
        }

        return bytes;
    }

    /// <summary>Decodes a checkpoint, failing closed on unknown versions and truncation.</summary>
    /// <param name="bytes">The encoded checkpoint.</param>
    /// <returns>The checkpoint.</returns>
    /// <exception cref="TerraScaleSyncV2ProtocolException">The encoding is not a v1 checkpoint or is truncated.</exception>
    public static TerraScaleSyncV2Checkpoint FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes[0] != 2)
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                $"unknown checkpoint encoding version {(bytes.IsEmpty ? 0 : bytes[0])}");
        }

        if (bytes.Length < 13)
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "truncated checkpoint encoding");
        }

        var storageSequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(1, 8));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(9, 4));
        // The pair count is bounded by the bytes that remain before any
        // allocation: a count whose pairs cannot fit the encoding is a
        // typed truncation, never an untyped capacity failure or an
        // overflowed truncation check. The bound also proves the count
        // fits int (a span's length already does).
        if (count > (ulong)((bytes.Length - 13L) / 16L))
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "truncated checkpoint encoding");
        }

        var ends = new List<TerraScaleSyncV2VersionEnd>((int)count);
        var offset = 13;
        for (var index = 0; index < count; index++)
        {
            ends.Add(new TerraScaleSyncV2VersionEnd(
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset + 8, 8))));
            offset += 16;
        }

        return new TerraScaleSyncV2Checkpoint(ends.ToArray(), storageSequence);
    }

    /// <inheritdoc />
    public bool Equals(TerraScaleSyncV2Checkpoint? other)
    {
        if (other is null || StorageSequence != other.StorageSequence || Ends.Count != other.Ends.Count)
        {
            return false;
        }

        for (var index = 0; index < Ends.Count; index++)
        {
            if (Ends[index] != other.Ends[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(StorageSequence);
        foreach (var end in Ends)
        {
            hash.Add(end);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// The typed scalar value of a document body at the sync seam: the
/// JSON-ish scalar subset a schema-free collection body carries (the
/// serving domain of the sync channel is scalar; composite values are
/// typed rejections, never coerced).
/// </summary>
public sealed record TerraScaleSyncV2Value
{
    private TerraScaleSyncV2Value(byte tag, bool boolean, long integer, double number, string? text)
    {
        Tag = tag;
        Boolean = boolean;
        Integer = integer;
        Number = number;
        Text = text;
    }

    /// <summary>The value's wire tag: 0 null, 1 boolean, 2 integer, 3 float, 4 string.</summary>
    public byte Tag { get; }

    /// <summary>The boolean, when the tag is 1.</summary>
    public bool Boolean { get; }

    /// <summary>The integer, when the tag is 2.</summary>
    public long Integer { get; }

    /// <summary>The float, when the tag is 3.</summary>
    public double Number { get; }

    /// <summary>The string, when the tag is 4.</summary>
    public string? Text { get; }

    /// <summary>The null value.</summary>
    public static TerraScaleSyncV2Value Null { get; } = new(0, false, 0, 0, null);

    /// <summary>Creates a boolean value.</summary>
    /// <param name="value">The boolean.</param>
    /// <returns>The value.</returns>
    public static TerraScaleSyncV2Value Of(bool value) => new(1, value, 0, 0, null);

    /// <summary>Creates an integer value.</summary>
    /// <param name="value">The integer.</param>
    /// <returns>The value.</returns>
    public static TerraScaleSyncV2Value Of(long value) => new(2, false, value, 0, null);

    /// <summary>Creates a float value.</summary>
    /// <param name="value">The float.</param>
    /// <returns>The value.</returns>
    public static TerraScaleSyncV2Value Of(double value) => new(3, false, 0, value, null);

    /// <summary>Creates a string value.</summary>
    /// <param name="value">The string.</param>
    /// <returns>The value.</returns>
    public static TerraScaleSyncV2Value Of(string value) => new(4, false, 0, 0, value);

    /// <inheritdoc />
    public bool Equals(TerraScaleSyncV2Value? other) => other is not null && Tag == other.Tag && Tag switch
    {
        0 => true,
        1 => Boolean == other.Boolean,
        2 => Integer == other.Integer,
        3 => Number == other.Number,
        4 => string.Equals(Text, other.Text, StringComparison.Ordinal),
        _ => false,
    };

    /// <inheritdoc />
    public override int GetHashCode()
    {
        object? payload = Tag switch
        {
            1 => Boolean,
            2 => Integer,
            3 => Number,
            4 => (string?)Text,
            _ => null,
        };
        return HashCode.Combine(Tag, payload);
    }
}

/// <summary>
/// The write a mutation request carries: a typed document body, never an
/// encoded delta — the server is the single writer of record.
/// </summary>
public abstract record TerraScaleSyncV2DocumentWrite
{
    /// <summary>Replaces the document body with these fields.</summary>
    /// <param name="Body">The full new body of the document. Fields encode in canonical UTF-8 key order.</param>
    public sealed record Put(IReadOnlyDictionary<string, TerraScaleSyncV2Value> Body) : TerraScaleSyncV2DocumentWrite;

    /// <summary>Deletes the document.</summary>
    public sealed record Delete : TerraScaleSyncV2DocumentWrite;
}

/// <summary>
/// The outcome of a mutation: the durable identities once applied, or a
/// typed rejection. There is no third state — mutations are
/// server-ack-only in v2 clients (PD-2026-08-26-12 D2).
/// </summary>
public abstract record TerraScaleSyncV2MutationOutcome;

/// <summary>The mutation is durably applied; the delta arrives as delivery.</summary>
/// <param name="Sequence">The storage sequence of the applied commit.</param>
/// <param name="OperationId">The stable operation identity of the applied commit.</param>
public sealed record TerraScaleSyncV2MutationApplied(ulong Sequence, string OperationId) : TerraScaleSyncV2MutationOutcome;

/// <summary>The mutation was refused.</summary>
/// <param name="Reason">The typed refusal reason (the closed error vocabulary).</param>
public sealed record TerraScaleSyncV2MutationRejected(TerraScaleSyncV2ErrorCode Reason) : TerraScaleSyncV2MutationOutcome
{
    /// <summary>The stable wire name of the refusal reason.</summary>
    public string WireName => Reason switch
    {
        TerraScaleSyncV2ErrorCode.UnsupportedVersion => "unsupported_version",
        TerraScaleSyncV2ErrorCode.MalformedFrame => "malformed_frame",
        TerraScaleSyncV2ErrorCode.CheckpointRefused => "checkpoint_refused",
        TerraScaleSyncV2ErrorCode.ScopeRefused => "scope_refused",
        TerraScaleSyncV2ErrorCode.SessionClosed => "session_closed",
        TerraScaleSyncV2ErrorCode.Overloaded => "overloaded",
        _ => "session_closed",
    };
}

/// <summary>
/// The typed payload of one <c>terrascale.sync.v2</c> frame body: one
/// record per frame kind, each with a canonical byte encoding that is the
/// digest input and the wire body.
/// </summary>
public abstract record TerraScaleSyncV2Body
{
    /// <summary>The body's frame kind.</summary>
    public abstract TerraScaleSyncV2FrameKind Kind { get; }

    /// <summary>Whether this body belongs to the mutation family: the only bodies whose frames carry an idempotency key.</summary>
    public bool IsMutation => this is TerraScaleSyncV2MutationRequestBody or TerraScaleSyncV2MutationAckBody;

    /// <summary>Whether the body carries an encoded byte payload.</summary>
    public bool CarriesPayload => this is TerraScaleSyncV2DeltaBatchBody or TerraScaleSyncV2SnapshotAnswerBody;

    /// <summary>The byte length of the encoded payload, <c>0</c> for every frame class without a payload.</summary>
    public virtual int PayloadLength => 0;

    /// <summary>The direction this frame travels on the client channel.</summary>
    public TerraScaleSyncV2Direction Direction => this switch
    {
        TerraScaleSyncV2SessionSetupBody
        or TerraScaleSyncV2SubscriptionModifyBody
        or TerraScaleSyncV2CheckpointAckBody
        or TerraScaleSyncV2MutationRequestBody => TerraScaleSyncV2Direction.ClientToServer,
        TerraScaleSyncV2SessionAcceptedBody
        or TerraScaleSyncV2SubscriptionChangedBody
        or TerraScaleSyncV2DeltaBatchBody
        or TerraScaleSyncV2SnapshotAnswerBody
        or TerraScaleSyncV2TriggerBody
        or TerraScaleSyncV2ResyncRequiredBody
        or TerraScaleSyncV2RegionRouteChangeBody
        or TerraScaleSyncV2ErrorBody
        or TerraScaleSyncV2MutationAckBody => TerraScaleSyncV2Direction.ServerToClient,
        _ => TerraScaleSyncV2Direction.Both,
    };

    /// <summary>The frame class whose written budget governs this frame.</summary>
    public TerraScaleSyncV2FrameClass FrameClass => this switch
    {
        TerraScaleSyncV2SessionSetupBody or TerraScaleSyncV2SessionAcceptedBody => TerraScaleSyncV2FrameClass.Session,
        TerraScaleSyncV2SubscriptionModifyBody or TerraScaleSyncV2SubscriptionChangedBody => TerraScaleSyncV2FrameClass.Subscription,
        TerraScaleSyncV2CheckpointAckBody or TerraScaleSyncV2MutationAckBody => TerraScaleSyncV2FrameClass.Acknowledgement,
        TerraScaleSyncV2HeartbeatBody => TerraScaleSyncV2FrameClass.Heartbeat,
        TerraScaleSyncV2DeltaBatchBody or TerraScaleSyncV2SnapshotAnswerBody => TerraScaleSyncV2FrameClass.Delivery,
        TerraScaleSyncV2TriggerBody => TerraScaleSyncV2FrameClass.Trigger,
        TerraScaleSyncV2ResyncRequiredBody
        or TerraScaleSyncV2RegionRouteChangeBody
        or TerraScaleSyncV2ErrorBody => TerraScaleSyncV2FrameClass.Control,
        _ => TerraScaleSyncV2FrameClass.Mutation,
    };

    /// <summary>
    /// The canonical body encoding: the digest input and the wire body.
    /// Validates the body's invariants first, failing closed.
    /// </summary>
    /// <returns>The canonical body bytes.</returns>
    /// <exception cref="TerraScaleSyncV2ProtocolException">The body violates its invariants.</exception>
    public byte[] ToBodyBytes()
    {
        Validate();
        var writer = new SyncV2Writer();
        writer.WriteUInt16((ushort)Kind);
        Encode(ref writer);
        return writer.ToBytes();
    }

    /// <summary>
    /// Decodes a canonical body, failing closed on every structural problem.
    /// </summary>
    /// <param name="bytes">The body bytes.</param>
    /// <returns>The decoded body.</returns>
    /// <exception cref="TerraScaleSyncV2ProtocolException">The body does not decode.</exception>
    public static TerraScaleSyncV2Body FromBodyBytes(ReadOnlySpan<byte> bytes)
    {
        var reader = new SyncV2Reader(bytes);
        var kind = reader.ReadUInt16();
        var body = Decode(kind, ref reader);
        reader.End();
        body.Validate();
        return body;
    }

    /// <summary>Validates and encodes the body's fields onto the writer.</summary>
    /// <param name="writer">The writer.</param>
    protected abstract void Encode(ref SyncV2Writer writer);

    /// <summary>Validates the body's invariants (limits, codec/kind pairing, flag consistency).</summary>
    /// <exception cref="TerraScaleSyncV2ProtocolException">The body violates its invariants.</exception>
    protected virtual void Validate()
    {
    }

    private static TerraScaleSyncV2Body Decode(ushort code, ref SyncV2Reader reader)
    {
        if (!Enum.IsDefined(typeof(TerraScaleSyncV2FrameKind), (int)code))
        {
            throw Malformed("unknown frame kind");
        }

        var kind = (TerraScaleSyncV2FrameKind)code;
        switch (kind)
        {
            case TerraScaleSyncV2FrameKind.SessionSetup:
                {
                    var collections = reader.ReadStrings();
                    var resume = reader.ReadUInt8() switch
                    {
                        0 => null,
                        1 => reader.ReadCheckpoint(),
                        _ => throw Malformed("unknown resume flag"),
                    };
                    return new TerraScaleSyncV2SessionSetupBody(collections, resume);
                }

            case TerraScaleSyncV2FrameKind.SessionAccepted:
                {
                    var session = reader.ReadUInt64();
                    var server = reader.ReadCheckpoint();
                    var collections = reader.ReadStrings();
                    return new TerraScaleSyncV2SessionAcceptedBody(session, server, collections);
                }

            case TerraScaleSyncV2FrameKind.SubscriptionModify:
                {
                    var add = reader.ReadStrings();
                    var remove = reader.ReadStrings();
                    return new TerraScaleSyncV2SubscriptionModifyBody(add, remove);
                }

            case TerraScaleSyncV2FrameKind.SubscriptionChanged:
                {
                    var active = reader.ReadStrings();
                    return new TerraScaleSyncV2SubscriptionChangedBody(active);
                }

            case TerraScaleSyncV2FrameKind.CheckpointAck:
                {
                    return new TerraScaleSyncV2CheckpointAckBody(reader.ReadCheckpoint());
                }

            case TerraScaleSyncV2FrameKind.Heartbeat:
                {
                    return new TerraScaleSyncV2HeartbeatBody();
                }

            case TerraScaleSyncV2FrameKind.DeltaBatch:
                {
                    var codec = reader.ReadPayloadCodec();
                    var payload = reader.ReadPayload();
                    var next = reader.ReadCheckpoint();
                    var withinBudget = reader.ReadUInt8() != 0;
                    var oversize = reader.ReadUInt8() != 0;
                    var commits = reader.ReadUInt64();
                    return new TerraScaleSyncV2DeltaBatchBody(codec, payload, next, withinBudget, oversize, commits);
                }

            case TerraScaleSyncV2FrameKind.SnapshotAnswer:
                {
                    var codec = reader.ReadPayloadCodec();
                    var payload = reader.ReadPayload();
                    var checkpoint = reader.ReadCheckpoint();
                    return new TerraScaleSyncV2SnapshotAnswerBody(codec, payload, checkpoint);
                }

            case TerraScaleSyncV2FrameKind.Trigger:
                {
                    var collection = reader.ReadString(TerraScaleSyncV2Protocol.MaxTriggerIdentBytes);
                    var sequence = reader.ReadUInt64();
                    var operationId = reader.ReadString(TerraScaleSyncV2Protocol.MaxTriggerIdentBytes);
                    return new TerraScaleSyncV2TriggerBody(collection, sequence, operationId);
                }

            case TerraScaleSyncV2FrameKind.ResyncRequired:
                {
                    var floor = reader.ReadCheckpoint();
                    var reason = reader.ReadResyncReason();
                    return new TerraScaleSyncV2ResyncRequiredBody(floor, reason);
                }

            case TerraScaleSyncV2FrameKind.RegionRouteChange:
                {
                    return new TerraScaleSyncV2RegionRouteChangeBody(
                        reader.ReadString(TerraScaleSyncV2Protocol.MaxIdentBytes));
                }

            case TerraScaleSyncV2FrameKind.Error:
                {
                    var errorCode = reader.ReadErrorCode();
                    var detail = reader.ReadUInt8() switch
                    {
                        0 => null,
                        1 => reader.ReadString(TerraScaleSyncV2Protocol.MaxDetailBytes),
                        _ => throw Malformed("unknown detail flag"),
                    };
                    return new TerraScaleSyncV2ErrorBody(errorCode, detail);
                }

            case TerraScaleSyncV2FrameKind.MutationRequest:
                {
                    var collection = reader.ReadString(TerraScaleSyncV2Protocol.MaxIdentBytes);
                    var document = reader.ReadString(TerraScaleSyncV2Protocol.MaxIdentBytes);
                    var write = reader.ReadUInt8() switch
                    {
                        1 => (TerraScaleSyncV2DocumentWrite)new TerraScaleSyncV2DocumentWrite.Put(ReadFields(ref reader)),
                        2 => new TerraScaleSyncV2DocumentWrite.Delete(),
                        _ => throw Malformed("unknown write tag"),
                    };
                    return new TerraScaleSyncV2MutationRequestBody(collection, document, write);
                }

            case TerraScaleSyncV2FrameKind.MutationAck:
                {
                    var outcome = reader.ReadUInt8() switch
                    {
                        1 => (TerraScaleSyncV2MutationOutcome)new TerraScaleSyncV2MutationApplied(
                            reader.ReadUInt64(),
                            reader.ReadString(TerraScaleSyncV2Protocol.MaxKeyBytes)),
                        2 => new TerraScaleSyncV2MutationRejected(reader.ReadErrorCode()),
                        _ => throw Malformed("unknown outcome tag"),
                    };
                    return new TerraScaleSyncV2MutationAckBody(outcome);
                }

            default:
                throw Malformed("unknown frame kind");
        }

        static SortedDictionary<string, TerraScaleSyncV2Value> ReadFields(ref SyncV2Reader reader)
        {
            var count = reader.ReadUInt32();
            if (count > TerraScaleSyncV2Protocol.MaxFields)
            {
                throw Malformed("too many mutation fields");
            }

            var fields = new SortedDictionary<string, TerraScaleSyncV2Value>(Utf8OrdinalComparer.Instance);
            for (var index = 0; index < count; index++)
            {
                var key = reader.ReadString(TerraScaleSyncV2Protocol.MaxIdentBytes);
                fields[key] = reader.ReadValue();
            }

            return fields;
        }
    }

    /// <summary>Builds the malformed-frame rejection.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns>The rejection.</returns>
    protected static TerraScaleSyncV2ProtocolException Malformed(string reason) =>
        new(TerraScaleSyncV2ProtocolError.MalformedFrame, reason);

    /// <summary>Throws when a string exceeds its frame limit (UTF-8 bytes).</summary>
    /// <param name="value">The string.</param>
    /// <param name="limit">The limit.</param>
    protected static void CheckString(string value, int limit)
    {
        if (Encoding.UTF8.GetByteCount(value) > limit)
        {
            throw Malformed("string exceeds its frame limit");
        }
    }

    /// <summary>Throws when a collection list exceeds its entry or identifier limits.</summary>
    /// <param name="list">The list.</param>
    protected static void CheckStrings(IReadOnlyList<string> list)
    {
        if (list.Count > TerraScaleSyncV2Protocol.MaxCollections)
        {
            throw Malformed("too many collection entries");
        }

        foreach (var entry in list)
        {
            CheckString(entry, TerraScaleSyncV2Protocol.MaxIdentBytes);
        }
    }

    /// <summary>Throws when a checkpoint does not round-trip its own encoding.</summary>
    /// <param name="checkpoint">The checkpoint.</param>
    /// <param name="reason">The refusal reason.</param>
    protected static void Roundtrip(TerraScaleSyncV2Checkpoint? checkpoint, string reason)
    {
        if (checkpoint is null)
        {
            return;
        }

        try
        {
            _ = TerraScaleSyncV2Checkpoint.FromBytes(checkpoint.ToBytes());
        }
        catch (TerraScaleSyncV2ProtocolException failure)
        {
            throw Malformed($"{reason}: {failure.Reason}");
        }
    }
}

/// <summary>Opens a session, fresh (<c>Resume</c> is <c>null</c>) or resuming from the session's durable checkpoint.</summary>
/// <param name="Collections">The collections the session subscribes to.</param>
/// <param name="Resume">The checkpoint a resuming session presents.</param>
public sealed record TerraScaleSyncV2SessionSetupBody(
    IReadOnlyList<string> Collections,
    TerraScaleSyncV2Checkpoint? Resume) : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.SessionSetup;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer)
    {
        writer.WriteStrings(Collections);
        if (Resume is { } resume)
        {
            writer.WriteUInt8(1);
            writer.WriteCheckpoint(resume);
        }
        else
        {
            writer.WriteUInt8(0);
        }
    }

    /// <inheritdoc />
    protected override void Validate()
    {
        CheckStrings(Collections);
        Roundtrip(Resume, "the resume checkpoint does not round-trip");
    }
}

/// <summary>The server accepts the session and states its checkpoint.</summary>
/// <param name="Session">The server-assigned session identity (diagnostics).</param>
/// <param name="Server">The server's current checkpoint.</param>
/// <param name="Collections">The subscription set now in force.</param>
public sealed record TerraScaleSyncV2SessionAcceptedBody(
    ulong Session,
    TerraScaleSyncV2Checkpoint Server,
    IReadOnlyList<string> Collections) : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.SessionAccepted;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer)
    {
        writer.WriteUInt64(Session);
        writer.WriteCheckpoint(Server);
        writer.WriteStrings(Collections);
    }

    /// <inheritdoc />
    protected override void Validate()
    {
        CheckStrings(Collections);
        Roundtrip(Server, "the server checkpoint does not round-trip");
    }
}

/// <summary>Modifies the session's subscriptions.</summary>
/// <param name="Add">Collections to add.</param>
/// <param name="Remove">Collections to remove.</param>
public sealed record TerraScaleSyncV2SubscriptionModifyBody(
    IReadOnlyList<string> Add,
    IReadOnlyList<string> Remove) : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.SubscriptionModify;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer)
    {
        writer.WriteStrings(Add);
        writer.WriteStrings(Remove);
    }

    /// <inheritdoc />
    protected override void Validate()
    {
        CheckStrings(Add);
        CheckStrings(Remove);
    }
}

/// <summary>The subscription set now in force.</summary>
/// <param name="Active">The active subscription set.</param>
public sealed record TerraScaleSyncV2SubscriptionChangedBody(IReadOnlyList<string> Active) : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.SubscriptionChanged;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer) => writer.WriteStrings(Active);

    /// <inheritdoc />
    protected override void Validate() => CheckStrings(Active);
}

/// <summary>Acknowledges the session's resumption position; retransmission is bounded by the acknowledged checkpoint.</summary>
/// <param name="Checkpoint">The session's durable checkpoint.</param>
public sealed record TerraScaleSyncV2CheckpointAckBody(TerraScaleSyncV2Checkpoint Checkpoint) : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.CheckpointAck;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer) => writer.WriteCheckpoint(Checkpoint);

    /// <inheritdoc />
    protected override void Validate() => Roundtrip(Checkpoint, "the checkpoint does not round-trip");
}

/// <summary>Liveness probe; no payload. A heartbeat encodes to exactly the fixed envelope cost.</summary>
public sealed record TerraScaleSyncV2HeartbeatBody : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.Heartbeat;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer)
    {
    }
}

/// <summary>One byte-budgeted delta batch.</summary>
/// <param name="Codec">The payload codec: always <see cref="TerraScaleSyncV2PayloadCodec.NativeDelta1"/>.</param>
/// <param name="Payload">The encoded delta bytes (opaque at the protocol layer; never re-encoded).</param>
/// <param name="Next">The checkpoint after applying this batch.</param>
/// <param name="WithinBudget">Whether the batch fits the configured byte budget.</param>
/// <param name="SingleCommitOversize">True only when a single commit's delta alone exceeded the budget and the batch carries exactly that one commit.</param>
/// <param name="Commits">How many journal commits the batch carries.</param>
public sealed record TerraScaleSyncV2DeltaBatchBody(
    TerraScaleSyncV2PayloadCodec Codec,
    byte[] Payload,
    TerraScaleSyncV2Checkpoint Next,
    bool WithinBudget,
    bool SingleCommitOversize,
    ulong Commits) : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.DeltaBatch;

    /// <inheritdoc />
    public override int PayloadLength => Payload.Length;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer)
    {
        writer.WriteUInt8((byte)Codec);
        writer.WritePayload(Payload);
        writer.WriteCheckpoint(Next);
        writer.WriteUInt8((byte)(WithinBudget ? 1 : 0));
        writer.WriteUInt8((byte)(SingleCommitOversize ? 1 : 0));
        writer.WriteUInt64(Commits);
    }

    /// <inheritdoc />
    protected override void Validate()
    {
        if (Codec != TerraScaleSyncV2PayloadCodec.NativeDelta1)
        {
            throw Malformed("a delta batch carries the terrabase-native/1 codec only");
        }

        if (Payload.Length > TerraScaleSyncV2Protocol.MaxPayloadBytes)
        {
            throw Malformed("payload exceeds the frame bound");
        }

        if (SingleCommitOversize && Commits != 1)
        {
            throw Malformed("the oversize flag requires exactly one commit");
        }

        Roundtrip(Next, "the next checkpoint does not round-trip");
    }

    /// <inheritdoc />
    public bool Equals(TerraScaleSyncV2DeltaBatchBody? other) => other is not null
        && Codec == other.Codec
        && Payload.AsSpan().SequenceEqual(other.Payload)
        && Next.Equals(other.Next)
        && WithinBudget == other.WithinBudget
        && SingleCommitOversize == other.SingleCommitOversize
        && Commits == other.Commits;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Codec, Payload.Length, Next, WithinBudget, SingleCommitOversize, Commits);
}

/// <summary>One full-state answer (initial sync or forced resync).</summary>
/// <param name="Codec">The payload codec: always <see cref="TerraScaleSyncV2PayloadCodec.NativeSnapshot1"/>.</param>
/// <param name="Payload">The encoded snapshot bytes (opaque at the protocol layer; never re-encoded).</param>
/// <param name="Checkpoint">The checkpoint the snapshot corresponds to.</param>
public sealed record TerraScaleSyncV2SnapshotAnswerBody(
    TerraScaleSyncV2PayloadCodec Codec,
    byte[] Payload,
    TerraScaleSyncV2Checkpoint Checkpoint) : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.SnapshotAnswer;

    /// <inheritdoc />
    public override int PayloadLength => Payload.Length;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer)
    {
        writer.WriteUInt8((byte)Codec);
        writer.WritePayload(Payload);
        writer.WriteCheckpoint(Checkpoint);
    }

    /// <inheritdoc />
    protected override void Validate()
    {
        if (Codec != TerraScaleSyncV2PayloadCodec.NativeSnapshot1)
        {
            throw Malformed("a snapshot answer carries the terrabase-snapshot/1 codec only");
        }

        if (Payload.Length > TerraScaleSyncV2Protocol.MaxPayloadBytes)
        {
            throw Malformed("payload exceeds the frame bound");
        }

        Roundtrip(Checkpoint, "the snapshot checkpoint does not round-trip");
    }

    /// <inheritdoc />
    public bool Equals(TerraScaleSyncV2SnapshotAnswerBody? other) => other is not null
        && Codec == other.Codec
        && Payload.AsSpan().SequenceEqual(other.Payload)
        && Checkpoint.Equals(other.Checkpoint);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Codec, Payload.Length, Checkpoint);
}

/// <summary>
/// A content-free trigger notification: the collection scope plus the two
/// commit identifiers. It never carries document data; the client answers
/// by pulling delta batches from its checkpoint.
/// </summary>
/// <param name="Collection">The collection the commit landed in.</param>
/// <param name="StorageSequence">The storage sequence of the published commit.</param>
/// <param name="OperationId">The stable operation identity of the published commit.</param>
public sealed record TerraScaleSyncV2TriggerBody(
    string Collection,
    ulong StorageSequence,
    string OperationId) : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.Trigger;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer)
    {
        writer.WriteString(Collection, TerraScaleSyncV2Protocol.MaxTriggerIdentBytes);
        writer.WriteUInt64(StorageSequence);
        writer.WriteString(OperationId, TerraScaleSyncV2Protocol.MaxTriggerIdentBytes);
    }

    /// <inheritdoc />
    protected override void Validate()
    {
        if (Encoding.UTF8.GetByteCount(Collection) > TerraScaleSyncV2Protocol.MaxTriggerIdentBytes
            || Encoding.UTF8.GetByteCount(OperationId) > TerraScaleSyncV2Protocol.MaxTriggerIdentBytes)
        {
            throw Malformed("trigger identifiers are bounded to 64 bytes");
        }
    }
}

/// <summary>The session must resync: its checkpoint is below the retained floor.</summary>
/// <param name="Floor">The oldest checkpoint deltas still serve from.</param>
/// <param name="Reason">Why the resync was demanded.</param>
public sealed record TerraScaleSyncV2ResyncRequiredBody(
    TerraScaleSyncV2Checkpoint Floor,
    TerraScaleSyncV2ResyncReason Reason) : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.ResyncRequired;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer)
    {
        writer.WriteCheckpoint(Floor);
        writer.WriteUInt8((byte)Reason);
    }

    /// <inheritdoc />
    protected override void Validate() => Roundtrip(Floor, "the floor checkpoint does not round-trip");
}

/// <summary>The serving region changed; the client re-resolves the endpoint and resumes from its checkpoint.</summary>
/// <param name="Region">The new serving region's name.</param>
public sealed record TerraScaleSyncV2RegionRouteChangeBody(string Region) : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.RegionRouteChange;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer) =>
        writer.WriteString(Region, TerraScaleSyncV2Protocol.MaxIdentBytes);

    /// <inheritdoc />
    protected override void Validate() => CheckString(Region, TerraScaleSyncV2Protocol.MaxIdentBytes);
}

/// <summary>A fatal error from the closed vocabulary.</summary>
/// <param name="Code">The typed error code.</param>
/// <param name="Detail">A bounded, machine-readable detail string (no secrets, no document data).</param>
public sealed record TerraScaleSyncV2ErrorBody(
    TerraScaleSyncV2ErrorCode Code,
    string? Detail) : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.Error;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer)
    {
        writer.WriteUInt8((byte)Code);
        if (Detail is { } detail)
        {
            writer.WriteUInt8(1);
            writer.WriteString(detail, TerraScaleSyncV2Protocol.MaxDetailBytes);
        }
        else
        {
            writer.WriteUInt8(0);
        }
    }

    /// <inheritdoc />
    protected override void Validate()
    {
        if (Detail is { } detail && Encoding.UTF8.GetByteCount(detail) > TerraScaleSyncV2Protocol.MaxDetailBytes)
        {
            throw Malformed("the error detail is bounded");
        }
    }
}

/// <summary>A mutation request: typed document write plus the envelope's idempotency key. The only mutation frame a client may send.</summary>
/// <param name="Collection">The target collection.</param>
/// <param name="Document">The target document.</param>
/// <param name="Write">The typed write.</param>
public sealed record TerraScaleSyncV2MutationRequestBody(
    string Collection,
    string Document,
    TerraScaleSyncV2DocumentWrite Write) : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.MutationRequest;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer)
    {
        writer.WriteString(Collection, TerraScaleSyncV2Protocol.MaxIdentBytes);
        writer.WriteString(Document, TerraScaleSyncV2Protocol.MaxIdentBytes);
        if (Write is TerraScaleSyncV2DocumentWrite.Put put)
        {
            writer.WriteUInt8(1);
            writer.WriteUInt32((uint)put.Body.Count);
            foreach (var (key, value) in put.Body.OrderBy(field => field.Key, Utf8OrdinalComparer.Instance))
            {
                writer.WriteString(key, TerraScaleSyncV2Protocol.MaxIdentBytes);
                writer.WriteValue(value);
            }
        }
        else
        {
            writer.WriteUInt8(2);
        }
    }

    /// <inheritdoc />
    protected override void Validate()
    {
        CheckString(Collection, TerraScaleSyncV2Protocol.MaxIdentBytes);
        CheckString(Document, TerraScaleSyncV2Protocol.MaxIdentBytes);
        if (Write is TerraScaleSyncV2DocumentWrite.Put put)
        {
            if (put.Body.Count > TerraScaleSyncV2Protocol.MaxFields)
            {
                throw Malformed("too many mutation fields");
            }

            foreach (var (key, value) in put.Body)
            {
                CheckString(key, TerraScaleSyncV2Protocol.MaxIdentBytes);
                if (value.Text is { } text)
                {
                    CheckString(text, TerraScaleSyncV2Protocol.MaxIdentBytes);
                }
            }
        }
    }
}

/// <summary>The server's acknowledgement of a mutation: the only answer.</summary>
/// <param name="Outcome">The applied identities or the typed refusal.</param>
public sealed record TerraScaleSyncV2MutationAckBody(TerraScaleSyncV2MutationOutcome Outcome) : TerraScaleSyncV2Body
{
    /// <inheritdoc />
    public override TerraScaleSyncV2FrameKind Kind => TerraScaleSyncV2FrameKind.MutationAck;

    /// <inheritdoc />
    protected override void Encode(ref SyncV2Writer writer)
    {
        if (Outcome is TerraScaleSyncV2MutationApplied applied)
        {
            writer.WriteUInt8(1);
            writer.WriteUInt64(applied.Sequence);
            writer.WriteString(applied.OperationId, TerraScaleSyncV2Protocol.MaxKeyBytes);
        }
        else
        {
            var rejected = (TerraScaleSyncV2MutationRejected)Outcome;
            writer.WriteUInt8(2);
            writer.WriteUInt8((byte)rejected.Reason);
        }
    }

    /// <inheritdoc />
    protected override void Validate()
    {
        if (Outcome is TerraScaleSyncV2MutationApplied applied)
        {
            CheckString(applied.OperationId, TerraScaleSyncV2Protocol.MaxKeyBytes);
        }
    }
}

/// <summary>
/// One typed, versioned <c>terrascale.sync.v2</c> frame: the shared
/// envelope discipline every frame carries — protocol version, frame
/// kind, correlation and causation identity, an idempotency key exactly
/// where mutation is possible, and a content digest over the canonical
/// body — plus its versioned, fail-closed wire encoding.
/// </summary>
public sealed class TerraScaleSyncV2Frame
{
    private TerraScaleSyncV2Frame(
        ulong correlation,
        ulong causation,
        TerraScaleSyncV2IdempotencyKey? idempotency,
        ulong digest,
        TerraScaleSyncV2Body body)
    {
        Correlation = correlation;
        Causation = causation;
        IdempotencyKey = idempotency;
        digestValue = digest;
        Body = body;
    }

    private readonly ulong digestValue;

    /// <summary>The frame's kind.</summary>
    public TerraScaleSyncV2FrameKind Kind => Body.Kind;

    /// <summary>The correlation identity of the frame.</summary>
    public ulong Correlation { get; }

    /// <summary>The causation identity of the frame.</summary>
    public ulong Causation { get; }

    /// <summary>The idempotency key, present exactly on mutation-family frames.</summary>
    public TerraScaleSyncV2IdempotencyKey? IdempotencyKey { get; }

    /// <summary>The frame's body.</summary>
    public TerraScaleSyncV2Body Body { get; }

    /// <summary>The content digest the frame was sealed with (FNV-1a 64 over the canonical body bytes).</summary>
    public ulong Digest => digestValue;

    /// <summary>The length of the encoded payload this frame carries, <c>0</c> when the frame class carries no payload.</summary>
    public int PayloadLength => Body.PayloadLength;

    /// <summary>
    /// Seals a body into a frame: validates the body, enforces the
    /// idempotency pairing (a key exactly on the mutation-family frames),
    /// and computes the content digest over the canonical body encoding.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="correlation">The frame's correlation identity.</param>
    /// <param name="causation">The peer frame this one answers.</param>
    /// <param name="idempotency">The idempotency key, exactly on mutation-family frames.</param>
    /// <returns>The sealed frame.</returns>
    /// <exception cref="TerraScaleSyncV2ProtocolException">The body violates its invariants or the pairing is wrong.</exception>
    public static TerraScaleSyncV2Frame Seal(
        TerraScaleSyncV2Body body,
        ulong correlation,
        ulong causation,
        TerraScaleSyncV2IdempotencyKey? idempotency = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.IsMutation != idempotency.HasValue)
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.IdempotencyPairing,
                body.IsMutation
                    ? "a mutation frame requires its idempotency key"
                    : "only mutation frames carry an idempotency key");
        }

        var bodyBytes = body.ToBodyBytes();
        return new TerraScaleSyncV2Frame(correlation, causation, idempotency, Fnv1a64(bodyBytes), body);
    }

    /// <summary>Whether the sealed digest still matches the body.</summary>
    /// <returns>The verification result.</returns>
    public bool Verify() => Fnv1a64(Body.ToBodyBytes()) == digestValue;

    /// <summary>Whether the frame fits its class's written budget; the single exception is a flagged single-commit oversize delivery.</summary>
    /// <returns>The budget result.</returns>
    public bool WithinClassBudget()
    {
        var wire = ToByteArray();
        if (wire.Length <= TerraScaleSyncV2Protocol.BudgetBytes(Body.FrameClass))
        {
            return true;
        }

        return Body is TerraScaleSyncV2DeltaBatchBody { SingleCommitOversize: true, Commits: 1 };
    }

    /// <summary>
    /// The versioned wire encoding: version byte, kind, correlation,
    /// causation, idempotency presence (and key), content digest, body
    /// length, then the canonical body bytes. All integers little-endian;
    /// deterministic across runs and platforms.
    /// </summary>
    /// <returns>The wire bytes.</returns>
    public byte[] ToByteArray()
    {
        var body = Body.ToBodyBytes();
        var keyBytes = IdempotencyKey is { } key ? Encoding.UTF8.GetBytes(key.Value) : null;
        var bytes = new byte[36 + body.Length + ((keyBytes?.Length ?? 0) + (keyBytes is null ? 0 : 8))];
        var offset = 0;
        bytes[offset++] = TerraScaleSyncV2Protocol.WireVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), (ushort)Kind);
        offset += 2;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), Correlation);
        offset += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), Causation);
        offset += 8;
        if (keyBytes is null)
        {
            bytes[offset++] = 0;
        }
        else
        {
            bytes[offset++] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), (ulong)keyBytes.Length);
            offset += 8;
            keyBytes.CopyTo(bytes, offset);
            offset += keyBytes.Length;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), digestValue);
        offset += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), (ulong)body.Length);
        offset += 8;
        body.CopyTo(bytes, offset);
        return bytes;
    }

    /// <summary>
    /// Decodes a frame, failing closed on unknown versions (including
    /// v1), unknown kinds, truncation, trailing bytes, length
    /// disagreement, limit violations, and idempotency-pairing
    /// violations. Digest disagreement is not a decode error — call
    /// <see cref="Verify"/> on the result.
    /// </summary>
    /// <param name="bytes">The wire bytes.</param>
    /// <returns>The decoded frame.</returns>
    /// <exception cref="TerraScaleSyncV2ProtocolException">The frame is rejected with the typed error.</exception>
    public static TerraScaleSyncV2Frame Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > TerraScaleSyncV2Protocol.MaxFrameBytes)
        {
            throw Malformed("the frame exceeds the decode bound");
        }

        if (bytes.IsEmpty || bytes[0] != TerraScaleSyncV2Protocol.WireVersion)
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.UnsupportedVersion,
                $"unsupported protocol version {(bytes.IsEmpty ? 0 : bytes[0])}");
        }

        if (bytes.Length < 20)
        {
            throw Malformed("truncated frame encoding");
        }

        var reader = new SyncV2Reader(bytes.Slice(1));
        var kind = reader.ReadUInt16();
        var correlation = reader.ReadUInt64();
        var causation = reader.ReadUInt64();
        TerraScaleSyncV2IdempotencyKey? idempotency;
        switch (reader.ReadUInt8())
        {
            case 0:
                idempotency = null;
                break;
            case 1:
                idempotency = new TerraScaleSyncV2IdempotencyKey(reader.ReadKeyBytes());
                break;
            default:
                throw Malformed("unknown idempotency presence");
        }

        var digest = reader.ReadUInt64();
        var bodyLength = reader.ReadBodyLength();
        var bodyBytes = reader.ReadBytes(bodyLength);
        reader.End();

        if (!Enum.IsDefined(typeof(TerraScaleSyncV2FrameKind), (int)kind))
        {
            throw Malformed("unknown frame kind");
        }

        var bodyTag = new SyncV2Reader(bodyBytes).ReadUInt16();
        if (bodyTag != kind)
        {
            throw Malformed("the body kind disagrees with the envelope kind");
        }

        var body = TerraScaleSyncV2Body.FromBodyBytes(bodyBytes);
        if (body.IsMutation != idempotency.HasValue)
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.IdempotencyPairing,
                body.IsMutation
                    ? "a mutation frame requires its idempotency key"
                    : "only mutation frames carry an idempotency key");
        }

        return new TerraScaleSyncV2Frame(correlation, causation, idempotency, digest, body);
    }

    private static TerraScaleSyncV2ProtocolException Malformed(string reason) =>
        new(TerraScaleSyncV2ProtocolError.MalformedFrame, reason);

    /// <summary>The FNV-1a 64 content digest: a correlation-integrity digest, not a cryptographic guarantee.</summary>
    /// <param name="bytes">The digest input.</param>
    /// <returns>The digest.</returns>
    internal static ulong Fnv1a64(ReadOnlySpan<byte> bytes)
    {
        var hash = 0xcbf2_9ce4_8422_2325ul;
        foreach (var byteValue in bytes)
        {
            hash ^= byteValue;
            hash *= 0x0000_0100_0000_01b3;
        }

        return hash;
    }
}

/// <summary>
/// The canonical body writer: little-endian integers, length-prefixed
/// strings (UTF-8 byte counts), and the fixed field order of each frame.
/// </summary>
public struct SyncV2Writer
{
    private byte[] buffer;

    /// <summary>Initializes an empty writer.</summary>
    public SyncV2Writer()
    {
        buffer = new byte[64];
    }

    /// <summary>The number of bytes written.</summary>
    public int Length { get; private set; }

    /// <summary>Writes a single byte.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt8(byte value)
    {
        Ensure(1);
        buffer[Length++] = value;
    }

    /// <summary>Writes a little-endian 16-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(Length, 2), value);
        Length += 2;
    }

    /// <summary>Writes a little-endian 32-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(Length, 4), value);
        Length += 4;
    }

    /// <summary>Writes a little-endian 64-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(Length, 8), value);
        Length += 8;
    }

    /// <summary>Writes a UTF-8 string with its byte length prefix, enforcing the limit.</summary>
    /// <param name="value">The string.</param>
    /// <param name="limit">The limit in bytes.</param>
    public void WriteString(string value, int limit)
    {
        if (Encoding.UTF8.GetByteCount(value) > limit)
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "string exceeds its frame limit");
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt64((ulong)bytes.Length);
        Ensure(bytes.Length);
        bytes.CopyTo(buffer, Length);
        Length += bytes.Length;
    }

    /// <summary>Writes a collection list: entry count, then each identifier.</summary>
    /// <param name="list">The list.</param>
    public void WriteStrings(IReadOnlyList<string> list)
    {
        if (list.Count > TerraScaleSyncV2Protocol.MaxCollections)
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "too many collection entries");
        }

        WriteUInt32((uint)list.Count);
        foreach (var entry in list)
        {
            WriteString(entry, TerraScaleSyncV2Protocol.MaxIdentBytes);
        }
    }

    /// <summary>Writes a checkpoint with its byte length prefix.</summary>
    /// <param name="checkpoint">The checkpoint.</param>
    public void WriteCheckpoint(TerraScaleSyncV2Checkpoint checkpoint)
    {
        var bytes = checkpoint.ToBytes();
        WriteUInt64((ulong)bytes.Length);
        Ensure(bytes.Length);
        bytes.CopyTo(buffer, Length);
        Length += bytes.Length;
    }

    /// <summary>Writes a payload with its byte length prefix.</summary>
    /// <param name="payload">The payload bytes.</param>
    public void WritePayload(byte[] payload)
    {
        WriteUInt64((ulong)payload.Length);
        Ensure(payload.Length);
        payload.CopyTo(buffer, Length);
        Length += payload.Length;
    }

    /// <summary>Writes one typed scalar value.</summary>
    /// <param name="value">The value.</param>
    public void WriteValue(TerraScaleSyncV2Value value)
    {
        switch (value.Tag)
        {
            case 0:
                WriteUInt8(0);
                break;
            case 1:
                WriteUInt8(1);
                WriteUInt8((byte)(value.Boolean ? 1 : 0));
                break;
            case 2:
                WriteUInt8(2);
                Ensure(8);
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(Length, 8), value.Integer);
                Length += 8;
                break;
            case 3:
                WriteUInt8(3);
                Ensure(8);
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(Length, 8), BitConverter.DoubleToInt64Bits(value.Number));
                Length += 8;
                break;
            case 4:
                WriteUInt8(4);
                WriteString(value.Text!, TerraScaleSyncV2Protocol.MaxIdentBytes);
                break;
            default:
                throw new TerraScaleSyncV2ProtocolException(
                    TerraScaleSyncV2ProtocolError.MalformedFrame,
                    "unknown value tag");
        }
    }

    /// <summary>Returns the written bytes.</summary>
    /// <returns>A copy of the written bytes.</returns>
    public byte[] ToBytes() => buffer[..Length];

    private void Ensure(int additional)
    {
        if (Length + additional <= buffer.Length)
        {
            return;
        }

        var grown = new byte[Math.Max(buffer.Length * 2, Length + additional)];
        buffer.CopyTo(grown, 0);
        buffer = grown;
    }
}

/// <summary>The bounds-checked body reader: every failure is the typed malformed-frame rejection.</summary>
public ref struct SyncV2Reader
{
    private readonly ReadOnlySpan<byte> bytes;
    private int offset;

    /// <summary>Initializes the reader over the bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    public SyncV2Reader(ReadOnlySpan<byte> bytes)
    {
        this.bytes = bytes;
        offset = 0;
    }

    /// <summary>Reads a single byte.</summary>
    /// <returns>The byte.</returns>
    public byte ReadUInt8() => Take(1)[0];

    /// <summary>Reads a little-endian 16-bit integer.</summary>
    /// <returns>The integer.</returns>
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    /// <summary>Reads a little-endian 32-bit integer.</summary>
    /// <returns>The integer.</returns>
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    /// <summary>Reads a little-endian 64-bit integer.</summary>
    /// <returns>The integer.</returns>
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    /// <summary>Reads a length-prefixed UTF-8 string, enforcing the limit.</summary>
    /// <param name="limit">The limit in bytes.</param>
    /// <returns>The string.</returns>
    public string ReadString(int limit)
    {
        var length = ReadBoundedLength(limit, "string exceeds its frame limit");
        var bytes = Take(length);
        if (!SyncV2Utf8.IsValid(bytes))
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "string fields are utf-8");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Reads an idempotency key's bytes (bounded by the key limit, strictly UTF-8).</summary>
    /// <returns>The key text.</returns>
    public string ReadKeyBytes()
    {
        var length = ReadBoundedLength(TerraScaleSyncV2Protocol.MaxKeyBytes, "key length disagrees with the bytes");
        var keyBytes = Take(length);
        if (!SyncV2Utf8.IsValid(keyBytes))
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "keys are utf-8");
        }

        return Encoding.UTF8.GetString(keyBytes);
    }

    /// <summary>Reads a collection list.</summary>
    /// <returns>The identifiers.</returns>
    public List<string> ReadStrings()
    {
        var count = ReadUInt32();
        if (count > TerraScaleSyncV2Protocol.MaxCollections)
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "too many collection entries");
        }

        var list = new List<string>((int)count);
        for (var index = 0; index < count; index++)
        {
            list.Add(ReadString(TerraScaleSyncV2Protocol.MaxIdentBytes));
        }

        return list;
    }

    /// <summary>Reads a length-prefixed checkpoint.</summary>
    /// <returns>The checkpoint.</returns>
    public TerraScaleSyncV2Checkpoint ReadCheckpoint()
    {
        var length = ReadUInt64();
        if (length > (ulong)int.MaxValue)
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "checkpoint length does not fit the platform");
        }

        return TerraScaleSyncV2Checkpoint.FromBytes(Take((int)length));
    }

    /// <summary>Reads a length-prefixed payload (bounded by the frame bound).</summary>
    /// <returns>The payload bytes.</returns>
    public byte[] ReadPayload()
    {
        var length = ReadBoundedLength(TerraScaleSyncV2Protocol.MaxPayloadBytes, "payload exceeds the frame bound");
        return Take(length).ToArray();
    }

    /// <summary>Reads a payload codec byte.</summary>
    /// <returns>The codec.</returns>
    public TerraScaleSyncV2PayloadCodec ReadPayloadCodec()
    {
        var code = ReadUInt8();
        return Enum.IsDefined(typeof(TerraScaleSyncV2PayloadCodec), (int)code)
            ? (TerraScaleSyncV2PayloadCodec)code
            : throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "unknown payload codec");
    }

    /// <summary>Reads an error code byte.</summary>
    /// <returns>The error code.</returns>
    public TerraScaleSyncV2ErrorCode ReadErrorCode()
    {
        var code = ReadUInt8();
        return Enum.IsDefined(typeof(TerraScaleSyncV2ErrorCode), (int)code)
            ? (TerraScaleSyncV2ErrorCode)code
            : throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "unknown error code");
    }

    /// <summary>Reads a resync reason byte.</summary>
    /// <returns>The reason.</returns>
    public TerraScaleSyncV2ResyncReason ReadResyncReason()
    {
        var code = ReadUInt8();
        return Enum.IsDefined(typeof(TerraScaleSyncV2ResyncReason), (int)code)
            ? (TerraScaleSyncV2ResyncReason)code
            : throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "unknown resync reason");
    }

    /// <summary>Reads one typed scalar value.</summary>
    /// <returns>The value.</returns>
    public TerraScaleSyncV2Value ReadValue() => ReadUInt8() switch
    {
        0 => TerraScaleSyncV2Value.Null,
        1 => TerraScaleSyncV2Value.Of(ReadUInt8() != 0),
        2 => TerraScaleSyncV2Value.Of(BinaryPrimitives.ReadInt64LittleEndian(Take(8))),
        3 => TerraScaleSyncV2Value.Of(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Take(8)))),
        4 => TerraScaleSyncV2Value.Of(ReadString(TerraScaleSyncV2Protocol.MaxIdentBytes)),
        _ => throw new TerraScaleSyncV2ProtocolException(
            TerraScaleSyncV2ProtocolError.MalformedFrame,
            "unknown value tag"),
    };

    /// <summary>Reads the frame body length, enforcing the frame bound.</summary>
    /// <returns>The body length.</returns>
    public int ReadBodyLength()
    {
        var length = ReadUInt64();
        if (length > (ulong)TerraScaleSyncV2Protocol.MaxPayloadBytes)
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "body length exceeds the frame bound");
        }

        return (int)length;
    }

    /// <summary>Reads a fixed number of bytes.</summary>
    /// <param name="length">The count.</param>
    /// <returns>The bytes.</returns>
    public ReadOnlySpan<byte> ReadBytes(int length) => Take(length);

    /// <summary>Fails when unread bytes remain.</summary>
    public void End()
    {
        if (offset != bytes.Length)
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "trailing bytes in the frame body");
        }
    }

    private int ReadBoundedLength(int limit, string refusal)
    {
        var length = ReadUInt64();
        if (length > (ulong)limit)
        {
            throw new TerraScaleSyncV2ProtocolException(TerraScaleSyncV2ProtocolError.MalformedFrame, refusal);
        }

        return (int)length;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        // Long arithmetic: a bounded count near int.MaxValue must not
        // wrap the offset check into a negative (and so pass it).
        if ((long)offset + count > bytes.Length)
        {
            throw new TerraScaleSyncV2ProtocolException(
                TerraScaleSyncV2ProtocolError.MalformedFrame,
                "truncated frame body");
        }

        var slice = bytes.Slice(offset, count);
        offset += count;
        return slice;
    }
}

/// <summary>Strict UTF-8 validation: string fields are utf-8 or rejected.</summary>
internal static class SyncV2Utf8
{
    /// <summary>Validates UTF-8 strictly (no replacement fallback).</summary>
    /// <param name="bytes">The candidate bytes.</param>
    /// <returns>Whether the bytes are well-formed UTF-8.</returns>
    internal static bool IsValid(ReadOnlySpan<byte> bytes)
    {
        // Well-formed UTF-8 per the Unicode standard: continuation bytes,
        // no overlong encodings, no surrogates, at most U+10FFFF.
        var index = 0;
        while (index < bytes.Length)
        {
            var lead = bytes[index++];
            if (lead < 0x80)
            {
                continue;
            }

            if (lead is >= 0xC2 and <= 0xDF)
            {
                if (index >= bytes.Length || (bytes[index] & 0xC0) != 0x80)
                {
                    return false;
                }

                index += 1;
            }
            else if (lead is >= 0xE0 and <= 0xEF)
            {
                if (index + 1 >= bytes.Length
                    || (bytes[index] & 0xC0) != 0x80
                    || (bytes[index + 1] & 0xC0) != 0x80
                    || (lead == 0xE0 && bytes[index] < 0xA0)
                    || (lead == 0xED && bytes[index] >= 0xA0))
                {
                    return false;
                }

                index += 2;
            }
            else if (lead is >= 0xF0 and <= 0xF4)
            {
                if (index + 2 >= bytes.Length
                    || (bytes[index] & 0xC0) != 0x80
                    || (bytes[index + 1] & 0xC0) != 0x80
                    || (bytes[index + 2] & 0xC0) != 0x80
                    || (lead == 0xF0 && bytes[index] < 0x90)
                    || (lead == 0xF4 && bytes[index] >= 0x90))
                {
                    return false;
                }

                index += 3;
            }
            else
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Compares strings by their UTF-8 byte sequences: the canonical body field order.</summary>
internal sealed class Utf8OrdinalComparer : IComparer<string>
{
    /// <summary>The shared instance.</summary>
    public static readonly Utf8OrdinalComparer Instance = new();

    /// <inheritdoc />
    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        return Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));
    }
}
