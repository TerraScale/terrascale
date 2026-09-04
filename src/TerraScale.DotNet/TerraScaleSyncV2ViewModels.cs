using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>
/// The logical session states of the native <c>terrascale.sync.v2</c> public
/// reactive protocol (ADR-0024): the client connection walks
/// <c>Connecting</c>, <c>Authenticated</c>, <c>Subscribing</c>,
/// <c>Snapshotting</c>, <c>Live</c>, <c>Resuming</c>, <c>Resetting</c>,
/// <c>Closing</c>, and <c>Closed</c>. A message received in the wrong state
/// is a typed protocol error and never allocates view work.
/// </summary>
public enum TerraScaleViewSessionState
{
    /// <summary>The session exists but no connection is established.</summary>
    Connecting,

    /// <summary>The connection is authenticated; the principal is known.</summary>
    Authenticated,

    /// <summary>A bounded view subscription is being opened.</summary>
    Subscribing,

    /// <summary>An authorized snapshot is being assembled chunk by chunk.</summary>
    Snapshotting,

    /// <summary>The view is current: snapshots and deltas have been applied and acknowledged.</summary>
    Live,

    /// <summary>A reconnect is presenting an opaque checkpoint for delta replay or reconstruction.</summary>
    Resuming,

    /// <summary>The current result is being replaced: reset, expiry, divergence, or an authorization change closed the generation.</summary>
    Resetting,

    /// <summary>The close handshake is in progress; new view work is refused.</summary>
    Closing,

    /// <summary>The session is closed. Nothing further applies.</summary>
    Closed,
}

/// <summary>The stable wire names of the ADR-0024 logical session states.</summary>
public static class TerraScaleViewSessionStateNames
{
    /// <summary>Returns the stable wire name of one logical session state.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The lowercase wire name recorded by the golden vectors.</returns>
    public static string WireName(TerraScaleViewSessionState state) => state switch
    {
        TerraScaleViewSessionState.Connecting => "connecting",
        TerraScaleViewSessionState.Authenticated => "authenticated",
        TerraScaleViewSessionState.Subscribing => "subscribing",
        TerraScaleViewSessionState.Snapshotting => "snapshotting",
        TerraScaleViewSessionState.Live => "live",
        TerraScaleViewSessionState.Resuming => "resuming",
        TerraScaleViewSessionState.Resetting => "resetting",
        TerraScaleViewSessionState.Closing => "closing",
        TerraScaleViewSessionState.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}

/// <summary>
/// The typed fail-closed errors of the client-side view engine. The names
/// are the client's own closed vocabulary: the server-typed results of
/// ADR-0024 (<c>ViewDiverged</c>, <c>CheckpointExpired</c>, reset) are
/// delivery outcomes, not exceptions.
/// </summary>
public enum TerraScaleViewProtocolError
{
    /// <summary>The message does not belong to the session's current logical state.</summary>
    WrongState,

    /// <summary>A snapshot frame is structurally invalid: bad identity, broken chunk ordinals, over-bound assembly, or a missing digest.</summary>
    MalformedSnapshot,

    /// <summary>A result delta is structurally invalid: bad generation, regression, over-bound result, or an incoherent operation list.</summary>
    MalformedDelta,

    /// <summary>The optimistic presentation identity is not usable: empty, over its bound, duplicated while in flight, or unknown on completion.</summary>
    InvalidOptimisticMutation,

    /// <summary>An optimistic mutation was attempted while disconnected.</summary>
    Offline,

    /// <summary>An authorization generation was stale or malformed.</summary>
    InvalidAuthorization,
}

/// <summary>The typed fail-closed rejection of the client-side view engine.</summary>
public sealed class TerraScaleViewProtocolException : Exception
{
    /// <summary>Initializes the rejection.</summary>
    /// <param name="error">The typed error.</param>
    /// <param name="reason">The exact reason text.</param>
    public TerraScaleViewProtocolException(TerraScaleViewProtocolError error, string reason)
        : base(reason)
    {
        Error = error;
        Reason = reason;
    }

    /// <summary>The typed error.</summary>
    public TerraScaleViewProtocolError Error { get; }

    /// <summary>The exact reason text.</summary>
    public string Reason { get; }

    /// <summary>The stable wire name of the typed error.</summary>
    public string WireName => Error switch
    {
        TerraScaleViewProtocolError.WrongState => "wrong_state",
        TerraScaleViewProtocolError.MalformedSnapshot => "malformed_snapshot",
        TerraScaleViewProtocolError.MalformedDelta => "malformed_delta",
        TerraScaleViewProtocolError.InvalidOptimisticMutation => "invalid_optimistic_mutation",
        TerraScaleViewProtocolError.Offline => "offline",
        TerraScaleViewProtocolError.InvalidAuthorization => "invalid_authorization",
        _ => "wrong_state",
    };
}

/// <summary>
/// The unpredictable 128-bit server identity of one view instance
/// (ADR-0024 canonical view identity). It is opaque to the client.
/// </summary>
public sealed record TerraScaleViewId
{
    /// <summary>Initializes the view identity.</summary>
    /// <param name="value">The 128-bit server identity.</param>
    public TerraScaleViewId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("The view identity must not be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The 128-bit server identity.</summary>
    public Guid Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// The identity of one result row: the document identity including its
/// incarnation (ADR-0024). A recreated document never aliases the deleted
/// incarnation in the client cache.
/// </summary>
public sealed record TerraScaleViewIdentity
{
    /// <summary>Initializes the row identity.</summary>
    /// <param name="documentId">The stable document identity.</param>
    /// <param name="incarnation">The document incarnation: bumped on recreate or restore.</param>
    public TerraScaleViewIdentity(string documentId, ulong incarnation)
    {
        if (string.IsNullOrWhiteSpace(documentId) || Encoding.UTF8.GetByteCount(documentId) > 256)
        {
            throw new ArgumentException("The document identity must not be empty.", nameof(documentId));
        }

        DocumentId = documentId;
        Incarnation = incarnation;
    }

    /// <summary>The stable document identity.</summary>
    public string DocumentId { get; }

    /// <summary>The document incarnation.</summary>
    public ulong Incarnation { get; }

    /// <inheritdoc />
    public override string ToString() => $"{DocumentId}:{Incarnation}";
}

/// <summary>
/// The canonical position of one row in the ordered result: the canonical
/// query sort key. The engine orders rows by position with the identity as
/// the deterministic tie breaker; offset pagination is not reactive state.
/// </summary>
public sealed record TerraScaleViewPosition : IComparable<TerraScaleViewPosition>
{
    /// <summary>Initializes the position.</summary>
    /// <param name="key">The opaque canonical sort key.</param>
    public TerraScaleViewPosition(string key)
    {
        if (string.IsNullOrEmpty(key) || Encoding.UTF8.GetByteCount(key) > 256)
        {
            throw new ArgumentException("The position key must not be empty.", nameof(key));
        }

        Key = key;
    }

    /// <summary>The opaque canonical sort key.</summary>
    public string Key { get; }

    /// <summary>Compares two positions by their canonical key order.</summary>
    /// <param name="other">The other position.</param>
    /// <returns>The ordinal comparison of the keys.</returns>
    public int CompareTo(TerraScaleViewPosition? other) => other is null ? 1 : string.CompareOrdinal(Key, other.Key);

    /// <inheritdoc />
    public override string ToString() => Key;
}

/// <summary>
/// The server-stated digest of the complete logical result at one revision
/// (ADR-0024). It covers the whole result, not only the operations of one
/// frame. The client adopts it after a validated apply and echoes it in
/// <see cref="TerraScaleViewAck"/>; the server detects divergence.
/// </summary>
public sealed record TerraScaleViewDigest
{
    /// <summary>Initializes the digest.</summary>
    /// <param name="value">The opaque digest value.</param>
    public TerraScaleViewDigest(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 256)
        {
            throw new ArgumentException("The digest must not be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The opaque digest value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// The opaque causal cut a result is bound to (ADR-0024). The public
/// protocol carries only opaque public causal session tokens, never member
/// progress clocks or native replication vectors.
/// </summary>
public sealed record TerraScaleViewCausalCut
{
    /// <summary>Initializes the causal cut.</summary>
    /// <param name="token">The opaque public causal session token.</param>
    public TerraScaleViewCausalCut(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || Encoding.UTF8.GetByteCount(token) > 4096)
        {
            throw new ArgumentException("The causal cut token must not be empty.", nameof(token));
        }

        Token = token;
    }

    /// <summary>The opaque public causal session token.</summary>
    public string Token { get; }
}

/// <summary>
/// An opaque, versioned, integrity-protected view checkpoint (ADR-0024).
/// It is an authenticated capability to <i>request</i> resume — never an
/// authorization grant, never a native storage payload, and never a Region
/// replication vector. The client persists it only after atomically
/// applying and validating the corresponding snapshot or delta.
/// </summary>
public sealed record TerraScaleViewCheckpoint
{
    /// <summary>Initializes the checkpoint from its opaque token.</summary>
    /// <param name="token">The opaque public checkpoint token.</param>
    public TerraScaleViewCheckpoint(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || Encoding.UTF8.GetByteCount(token) > 16 * 1024)
        {
            throw new ArgumentException("The checkpoint token must not be empty.", nameof(token));
        }

        Token = token;
    }

    /// <summary>The opaque public checkpoint token.</summary>
    public string Token { get; }

    /// <inheritdoc />
    public override string ToString() => Token;
}

/// <summary>The kind of one canonical patch step (ADR-0024 result operations).</summary>
public enum TerraScaleViewPatchStepKind
{
    /// <summary>Replace the value at the path.</summary>
    Replace,

    /// <summary>Remove the value at the path.</summary>
    Remove,
}

/// <summary>
/// One step of a <see cref="TerraScaleViewPatch"/> operation. The path is
/// the canonical typed path encoding of ADR-0022; this model carries it as
/// slash-separated segments until the wire binding fixes its bytes.
/// </summary>
public sealed record TerraScaleViewPatchStep
{
    /// <summary>Initializes the patch step.</summary>
    /// <param name="path">The canonical slash-separated path.</param>
    /// <param name="kind">The step kind.</param>
    /// <param name="value">The replacement value, when the kind is <c>Replace</c>.</param>
    public TerraScaleViewPatchStep(string path, TerraScaleViewPatchStepKind kind, JsonElement? value = null)
    {
        if (string.IsNullOrWhiteSpace(path) || Encoding.UTF8.GetByteCount(path) > 256)
        {
            throw new ArgumentException("The patch path must not be empty.", nameof(path));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind == TerraScaleViewPatchStepKind.Replace && value is null)
        {
            throw new ArgumentException("A replace patch step requires a value.", nameof(value));
        }

        if (value is { } element && Encoding.UTF8.GetByteCount(element.GetRawText()) > 64 * 1024)
        {
            throw new ArgumentException("The replacement value exceeds the 64 KiB bound.", nameof(value));
        }

        Path = path;
        Kind = kind;
        Value = value is { } replacement ? TerraScaleViewJson.Clone(replacement) : null;
    }

    /// <summary>The canonical slash-separated path.</summary>
    public string Path { get; }

    /// <summary>The step kind.</summary>
    public TerraScaleViewPatchStepKind Kind { get; }

    /// <summary>The replacement value, when the kind is <c>Replace</c>.</summary>
    public JsonElement? Value { get; }
}

/// <summary>The discriminator of one ADR-0024 result operation.</summary>
public enum TerraScaleViewResultOperationKind
{
    /// <summary>Insert a new row identity at a position.</summary>
    Insert,

    /// <summary>Replace the value of an existing row identity.</summary>
    Update,

    /// <summary>Apply canonical path operations to an existing row identity.</summary>
    Patch,

    /// <summary>Remove an existing row identity.</summary>
    Remove,

    /// <summary>Move an existing row identity to a new position.</summary>
    Move,

    /// <summary>Replace the complete result with the descriptor's rows.</summary>
    Reset,
}

/// <summary>
/// One ADR-0024 result operation. Identity — not array position — selects
/// an existing row; ordering is the canonical query sort with the identity
/// tie breaker.
/// </summary>
public abstract record TerraScaleViewResultOperation
{
    /// <summary>The operation discriminator.</summary>
    public abstract TerraScaleViewResultOperationKind Kind { get; }
}

/// <summary>Inserts a new row identity at a canonical position.</summary>
public sealed record TerraScaleViewInsert : TerraScaleViewResultOperation
{
    /// <summary>Initializes the insert.</summary>
    /// <param name="identity">The new row identity.</param>
    /// <param name="position">The canonical position.</param>
    /// <param name="value">The row value.</param>
    public TerraScaleViewInsert(TerraScaleViewIdentity identity, TerraScaleViewPosition position, JsonElement value)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Position = position ?? throw new ArgumentNullException(nameof(position));
        Value = TerraScaleViewJson.Clone(value);
    }

    /// <inheritdoc />
    public override TerraScaleViewResultOperationKind Kind => TerraScaleViewResultOperationKind.Insert;

    /// <summary>The new row identity.</summary>
    public TerraScaleViewIdentity Identity { get; }

    /// <summary>The canonical position.</summary>
    public TerraScaleViewPosition Position { get; }

    /// <summary>The row value.</summary>
    public JsonElement Value { get; }
}

/// <summary>Replaces the value of an existing row identity.</summary>
public sealed record TerraScaleViewUpdate : TerraScaleViewResultOperation
{
    /// <summary>Initializes the update.</summary>
    /// <param name="identity">The existing row identity.</param>
    /// <param name="value">The complete replacement value.</param>
    public TerraScaleViewUpdate(TerraScaleViewIdentity identity, JsonElement value)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Value = TerraScaleViewJson.Clone(value);
    }

    /// <inheritdoc />
    public override TerraScaleViewResultOperationKind Kind => TerraScaleViewResultOperationKind.Update;

    /// <summary>The existing row identity.</summary>
    public TerraScaleViewIdentity Identity { get; }

    /// <summary>The complete replacement value.</summary>
    public JsonElement Value { get; }
}

/// <summary>Applies canonical path operations to an existing row identity.</summary>
public sealed record TerraScaleViewPatch : TerraScaleViewResultOperation
{
    /// <summary>Initializes the patch.</summary>
    /// <param name="identity">The existing row identity.</param>
    /// <param name="steps">The canonical path operations, in order.</param>
    public TerraScaleViewPatch(TerraScaleViewIdentity identity, IReadOnlyList<TerraScaleViewPatchStep> steps)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (steps is null)
        {
            throw new ArgumentNullException(nameof(steps));
        }

        if (steps.Count is 0 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), "A patch must contain 1-1024 steps.");
        }

        Steps = [.. steps];
    }

    /// <inheritdoc />
    public override TerraScaleViewResultOperationKind Kind => TerraScaleViewResultOperationKind.Patch;

    /// <summary>The existing row identity.</summary>
    public TerraScaleViewIdentity Identity { get; }

    /// <summary>The canonical path operations, in order.</summary>
    public IReadOnlyList<TerraScaleViewPatchStep> Steps { get; }
}

/// <summary>Removes an existing row identity from the result.</summary>
public sealed record TerraScaleViewRemove : TerraScaleViewResultOperation
{
    /// <summary>Initializes the removal.</summary>
    /// <param name="identity">The existing row identity.</param>
    public TerraScaleViewRemove(TerraScaleViewIdentity identity)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    /// <inheritdoc />
    public override TerraScaleViewResultOperationKind Kind => TerraScaleViewResultOperationKind.Remove;

    /// <summary>The existing row identity.</summary>
    public TerraScaleViewIdentity Identity { get; }
}

/// <summary>Moves an existing row identity from one canonical position to another.</summary>
public sealed record TerraScaleViewMove : TerraScaleViewResultOperation
{
    /// <summary>Initializes the move.</summary>
    /// <param name="identity">The existing row identity.</param>
    /// <param name="oldPosition">The position the server asserts the row currently occupies.</param>
    /// <param name="newPosition">The position the row moves to.</param>
    public TerraScaleViewMove(TerraScaleViewIdentity identity, TerraScaleViewPosition oldPosition, TerraScaleViewPosition newPosition)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        OldPosition = oldPosition ?? throw new ArgumentNullException(nameof(oldPosition));
        NewPosition = newPosition ?? throw new ArgumentNullException(nameof(newPosition));
    }

    /// <inheritdoc />
    public override TerraScaleViewResultOperationKind Kind => TerraScaleViewResultOperationKind.Move;

    /// <summary>The existing row identity.</summary>
    public TerraScaleViewIdentity Identity { get; }

    /// <summary>The position the server asserts the row currently occupies.</summary>
    public TerraScaleViewPosition OldPosition { get; }

    /// <summary>The position the row moves to.</summary>
    public TerraScaleViewPosition NewPosition { get; }
}

/// <summary>
/// Replaces the complete logical result with the snapshot descriptor's
/// rows (ADR-0024 <c>Reset</c>): a correct bounded fallback, never an
/// error and never a guessed incremental patch.
/// </summary>
public sealed record TerraScaleViewReset : TerraScaleViewResultOperation
{
    /// <summary>Initializes the reset.</summary>
    /// <param name="rows">The complete replacement result, in canonical order.</param>
    public TerraScaleViewReset(IReadOnlyList<TerraScaleViewRow> rows)
    {
        if (rows is null)
        {
            throw new ArgumentNullException(nameof(rows));
        }

        if (rows.Count > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), "A reset is limited to 10000 rows.");
        }

        Rows = [.. rows];
    }

    /// <inheritdoc />
    public override TerraScaleViewResultOperationKind Kind => TerraScaleViewResultOperationKind.Reset;

    /// <summary>The complete replacement result, in canonical order.</summary>
    public IReadOnlyList<TerraScaleViewRow> Rows { get; }
}

/// <summary>One row of an authorized query result.</summary>
public sealed record TerraScaleViewRow
{
    /// <summary>Initializes the row.</summary>
    /// <param name="identity">The row identity, including incarnation.</param>
    /// <param name="position">The canonical position.</param>
    /// <param name="value">The row value.</param>
    public TerraScaleViewRow(TerraScaleViewIdentity identity, TerraScaleViewPosition position, JsonElement value)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Position = position ?? throw new ArgumentNullException(nameof(position));
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > 64 * 1024)
        {
            throw new ArgumentException("The row value exceeds the 64 KiB bound.", nameof(value));
        }

        Value = TerraScaleViewJson.Clone(value);
    }

    /// <summary>The row identity, including incarnation.</summary>
    public TerraScaleViewIdentity Identity { get; }

    /// <summary>The canonical position.</summary>
    public TerraScaleViewPosition Position { get; }

    /// <summary>The row value.</summary>
    public JsonElement Value { get; }
}

/// <summary>
/// The opening frame of an authorized snapshot (ADR-0024
/// <c>SnapshotBegin</c>). The declared row and byte bounds are the view's
/// result budget: the engine fails closed when an assembly would exceed
/// them.
/// </summary>
public sealed record TerraScaleViewSnapshotBegin
{
    /// <summary>Initializes the frame.</summary>
    /// <param name="viewId">The view instance identity.</param>
    /// <param name="viewGeneration">The view generation.</param>
    /// <param name="baseRevision">The revision the snapshot materializes.</param>
    /// <param name="causalCut">The causal cut the result is bound to.</param>
    /// <param name="schemaGeneration">The schema generation the plan bound.</param>
    /// <param name="resultShape">The canonical result shape identity.</param>
    /// <param name="declaredRowBound">The declared maximum row count of the result.</param>
    /// <param name="declaredByteBound">The declared maximum logical byte size of the result.</param>
    public TerraScaleViewSnapshotBegin(
        TerraScaleViewId viewId,
        ulong viewGeneration,
        ulong baseRevision,
        TerraScaleViewCausalCut causalCut,
        ulong schemaGeneration,
        string resultShape,
        int declaredRowBound,
        long declaredByteBound)
    {
        ViewId = viewId ?? throw new ArgumentNullException(nameof(viewId));
        CausalCut = causalCut ?? throw new ArgumentNullException(nameof(causalCut));
        if (string.IsNullOrWhiteSpace(resultShape) || Encoding.UTF8.GetByteCount(resultShape) > 256)
        {
            throw new ArgumentException("The result shape must not be empty.", nameof(resultShape));
        }

        if (declaredRowBound is <= 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(declaredRowBound), "The declared row bound must be positive.");
        }

        if (declaredByteBound is <= 0 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(declaredByteBound), "The declared byte bound must be positive.");
        }

        ViewGeneration = viewGeneration;
        BaseRevision = baseRevision;
        SchemaGeneration = schemaGeneration;
        ResultShape = resultShape;
        DeclaredRowBound = declaredRowBound;
        DeclaredByteBound = declaredByteBound;
    }

    /// <summary>The view instance identity.</summary>
    public TerraScaleViewId ViewId { get; }

    /// <summary>The view generation.</summary>
    public ulong ViewGeneration { get; }

    /// <summary>The revision the snapshot materializes.</summary>
    public ulong BaseRevision { get; }

    /// <summary>The causal cut the result is bound to.</summary>
    public TerraScaleViewCausalCut CausalCut { get; }

    /// <summary>The schema generation the plan bound.</summary>
    public ulong SchemaGeneration { get; }

    /// <summary>The canonical result shape identity.</summary>
    public string ResultShape { get; }

    /// <summary>The declared maximum row count of the result.</summary>
    public int DeclaredRowBound { get; }

    /// <summary>The declared maximum logical byte size of the result.</summary>
    public long DeclaredByteBound { get; }
}

/// <summary>
/// One bounded chunk of an authorized snapshot (ADR-0024
/// <c>SnapshotChunk</c>). Chunks carry one-based contiguous ordinals; the
/// chunk hash is the server's integrity statement over the chunk.
/// </summary>
public sealed record TerraScaleViewSnapshotChunk
{
    /// <summary>Initializes the frame.</summary>
    /// <param name="viewGeneration">The view generation.</param>
    /// <param name="chunkOrdinal">The one-based chunk ordinal.</param>
    /// <param name="rows">The chunk's rows.</param>
    /// <param name="chunkHash">The server's hash over the chunk.</param>
    public TerraScaleViewSnapshotChunk(ulong viewGeneration, int chunkOrdinal, IReadOnlyList<TerraScaleViewRow> rows, string chunkHash)
    {
        if (chunkOrdinal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkOrdinal), "The chunk ordinal is one-based.");
        }

        if (string.IsNullOrWhiteSpace(chunkHash) || Encoding.UTF8.GetByteCount(chunkHash) > 256)
        {
            throw new ArgumentException("The chunk hash must not be empty.", nameof(chunkHash));
        }

        ViewGeneration = viewGeneration;
        ChunkOrdinal = chunkOrdinal;
        Rows = rows is null ? throw new ArgumentNullException(nameof(rows)) : [.. rows];
        ChunkHash = chunkHash;
    }

    /// <summary>The view generation.</summary>
    public ulong ViewGeneration { get; }

    /// <summary>The one-based chunk ordinal.</summary>
    public int ChunkOrdinal { get; }

    /// <summary>The chunk's rows.</summary>
    public IReadOnlyList<TerraScaleViewRow> Rows { get; }

    /// <summary>The server's hash over the chunk.</summary>
    public string ChunkHash { get; }
}

/// <summary>
/// The closing frame of an authorized snapshot (ADR-0024
/// <c>SnapshotEnd</c>). The result digest and checkpoint are adopted only
/// when the complete assembly validated.
/// </summary>
public sealed record TerraScaleViewSnapshotEnd
{
    /// <summary>Initializes the the frame.</summary>
    /// <param name="viewGeneration">The view generation.</param>
    /// <param name="baseRevision">The revision the snapshot materializes; must match the begin frame.</param>
    /// <param name="resultDigest">The digest of the complete logical result.</param>
    /// <param name="checkpoint">The checkpoint bound to this snapshot.</param>
    public TerraScaleViewSnapshotEnd(ulong viewGeneration, ulong baseRevision, TerraScaleViewDigest resultDigest, TerraScaleViewCheckpoint checkpoint)
    {
        ResultDigest = resultDigest ?? throw new ArgumentNullException(nameof(resultDigest));
        Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
        ViewGeneration = viewGeneration;
        BaseRevision = baseRevision;
    }

    /// <summary>The view generation.</summary>
    public ulong ViewGeneration { get; }

    /// <summary>The revision the snapshot materializes; must match the begin frame.</summary>
    public ulong BaseRevision { get; }

    /// <summary>The digest of the complete logical result.</summary>
    public TerraScaleViewDigest ResultDigest { get; }

    /// <summary>The checkpoint bound to this snapshot.</summary>
    public TerraScaleViewCheckpoint Checkpoint { get; }
}

/// <summary>
/// One incremental result frame (ADR-0024 <c>ResultDelta</c>). One
/// committed transaction produces zero or one atomic revision per affected
/// view; several commits may be coalesced when applying the final revision
/// is identical to applying each omitted intermediate.
/// </summary>
public sealed record TerraScaleViewResultDelta
{
    /// <summary>Initializes the frame.</summary>
    /// <param name="viewId">The view instance identity.</param>
    /// <param name="viewGeneration">The view generation.</param>
    /// <param name="fromRevision">The revision the operations apply from.</param>
    /// <param name="toRevision">The revision the operations apply to.</param>
    /// <param name="causalCut">The causal cut the result is bound to.</param>
    /// <param name="operations">The atomic result operations.</param>
    /// <param name="resultDigest">The digest of the complete logical result at <paramref name="toRevision"/>.</param>
    /// <param name="checkpoint">The checkpoint bound to this revision.</param>
    public TerraScaleViewResultDelta(
        TerraScaleViewId viewId,
        ulong viewGeneration,
        ulong fromRevision,
        ulong toRevision,
        TerraScaleViewCausalCut causalCut,
        IReadOnlyList<TerraScaleViewResultOperation> operations,
        TerraScaleViewDigest resultDigest,
        TerraScaleViewCheckpoint checkpoint)
    {
        ViewId = viewId ?? throw new ArgumentNullException(nameof(viewId));
        CausalCut = causalCut ?? throw new ArgumentNullException(nameof(causalCut));
        if (operations is null)
        {
            throw new ArgumentNullException(nameof(operations));
        }

        if (operations.Count > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(operations), "A result delta is limited to 1024 operations.");
        }

        Operations = [.. operations];
        ResultDigest = resultDigest ?? throw new ArgumentNullException(nameof(resultDigest));
        Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
        ViewGeneration = viewGeneration;
        FromRevision = fromRevision;
        ToRevision = toRevision;
    }

    /// <summary>The view instance identity.</summary>
    public TerraScaleViewId ViewId { get; }

    /// <summary>The view generation.</summary>
    public ulong ViewGeneration { get; }

    /// <summary>The revision the operations apply from.</summary>
    public ulong FromRevision { get; }

    /// <summary>The revision the operations apply to.</summary>
    public ulong ToRevision { get; }

    /// <summary>The causal cut the result is bound to.</summary>
    public TerraScaleViewCausalCut CausalCut { get; }

    /// <summary>The atomic result operations.</summary>
    public IReadOnlyList<TerraScaleViewResultOperation> Operations { get; }

    /// <summary>The digest of the complete logical result at <see cref="ToRevision"/>.</summary>
    public TerraScaleViewDigest ResultDigest { get; }

    /// <summary>The checkpoint bound to this revision.</summary>
    public TerraScaleViewCheckpoint Checkpoint { get; }
}

/// <summary>
/// The client acknowledgement of one applied revision (ADR-0024
/// <c>ViewAck</c>). It releases per-subscriber delivery buffers; it never
/// advances replication progress or proves authorization.
/// </summary>
public sealed record TerraScaleViewAck
{
    /// <summary>Initializes the acknowledgement.</summary>
    /// <param name="viewId">The view instance identity.</param>
    /// <param name="viewGeneration">The view generation.</param>
    /// <param name="appliedRevision">The revision the client applied and validated.</param>
    /// <param name="resultDigest">The digest the client adopted at that revision.</param>
    public TerraScaleViewAck(TerraScaleViewId viewId, ulong viewGeneration, ulong appliedRevision, TerraScaleViewDigest resultDigest)
    {
        ViewId = viewId ?? throw new ArgumentNullException(nameof(viewId));
        ResultDigest = resultDigest ?? throw new ArgumentNullException(nameof(resultDigest));
        ViewGeneration = viewGeneration;
        AppliedRevision = appliedRevision;
    }

    /// <summary>The view instance identity.</summary>
    public TerraScaleViewId ViewId { get; }

    /// <summary>The view generation.</summary>
    public ulong ViewGeneration { get; }

    /// <summary>The revision the client applied and validated.</summary>
    public ulong AppliedRevision { get; }

    /// <summary>The digest the client adopted at that revision.</summary>
    public TerraScaleViewDigest ResultDigest { get; }
}

/// <summary>
/// The effect an authorization generation change has on one subscribed
/// view (ADR-0024 authorization transitions).
/// </summary>
public enum TerraScaleViewAuthorizationEffect
{
    /// <summary>Visibility was lost: removals or a fresh excluding snapshot follow.</summary>
    VisibilityLost,

    /// <summary>Visibility was gained: a bounded authorized diff or a fresh snapshot follows.</summary>
    VisibilityGained,

    /// <summary>The field projection was reduced: forbidden fields leave even when data did not change.</summary>
    FieldProjectionReduced,

    /// <summary>The session was revoked: it closes after the removal or fail-closed boundary policy requires.</summary>
    SessionRevoked,
}

/// <summary>The typed outcome of one delivery applied to the view engine.</summary>
public enum TerraScaleViewDeliveryOutcome
{
    /// <summary>A complete snapshot validated and was published to the cache atomically.</summary>
    SnapshotPublished,

    /// <summary>A result delta applied atomically and advanced the revision.</summary>
    DeltaApplied,

    /// <summary>A <c>Reset</c> operation replaced the complete result.</summary>
    ResetApplied,

    /// <summary>A delta whose <c>to_revision</c> was already applied was ignored (at-least-once duplicate).</summary>
    DuplicateDeltaIgnored,

    /// <summary>The delta's <c>from_revision</c> does not match the applied revision: resume or reset is required.</summary>
    RevisionGapDetected,

    /// <summary>The view diverged: the generation is closed and an authorized snapshot is required.</summary>
    ViewDiverged,

    /// <summary>The checkpoint fell below the retained floor: an automatic authorized bootstrap follows.</summary>
    CheckpointExpired,

    /// <summary>The server signaled a reset: a fresh snapshot follows.</summary>
    SessionReset,

    /// <summary>The session was revoked: it drops its checkpoint and closes.</summary>
    SessionRevoked,

    /// <summary>A new view generation opened (authorization change or subscription expansion); the old generation is closed.</summary>
    ViewGenerationAdvanced,
}

/// <summary>
/// The typed result of one delivery: the outcome, the session state it
/// produced, and the acknowledgement to send when one was produced.
/// </summary>
public sealed record TerraScaleViewDelivery
{
    /// <summary>Initializes the delivery result.</summary>
    /// <param name="outcome">The typed outcome.</param>
    /// <param name="state">The session state after the delivery.</param>
    /// <param name="ack">The acknowledgement to send, when one was produced.</param>
    public TerraScaleViewDelivery(TerraScaleViewDeliveryOutcome outcome, TerraScaleViewSessionState state, TerraScaleViewAck? ack = null)
    {
        Outcome = outcome;
        State = state;
        Ack = ack;
    }

    /// <summary>The typed outcome.</summary>
    public TerraScaleViewDeliveryOutcome Outcome { get; }

    /// <summary>The session state after the delivery.</summary>
    public TerraScaleViewSessionState State { get; }

    /// <summary>The acknowledgement to send, when one was produced.</summary>
    public TerraScaleViewAck? Ack { get; }
}

/// <summary>
/// An immutable materialized view result: the ordered rows at the applied
/// revision, with the digest and whether an optimistic presentation layer
/// is currently coloring the rows.
/// </summary>
public sealed record TerraScaleViewResult
{
    /// <summary>Initializes the result.</summary>
    /// <param name="viewId">The view instance identity.</param>
    /// <param name="viewGeneration">The view generation.</param>
    /// <param name="revision">The applied revision.</param>
    /// <param name="rows">The ordered rows at the revision.</param>
    /// <param name="resultDigest">The adopted digest at the revision.</param>
    /// <param name="optimistic">Whether one or more optimistic presentation layers affect the rows.</param>
    public TerraScaleViewResult(
        TerraScaleViewId viewId,
        ulong viewGeneration,
        ulong revision,
        IReadOnlyList<TerraScaleViewRow> rows,
        TerraScaleViewDigest resultDigest,
        bool optimistic)
    {
        ViewId = viewId ?? throw new ArgumentNullException(nameof(viewId));
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));
        ResultDigest = resultDigest ?? throw new ArgumentNullException(nameof(resultDigest));
        ViewGeneration = viewGeneration;
        Revision = revision;
        Optimistic = optimistic;
    }

    /// <summary>The view instance identity.</summary>
    public TerraScaleViewId ViewId { get; }

    /// <summary>The view generation.</summary>
    public ulong ViewGeneration { get; }

    /// <summary>The applied revision.</summary>
    public ulong Revision { get; }

    /// <summary>The ordered rows at the revision.</summary>
    public IReadOnlyList<TerraScaleViewRow> Rows { get; }

    /// <summary>The adopted digest at the revision.</summary>
    public TerraScaleViewDigest ResultDigest { get; }

    /// <summary>Whether one or more optimistic presentation layers affect the rows.</summary>
    public bool Optimistic { get; }
}

/// <summary>Internal JSON clone helpers for view models.</summary>
internal static class TerraScaleViewJson
{
    /// <summary>Clones one element so it is safe beyond the caller's document lifetime.</summary>
    /// <param name="element">The element to clone.</param>
    /// <returns>The cloned element.</returns>
    internal static JsonElement Clone(JsonElement element) => element.Clone();
}
