using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraScale.DotNet;

/// <summary>
/// The native <c>terrascale.sync.v1</c> client-side view engine
/// (ADR-0024): one server-authoritative authorized query result, its
/// logical session states, its snapshot assembly, its result revisions,
/// its opaque checkpoint, its acknowledgements, and its reversible
/// optimistic presentation.
///
/// The engine implements the published state-machine semantics only. It
/// owns no transport, no MessagePack framing, and no route: the public
/// wire binding lands with the owning public contract and the
/// engine-owned golden corpus, and conformance is claimed only through
/// that corpus. Every mutation is online and server-authoritative; the
/// optimistic layer is presentation that never advances a revision or a
/// checkpoint and never becomes durable state.
/// </summary>
public sealed class TerraScaleSyncV1ViewEngine
{
    private const int MaxIdempotencyKeyBytes = 128;

    private readonly object gate = new();
    private readonly Dictionary<TerraScaleViewIdentity, TerraScaleViewRow> rows = [];
    private readonly List<OptimisticLayer> optimisticLayers = [];
    private readonly HashSet<string> completedMutations = new(StringComparer.Ordinal);

    private TerraScaleViewSessionState state = TerraScaleViewSessionState.Connecting;
    private TerraScaleViewId? viewId;
    private ulong viewGeneration;
    private ulong authorizationGeneration;
    private ulong appliedRevision;
    private TerraScaleViewDigest? resultDigest;
    private TerraScaleViewCheckpoint? checkpoint;
    private TerraScaleViewCausalCut? causalCut;
    private int declaredRowBound;
    private long declaredLogicalByteBound;
    private long appliedLogicalBytes;
    private bool generationAdvanceExpected;
    private bool online = true;
    private SnapshotAssembly? assembly;

    private sealed record OptimisticLayer(string IdempotencyId, List<TerraScaleViewResultOperation> Operations);

    private sealed class SnapshotAssembly(TerraScaleViewSnapshotBegin begin)
    {
        public TerraScaleViewSnapshotBegin Begin { get; } = begin;

        public List<TerraScaleViewRow> Rows { get; } = [];

        public HashSet<TerraScaleViewIdentity> Identities { get; } = [];

        public int NextOrdinal { get; set; } = 1;

        public long LogicalBytes { get; set; }
    }

    private sealed record WorkingMutation(TerraScaleViewRow? Row, bool Removed);

    private sealed record DeltaPlan(
        Dictionary<TerraScaleViewIdentity, WorkingMutation> Working,
        TerraScaleViewReset? Reset,
        long LogicalBytes);

    /// <summary>Initializes a fresh engine in the <c>Connecting</c> state: no view, no cache, no checkpoint.</summary>
    public TerraScaleSyncV1ViewEngine()
    {
    }

    /// <summary>The current logical session state (ADR-0024).</summary>
    public TerraScaleViewSessionState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    /// <summary>The view instance identity, or <c>null</c> before the first validated publish.</summary>
    public TerraScaleViewId? ViewId
    {
        get
        {
            lock (gate)
            {
                return viewId;
            }
        }
    }

    /// <summary>The view generation, or zero before the first validated publish.</summary>
    public ulong ViewGeneration
    {
        get
        {
            lock (gate)
            {
                return viewGeneration;
            }
        }
    }

    /// <summary>The authorization generation last observed for this view.</summary>
    public ulong AuthorizationGeneration
    {
        get
        {
            lock (gate)
            {
                return authorizationGeneration;
            }
        }
    }

    /// <summary>The applied result revision, or zero before the first validated publish.</summary>
    public ulong AppliedRevision
    {
        get
        {
            lock (gate)
            {
                return appliedRevision;
            }
        }
    }

    /// <summary>
    /// The adopted result digest, or <c>null</c> before the first
    /// validated publish. Adopted only with the revision it states.
    /// </summary>
    public TerraScaleViewDigest? ResultDigest
    {
        get
        {
            lock (gate)
            {
                return resultDigest;
            }
        }
    }

    /// <summary>
    /// The durable resume checkpoint, or <c>null</c> when none is usable.
    /// It is adopted only after a snapshot or delta applied and validated
    /// atomically, and it is dropped fail-closed on expiry, divergence,
    /// revocation, and view-generation advance. A checkpoint never grants
    /// access: reconnect always reevaluates current authorization.
    /// </summary>
    public TerraScaleViewCheckpoint? Checkpoint
    {
        get
        {
            lock (gate)
            {
                return checkpoint;
            }
        }
    }

    /// <summary>The last causal cut the server bound a delivered result to, or <c>null</c>.</summary>
    public TerraScaleViewCausalCut? CausalCut
    {
        get
        {
            lock (gate)
            {
                return causalCut;
            }
        }
    }

    /// <summary>The current presentation: authoritative rows with the optimistic layers applied.</summary>
    public TerraScaleViewResult? CurrentView
    {
        get
        {
            lock (gate)
            {
                return viewId is null || resultDigest is null ? null : Materialize(optimistic: true);
            }
        }
    }

    /// <summary>The authoritative result alone: no optimistic presentation colors these rows.</summary>
    public TerraScaleViewResult? AuthoritativeView
    {
        get
        {
            lock (gate)
            {
                return viewId is null || resultDigest is null ? null : Materialize(optimistic: false);
            }
        }
    }

    /// <summary>How many complete snapshots published atomically to the cache.</summary>
    public ulong SnapshotsPublished { get; private set; }

    /// <summary>How many result deltas applied and advanced the revision.</summary>
    public ulong DeltasApplied { get; private set; }

    /// <summary>How many <c>Reset</c> operations replaced the complete result.</summary>
    public ulong ResetsApplied { get; private set; }

    /// <summary>How many at-least-once duplicate deltas were ignored (<c>to_revision</c> already applied).</summary>
    public ulong DuplicateDeltasIgnored { get; private set; }

    /// <summary>How many deltas arrived with a <c>from_revision</c> gap and applied nothing.</summary>
    public ulong RevisionGapsDetected { get; private set; }

    /// <summary>How many divergences were detected (client-detected or server-signaled).</summary>
    public ulong DivergencesDetected { get; private set; }

    /// <summary>How many in-flight optimistic mutations are coloring the current presentation.</summary>
    public int OptimisticMutationsPending
    {
        get
        {
            lock (gate)
            {
                return optimisticLayers.Count;
            }
        }
    }

    /// <summary>Whether online server-authoritative mutations may be started.</summary>
    public bool IsOnline
    {
        get
        {
            lock (gate)
            {
                return online;
            }
        }
    }

    /// <summary>
    /// Sets connectivity for the semantic engine. Disconnected clients may
    /// continue to render cached results, but cannot queue or create durable
    /// mutations; callers must retry the same idempotency identity online.
    /// </summary>
    /// <param name="connected">Whether the server connection is available.</param>
    public void SetOnline(bool connected)
    {
        lock (gate)
        {
            online = connected;
        }
    }

    /// <summary>
    /// Completes authentication: <c>Connecting</c> becomes
    /// <c>Authenticated</c>. The engine never validates credentials — the
    /// transport does; this only advances the logical state.
    /// </summary>
    public void Authenticate()
    {
        lock (gate)
        {
            RequireState("authenticate", TerraScaleViewSessionState.Connecting);
            state = TerraScaleViewSessionState.Authenticated;
        }
    }

    /// <summary>
    /// Opens the bounded view subscription: <c>Authenticated</c>
    /// becomes <c>Subscribing</c>. The subscription's identity is the
    /// server's business; the engine tracks only what it delivers.
    /// </summary>
    public void Subscribe()
    {
        lock (gate)
        {
            RequireState("subscribe", TerraScaleViewSessionState.Authenticated);
            state = TerraScaleViewSessionState.Subscribing;
        }
    }

    /// <summary>
    /// Begins resume after reconnect: a dropped <c>Live</c> session (or a
    /// restored <c>Connecting</c> one) becomes <c>Resuming</c>. Requires
    /// an adopted checkpoint to present — a session with nothing to
    /// resume subscribes fresh instead.
    /// </summary>
    public void BeginResume()
    {
        lock (gate)
        {
            RequireState("begin resume", TerraScaleViewSessionState.Connecting, TerraScaleViewSessionState.Live);
            if (checkpoint is null)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.WrongState,
                    "Cannot resume: the session has no adopted checkpoint to present. Subscribe fresh instead.");
            }

            state = TerraScaleViewSessionState.Resuming;
        }
    }

    /// <summary>
    /// Accepts a <c>SnapshotBegin</c> and starts a fresh assembly. Valid
    /// from <c>Subscribing</c>, <c>Live</c>, <c>Resuming</c>,
    /// <c>Resetting</c>, and <c>Snapshotting</c> — restarting an
    /// interrupted snapshot discards the partial assembly and replaces
    /// nothing. The published cache changes only when the matching
    /// <c>SnapshotEnd</c> validates.
    /// </summary>
    /// <param name="begin">The begin frame.</param>
    public void BeginSnapshot(TerraScaleViewSnapshotBegin begin)
    {
        ArgumentNullException.ThrowIfNull(begin);
        lock (gate)
        {
            RequireState(
                "begin snapshot",
                TerraScaleViewSessionState.Subscribing,
                TerraScaleViewSessionState.Snapshotting,
                TerraScaleViewSessionState.Live,
                TerraScaleViewSessionState.Resuming,
                TerraScaleViewSessionState.Resetting);

            assembly = new SnapshotAssembly(begin);
            state = TerraScaleViewSessionState.Snapshotting;
        }
    }

    /// <summary>
    /// Accepts one <c>SnapshotChunk</c>. Ordinals are one-based and
    /// strictly contiguous; a broken ordinal, a repeated identity, or an
    /// assembly that would exceed the declared row or byte bound fails
    /// closed and publishes nothing.
    /// </summary>
    /// <param name="chunk">The chunk frame.</param>
    public void ApplySnapshotChunk(TerraScaleViewSnapshotChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        lock (gate)
        {
            RequireState("apply snapshot chunk", TerraScaleViewSessionState.Snapshotting);
            var current = assembly ?? throw new TerraScaleViewProtocolException(
                TerraScaleViewProtocolError.MalformedSnapshot,
                "A snapshot chunk arrived without a begin frame.");

            if (chunk.ViewGeneration != current.Begin.ViewGeneration)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.MalformedSnapshot,
                    $"The chunk's view generation {chunk.ViewGeneration} does not match the assembly's {current.Begin.ViewGeneration}.");
            }

            if (chunk.ChunkOrdinal != current.NextOrdinal)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.MalformedSnapshot,
                    $"The chunk ordinal {chunk.ChunkOrdinal} breaks the assembly: ordinal {current.NextOrdinal} is next.");
            }

            foreach (var row in chunk.Rows)
            {
                if (!current.Identities.Add(row.Identity))
                {
                    throw new TerraScaleViewProtocolException(
                        TerraScaleViewProtocolError.MalformedSnapshot,
                        $"The snapshot repeats the row identity {row.Identity}.");
                }

                var rowBytes = LogicalBytes(row);
                if (current.LogicalBytes + rowBytes > current.Begin.DeclaredByteBound)
                {
                    throw new TerraScaleViewProtocolException(
                        TerraScaleViewProtocolError.MalformedSnapshot,
                        "The snapshot assembly exceeds its declared byte bound.");
                }

                current.Rows.Add(row);
                current.LogicalBytes += rowBytes;
            }

            if (current.Rows.Count > current.Begin.DeclaredRowBound)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.MalformedSnapshot,
                    "The snapshot assembly exceeds its declared row bound.");
            }

            current.NextOrdinal++;
        }
    }

    /// <summary>
    /// Accepts a <c>SnapshotEnd</c>: when the complete assembly validated,
    /// the snapshot publishes atomically — the cache becomes exactly these
    /// rows, the revision becomes the base revision, the digest and
    /// checkpoint are adopted, and the acknowledgement is produced. An
    /// interrupted assembly never reaches this publish.
    /// </summary>
    /// <param name="end">The end frame.</param>
    /// <returns>The delivery: <see cref="TerraScaleViewDeliveryOutcome.SnapshotPublished"/> with the acknowledgement to send.</returns>
    public TerraScaleViewDelivery CompleteSnapshot(TerraScaleViewSnapshotEnd end)
    {
        ArgumentNullException.ThrowIfNull(end);
        lock (gate)
        {
            RequireState("complete snapshot", TerraScaleViewSessionState.Snapshotting);
            var current = assembly ?? throw new TerraScaleViewProtocolException(
                TerraScaleViewProtocolError.MalformedSnapshot,
                "A snapshot end arrived without a begin frame.");

            if (end.ViewGeneration != current.Begin.ViewGeneration)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.MalformedSnapshot,
                    $"The end's view generation {end.ViewGeneration} does not match the assembly's {current.Begin.ViewGeneration}.");
            }

            if (end.BaseRevision != current.Begin.BaseRevision)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.MalformedSnapshot,
                    $"The end's base revision {end.BaseRevision} does not match the begin's {current.Begin.BaseRevision}.");
            }

            rows.Clear();
            foreach (var row in current.Rows)
            {
                rows[row.Identity] = row;
            }

            viewId = current.Begin.ViewId;
            viewGeneration = current.Begin.ViewGeneration;
            appliedRevision = end.BaseRevision;
            resultDigest = end.ResultDigest;
            checkpoint = end.Checkpoint;
            causalCut = current.Begin.CausalCut;
            declaredRowBound = current.Begin.DeclaredRowBound;
            declaredLogicalByteBound = current.Begin.DeclaredByteBound;
            appliedLogicalBytes = current.LogicalBytes;
            generationAdvanceExpected = false;
            assembly = null;
            state = TerraScaleViewSessionState.Live;
            SnapshotsPublished++;

            return new TerraScaleViewDelivery(
                TerraScaleViewDeliveryOutcome.SnapshotPublished,
                state,
                Ack());
        }
    }

    /// <summary>
    /// Applies one <c>ResultDelta</c> under the ADR-0024 rules:
    /// a delta whose <c>to_revision</c> is already applied is an ignored
    /// duplicate; a <c>from_revision</c> that does not match the applied
    /// revision is a gap that applies nothing and requires resume or
    /// reset; otherwise the operations apply atomically — removals run
    /// before the inserts that may reuse a visible key, identity selects
    /// the row, a move may accompany an update, and a <c>Reset</c>
    /// replaces the complete result. A delta that assumes client state
    /// the engine cannot prove (unknown identity, moved-from position
    /// mismatch) diverges the view: nothing applies, the generation
    /// closes, and an authorized snapshot is required.
    /// </summary>
    /// <param name="delta">The delta frame.</param>
    /// <returns>The typed delivery: applied, reset, duplicate, gap, or divergence.</returns>
    public TerraScaleViewDelivery ApplyResultDelta(TerraScaleViewResultDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        lock (gate)
        {
            RequireState("apply result delta", TerraScaleViewSessionState.Live, TerraScaleViewSessionState.Resuming);

            var generationChanged = delta.ViewGeneration != viewGeneration;
            if (generationChanged && !generationAdvanceExpected)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.MalformedDelta,
                    $"The delta's view generation {delta.ViewGeneration} is not the applied generation {viewGeneration} and no generation advance was signaled.");
            }

            if (delta.ViewId.Equals(viewId) is false && !generationChanged)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.MalformedDelta,
                    "The delta's view identity does not match the subscribed view.");
            }

            if (delta.ToRevision < appliedRevision && !generationChanged)
            {
                DuplicateDeltasIgnored++;
                return new TerraScaleViewDelivery(TerraScaleViewDeliveryOutcome.DuplicateDeltaIgnored, state);
            }

            if (delta.ToRevision == appliedRevision && !generationChanged)
            {
                if (!delta.ResultDigest.Equals(resultDigest))
                {
                    return Diverge();
                }

                DuplicateDeltasIgnored++;
                return new TerraScaleViewDelivery(TerraScaleViewDeliveryOutcome.DuplicateDeltaIgnored, state);
            }

            if (delta.ToRevision <= delta.FromRevision)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.MalformedDelta,
                    $"The delta revises {delta.FromRevision} to {delta.ToRevision}: revisions advance inside a generation.");
            }

            if (generationChanged)
            {
                if (delta.FromRevision != 0)
                {
                    RevisionGapsDetected++;
                    return new TerraScaleViewDelivery(TerraScaleViewDeliveryOutcome.RevisionGapDetected, state);
                }

                viewGeneration = delta.ViewGeneration;
                viewId = delta.ViewId;
                checkpoint = null;
                generationAdvanceExpected = false;
            }
            else if (delta.FromRevision != appliedRevision)
            {
                RevisionGapsDetected++;
                return new TerraScaleViewDelivery(TerraScaleViewDeliveryOutcome.RevisionGapDetected, state);
            }

            var plan = PlanDelta(delta);
            if (plan is null)
            {
                return Diverge();
            }

            var reset = plan.Reset;
            if (reset is not null)
            {
                rows.Clear();
                foreach (var row in reset.Rows)
                {
                    rows[row.Identity] = row;
                }

                appliedLogicalBytes = plan.LogicalBytes;
                ResetsApplied++;
            }
            else
            {
                // Removals run before the rows that may reuse a visible
                // key; each surviving identity then lands its folded
                // frame-final state.
                foreach (var (identity, mutation) in plan.Working)
                {
                    if (mutation.Removed)
                    {
                        rows.Remove(identity);
                    }
                }

                foreach (var (identity, mutation) in plan.Working)
                {
                    if (!mutation.Removed && mutation.Row is not null)
                    {
                        rows[identity] = mutation.Row;
                    }
                }

                appliedLogicalBytes = plan.LogicalBytes;
                DeltasApplied++;
            }

            appliedRevision = delta.ToRevision;
            resultDigest = delta.ResultDigest;
            checkpoint = delta.Checkpoint;
            causalCut = delta.CausalCut;
            state = TerraScaleViewSessionState.Live;

            var outcome = reset is not null
                ? TerraScaleViewDeliveryOutcome.ResetApplied
                : TerraScaleViewDeliveryOutcome.DeltaApplied;
            return new TerraScaleViewDelivery(outcome, state, Ack());
        }
    }

    /// <summary>
    /// Records an authorization generation change. A non-revoked change
    /// expects the next delivery to open a new view generation: the
    /// server emits removals or a bounded diff or a fresh snapshot — the
    /// engine accepts whichever arrives first and never resumes the old
    /// generation as if its permissions were current. A revoked session
    /// drops its checkpoint and closes: no new view work applies.
    /// </summary>
    /// <param name="newAuthorizationGeneration">The new authorization generation.</param>
    /// <param name="effect">The change's effect on this view.</param>
    /// <returns>The typed delivery.</returns>
    public TerraScaleViewDelivery NotifyAuthorizationChange(ulong newAuthorizationGeneration, TerraScaleViewAuthorizationEffect effect)
    {
        lock (gate)
        {
            RequireState(
                "notify authorization change",
                TerraScaleViewSessionState.Authenticated,
                TerraScaleViewSessionState.Subscribing,
                TerraScaleViewSessionState.Snapshotting,
                TerraScaleViewSessionState.Live,
                TerraScaleViewSessionState.Resuming,
                TerraScaleViewSessionState.Resetting);

            if (newAuthorizationGeneration <= authorizationGeneration)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.InvalidAuthorization,
                    $"The authorization generation {newAuthorizationGeneration} is not newer than {authorizationGeneration}.");
            }

            authorizationGeneration = newAuthorizationGeneration;
            if (effect == TerraScaleViewAuthorizationEffect.SessionRevoked)
            {
                checkpoint = null;
                generationAdvanceExpected = false;
                assembly = null;
                optimisticLayers.Clear();
                state = TerraScaleViewSessionState.Closing;
                return new TerraScaleViewDelivery(TerraScaleViewDeliveryOutcome.SessionRevoked, state);
            }

            optimisticLayers.Clear();
            generationAdvanceExpected = true;
            return new TerraScaleViewDelivery(TerraScaleViewDeliveryOutcome.ViewGenerationAdvanced, state);
        }
    }

    /// <summary>
    /// Records a server-signaled reset: the current result is being
    /// replaced by a fresh authorized snapshot (a correct bounded
    /// fallback, not an error). The cache stays visible until the
    /// replacement publishes; a reset never guesses a patch.
    /// </summary>
    /// <returns>The typed delivery: <see cref="TerraScaleViewDeliveryOutcome.SessionReset"/> in the <c>Resetting</c> state.</returns>
    public TerraScaleViewDelivery NotifyReset()
    {
        lock (gate)
        {
            RequireState(
                "notify reset",
                TerraScaleViewSessionState.Authenticated,
                TerraScaleViewSessionState.Subscribing,
                TerraScaleViewSessionState.Snapshotting,
                TerraScaleViewSessionState.Live,
                TerraScaleViewSessionState.Resuming,
                TerraScaleViewSessionState.Resetting);

            state = TerraScaleViewSessionState.Resetting;
            return new TerraScaleViewDelivery(TerraScaleViewDeliveryOutcome.SessionReset, state);
        }
    }

    /// <summary>
    /// Records a <c>CheckpointExpired</c> answer: the presented checkpoint
    /// fell below the retained floor. The unusable checkpoint is dropped
    /// and the automatic authorized bootstrap — a fresh snapshot — is the
    /// expected next delivery. Expiry is lifecycle, not corruption.
    /// </summary>
    /// <returns>The typed delivery: <see cref="TerraScaleViewDeliveryOutcome.CheckpointExpired"/> in the <c>Resetting</c> state.</returns>
    public TerraScaleViewDelivery NotifyCheckpointExpired()
    {
        lock (gate)
        {
            RequireState(
                "notify checkpoint expiry",
                TerraScaleViewSessionState.Authenticated,
                TerraScaleViewSessionState.Subscribing,
                TerraScaleViewSessionState.Snapshotting,
                TerraScaleViewSessionState.Live,
                TerraScaleViewSessionState.Resuming,
                TerraScaleViewSessionState.Resetting);

            checkpoint = null;
            optimisticLayers.Clear();
            state = TerraScaleViewSessionState.Resetting;
            return new TerraScaleViewDelivery(TerraScaleViewDeliveryOutcome.CheckpointExpired, state);
        }
    }

    /// <summary>
    /// Records a server-signaled <c>ViewDiverged</c>: the acknowledged
    /// digest did not match the server's result at that revision. The
    /// generation closes, the checkpoint is dropped, and an authorized
    /// snapshot is required before anything else applies.
    /// </summary>
    /// <returns>The typed delivery: <see cref="TerraScaleViewDeliveryOutcome.ViewDiverged"/> in the <c>Resetting</c> state.</returns>
    public TerraScaleViewDelivery NotifyDiverged()
    {
        lock (gate)
        {
            RequireState(
                "notify divergence",
                TerraScaleViewSessionState.Subscribing,
                TerraScaleViewSessionState.Snapshotting,
                TerraScaleViewSessionState.Live,
                TerraScaleViewSessionState.Resuming,
                TerraScaleViewSessionState.Resetting);

            return Diverge();
        }
    }

    /// <summary>
    /// Attaches one reversible optimistic presentation layer under a
    /// stable idempotency identity. The layer colors
    /// <see cref="CurrentView"/> only: it never enters durable state,
    /// never advances a revision, digest, or checkpoint, rebases over
    /// authoritative result operations as they arrive, and disappears on
    /// the mutation's outcome. The identity is stable for retries: a
    /// second attach while in flight is rejected, not duplicated.
    /// </summary>
    /// <param name="idempotencyId">The stable idempotency identity of the in-flight mutation.</param>
    /// <param name="operations">The presentation operations, applied in order.</param>
    /// <returns>The presentation view after the layer applied.</returns>
    public TerraScaleViewResult AttachOptimisticPresentation(string idempotencyId, IReadOnlyList<TerraScaleViewResultOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        lock (gate)
        {
            RequireState("attach optimistic presentation", TerraScaleViewSessionState.Live);

            if (!online)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.Offline,
                    "Optimistic mutations are online-only and cannot be queued while disconnected.");
            }

            if (string.IsNullOrWhiteSpace(idempotencyId) || Encoding.UTF8.GetByteCount(idempotencyId) > MaxIdempotencyKeyBytes)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.InvalidOptimisticMutation,
                    "The optimistic mutation identity must be non-empty and at most 128 UTF-8 bytes.");
            }

            if (operations.Count == 0)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.InvalidOptimisticMutation,
                    "An optimistic presentation requires at least one operation.");
            }

            if (optimisticLayers.Any(layer => layer.IdempotencyId == idempotencyId))
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.InvalidOptimisticMutation,
                    $"The optimistic mutation {idempotencyId} is already in flight: one stable identity per mutation.");
            }

            if (completedMutations.Contains(idempotencyId))
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.InvalidOptimisticMutation,
                    $"The optimistic mutation {idempotencyId} has already completed.");
            }

            // Validate the presentation before retaining it. A malformed
            // optimistic layer must never silently disappear during replay.
            _ = ApplyPresentation([.. rows.Values], [.. operations], lenient: false);

            optimisticLayers.Add(new OptimisticLayer(idempotencyId, [.. operations]));
            return Materialize(optimistic: true);
        }
    }

    /// <summary>
    /// Resolves one in-flight optimistic mutation: accepted or rejected,
    /// the presentation layer is removed — an acceptance is covered by the
    /// authoritative result operations, a rejection reverts the
    /// presentation. An unknown identity is rejected fail-closed.
    /// </summary>
    /// <param name="idempotencyId">The stable idempotency identity of the mutation.</param>
    /// <param name="accepted">Whether the server accepted the mutation.</param>
    /// <returns>The presentation view after the layer was removed.</returns>
    public TerraScaleViewResult ResolveMutationOutcome(string idempotencyId, bool accepted)
    {
        lock (gate)
        {
            RequireState("resolve optimistic mutation", TerraScaleViewSessionState.Live);

            if (completedMutations.Contains(idempotencyId))
            {
                return Materialize(optimistic: true);
            }

            var index = optimisticLayers.FindIndex(layer => layer.IdempotencyId == idempotencyId);
            if (index < 0)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.InvalidOptimisticMutation,
                    $"The optimistic mutation {idempotencyId} is not in flight.");
            }

            optimisticLayers.RemoveAt(index);
            if (completedMutations.Count >= 1024)
            {
                completedMutations.Remove(completedMutations.First());
            }

            completedMutations.Add(idempotencyId);
            return Materialize(optimistic: true);
        }
    }

    /// <summary>
    /// Starts the close handshake: the session refuses new view work in
    /// <c>Closing</c> until the transport confirms the close. Closing a
    /// closed session is the only wrong state.
    /// </summary>
    public void Close()
    {
        lock (gate)
        {
            if (state == TerraScaleViewSessionState.Closed)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.WrongState,
                    "Cannot close in the closed state: the session is already closed.");
            }

            state = TerraScaleViewSessionState.Closing;
        }
    }

    /// <summary>
    /// Confirms the server observed the close: <c>Closing</c> becomes
    /// <c>Closed</c>. Nothing further applies to a closed session.
    /// </summary>
    public void ConfirmClosed()
    {
        lock (gate)
        {
            RequireState("confirm closed", TerraScaleViewSessionState.Closing);
            state = TerraScaleViewSessionState.Closed;
            optimisticLayers.Clear();
            assembly = null;
        }
    }

    private TerraScaleViewAck Ack() => new(viewId!, viewGeneration, appliedRevision, resultDigest!);

    private TerraScaleViewDelivery Diverge()
    {
        DivergencesDetected++;
        checkpoint = null;
        optimisticLayers.Clear();
        generationAdvanceExpected = false;
        assembly = null;
        state = TerraScaleViewSessionState.Resetting;
        return new TerraScaleViewDelivery(TerraScaleViewDeliveryOutcome.ViewDiverged, state);
    }

    /// <summary>
    /// Plans one delta against the current cache: operations fold per
    /// identity in frame order (a move may accompany an update or patch),
    /// every divergence assumption is exhausted before anything applies,
    /// and the projected row and byte bounds are checked fail-closed.
    /// Returns <c>null</c> when the delta assumes client state the engine
    /// cannot prove: the view diverges and nothing applies.
    /// </summary>
    private DeltaPlan? PlanDelta(TerraScaleViewResultDelta delta)
    {
        var working = new Dictionary<TerraScaleViewIdentity, WorkingMutation>();
        TerraScaleViewReset? reset = null;

        TerraScaleViewRow? Resolve(TerraScaleViewIdentity identity)
        {
            if (working.TryGetValue(identity, out var pending))
            {
                return pending.Removed ? null : pending.Row;
            }

            return rows.TryGetValue(identity, out var existing) ? existing : null;
        }

        void Fold(TerraScaleViewIdentity identity, TerraScaleViewRow row) =>
            working[identity] = new WorkingMutation(row, Removed: false);

        foreach (var operation in delta.Operations)
        {
            switch (operation)
            {
                case TerraScaleViewReset resetOperation:
                    if (delta.Operations.Count > 1)
                    {
                        throw new TerraScaleViewProtocolException(
                            TerraScaleViewProtocolError.MalformedDelta,
                            "A Reset replaces the complete result and cannot accompany other operations.");
                    }

                    reset = resetOperation;
                    break;

                case TerraScaleViewInsert insert:
                    if (Resolve(insert.Identity) is not null)
                    {
                        throw new TerraScaleViewProtocolException(
                            TerraScaleViewProtocolError.MalformedDelta,
                            $"The insert targets the existing identity {insert.Identity}: identity selects an existing row, updates replace it.");
                    }

                    Fold(insert.Identity, new TerraScaleViewRow(insert.Identity, insert.Position, insert.Value));
                    break;

                case TerraScaleViewUpdate update:
                    if (Resolve(update.Identity) is not { } updatedRow)
                    {
                        return null;
                    }

                    Fold(update.Identity, new TerraScaleViewRow(update.Identity, updatedRow.Position, update.Value));
                    break;

                case TerraScaleViewPatch patch:
                    if (Resolve(patch.Identity) is not { } patchedRow)
                    {
                        return null;
                    }

                    var patched = ApplyPatch(patchedRow.Value, patch.Steps);
                    if (patched is null)
                    {
                        return null;
                    }

                    Fold(patch.Identity, new TerraScaleViewRow(patch.Identity, patchedRow.Position, patched.Value));
                    break;

                case TerraScaleViewRemove remove:
                    if (Resolve(remove.Identity) is null)
                    {
                        return null;
                    }

                    working[remove.Identity] = new WorkingMutation(Row: null, Removed: true);
                    break;

                case TerraScaleViewMove move:
                    if (Resolve(move.Identity) is not { } movedRow)
                    {
                        return null;
                    }

                    if (movedRow.Position.CompareTo(move.OldPosition) != 0)
                    {
                        return null;
                    }

                    Fold(move.Identity, new TerraScaleViewRow(move.Identity, move.NewPosition, movedRow.Value));
                    break;

                default:
                    throw new TerraScaleViewProtocolException(
                        TerraScaleViewProtocolError.MalformedDelta,
                        $"The result operation {operation.Kind} is not part of the closed v1 operation set.");
            }
        }

        if (reset is not null)
        {
            if (reset.Rows.Count > declaredRowBound)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.MalformedDelta,
                    "The reset result exceeds the declared row bound.");
            }

            if (reset.Rows.Select(row => row.Identity).Distinct().Count() != reset.Rows.Count)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.MalformedDelta,
                    "The reset result repeats a row identity.");
            }

            var resetBytes = reset.Rows.Sum(LogicalBytes);
            if (resetBytes > declaredLogicalByteBound)
            {
                throw new TerraScaleViewProtocolException(
                    TerraScaleViewProtocolError.MalformedDelta,
                    "The reset result exceeds the declared byte bound.");
            }

            return new DeltaPlan(working, reset, resetBytes);
        }

        var projectedCount = rows.Count;
        var projectedBytes = appliedLogicalBytes;
        foreach (var (identity, mutation) in working)
        {
            if (mutation.Removed)
            {
                if (rows.TryGetValue(identity, out var removed))
                {
                    projectedCount--;
                    projectedBytes -= LogicalBytes(removed);
                }
            }
            else if (rows.TryGetValue(identity, out var existing))
            {
                projectedBytes += LogicalBytes(mutation.Row!) - LogicalBytes(existing);
            }
            else
            {
                projectedCount++;
                projectedBytes += LogicalBytes(mutation.Row!);
            }
        }

        if (projectedCount > declaredRowBound)
        {
            throw new TerraScaleViewProtocolException(
                TerraScaleViewProtocolError.MalformedDelta,
                "The delta would exceed the declared row bound.");
        }

        if (projectedBytes > declaredLogicalByteBound)
        {
            throw new TerraScaleViewProtocolException(
                TerraScaleViewProtocolError.MalformedDelta,
                "The delta would exceed the declared byte bound.");
        }

        return new DeltaPlan(working, Reset: null, projectedBytes);
    }

    private TerraScaleViewResult Materialize(bool optimistic)
    {
        var materialized = new List<TerraScaleViewRow>(rows.Values);
        materialized.Sort(CompareRows);

        if (optimistic)
        {
            foreach (var layer in optimisticLayers)
            {
                materialized = ApplyPresentation(materialized, layer.Operations);
            }
        }

        return new TerraScaleViewResult(
            viewId!,
            viewGeneration,
            appliedRevision,
            materialized,
            resultDigest!,
            optimistic && optimisticLayers.Count > 0);
    }

    private static List<TerraScaleViewRow> ApplyPresentation(
        List<TerraScaleViewRow> materialized,
        List<TerraScaleViewResultOperation> operations,
        bool lenient = true)
    {
        var rowsByKey = materialized.ToDictionary(row => row.Identity);
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case TerraScaleViewReset reset:
                    rowsByKey.Clear();
                    foreach (var row in reset.Rows)
                    {
                        if (!rowsByKey.TryAdd(row.Identity, row) && !lenient)
                        {
                            throw PresentationFailure("The optimistic reset repeats a row identity.");
                        }

                        rowsByKey[row.Identity] = row;
                    }

                    break;

                case TerraScaleViewInsert insert:
                    if (rowsByKey.ContainsKey(insert.Identity) && !lenient)
                    {
                        throw PresentationFailure($"The optimistic insert targets existing identity {insert.Identity}.");
                    }

                    rowsByKey[insert.Identity] = new TerraScaleViewRow(insert.Identity, insert.Position, insert.Value);
                    break;

                case TerraScaleViewUpdate update:
                    if (rowsByKey.TryGetValue(update.Identity, out var updated))
                    {
                        rowsByKey[update.Identity] = new TerraScaleViewRow(update.Identity, updated.Position, update.Value);
                    }
                    else if (!lenient)
                    {
                        throw PresentationFailure($"The optimistic update targets unknown identity {update.Identity}.");
                    }

                    break;

                case TerraScaleViewPatch patch:
                    if (rowsByKey.TryGetValue(patch.Identity, out var patched))
                    {
                        var patchedValue = ApplyPatch(patched.Value, patch.Steps, lenient);
                        if (patchedValue is not null)
                        {
                            rowsByKey[patch.Identity] = new TerraScaleViewRow(patch.Identity, patched.Position, patchedValue.Value);
                        }
                        else if (!lenient)
                        {
                            throw PresentationFailure($"The optimistic patch does not resolve on identity {patch.Identity}.");
                        }
                    }
                    else if (!lenient)
                    {
                        throw PresentationFailure($"The optimistic patch targets unknown identity {patch.Identity}.");
                    }

                    break;

                case TerraScaleViewRemove remove:
                    if (!rowsByKey.Remove(remove.Identity) && !lenient)
                    {
                        throw PresentationFailure($"The optimistic remove targets unknown identity {remove.Identity}.");
                    }

                    break;

                case TerraScaleViewMove move:
                    if (rowsByKey.TryGetValue(move.Identity, out var moved))
                    {
                        if (!lenient && moved.Position.CompareTo(move.OldPosition) != 0)
                        {
                            throw PresentationFailure($"The optimistic move has an unprovable old position for identity {move.Identity}.");
                        }

                        rowsByKey[move.Identity] = new TerraScaleViewRow(move.Identity, move.NewPosition, moved.Value);
                    }
                    else if (!lenient)
                    {
                        throw PresentationFailure($"The optimistic move targets unknown identity {move.Identity}.");
                    }

                    break;
            }

            if (!lenient && (rowsByKey.Count > 10_000 || rowsByKey.Values.Sum(LogicalBytes) > 64 * 1024 * 1024))
            {
                throw PresentationFailure("The optimistic presentation exceeds the bounded result budget.");
            }
        }

        var result = new List<TerraScaleViewRow>(rowsByKey.Values);
        result.Sort(CompareRows);
        return result;

        static TerraScaleViewProtocolException PresentationFailure(string reason) =>
            new(TerraScaleViewProtocolError.InvalidOptimisticMutation, reason);
    }

    /// <summary>Applies canonical patch steps to a cloned value. Returns <c>null</c> when a path does not resolve.</summary>
    private static JsonElement? ApplyPatch(JsonElement value, IReadOnlyList<TerraScaleViewPatchStep> steps, bool lenient = false)
    {
        var node = JsonNode.Parse(value.GetRawText());
        foreach (var step in steps)
        {
            var segments = step.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return null;
            }

            var parent = node;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                parent = StepInto(parent, segments[index], lenient);
                if (parent is null)
                {
                    return null;
                }
            }

            var last = segments[^1];
            var applied = step.Kind == TerraScaleViewPatchStepKind.Replace
                ? ReplaceAt(parent, last, step.Value)
                : RemoveAt(parent, last);
            if (!applied)
            {
                if (lenient && step.Kind == TerraScaleViewPatchStepKind.Replace)
                {
                    continue;
                }

                return null;
            }
        }

        return node is null ? null : JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    private static JsonNode? StepInto(JsonNode? node, string segment, bool lenient)
    {
        switch (node)
        {
            case JsonObject objectNode:
                if (objectNode.TryGetPropertyValue(segment, out var child))
                {
                    return child;
                }

                return lenient ? objectNode[segment] = new JsonObject() : null;
            case JsonArray arrayNode when int.TryParse(segment, out var index):
                return index >= 0 && index < arrayNode.Count ? arrayNode[index] : null;
            default:
                return null;
        }
    }

    private static bool ReplaceAt(JsonNode? parent, string segment, JsonElement? value)
    {
        var replacement = value is { } element ? JsonNode.Parse(element.GetRawText()) : null;
        switch (parent)
        {
            case JsonObject objectNode:
                objectNode[segment] = replacement;
                return true;
            case JsonArray arrayNode when int.TryParse(segment, out var index):
                if (index < 0 || index >= arrayNode.Count)
                {
                    return false;
                }

                arrayNode[index] = replacement;
                return true;
            default:
                return false;
        }
    }

    private static bool RemoveAt(JsonNode? parent, string segment)
    {
        switch (parent)
        {
            case JsonObject objectNode:
                return objectNode.Remove(segment);
            case JsonArray arrayNode when int.TryParse(segment, out var index):
                if (index < 0 || index >= arrayNode.Count)
                {
                    return false;
                }

                arrayNode.RemoveAt(index);
                return true;
            default:
                return false;
        }
    }

    private static int CompareRows(TerraScaleViewRow left, TerraScaleViewRow right)
    {
        var byPosition = left.Position.CompareTo(right.Position);
        if (byPosition != 0)
        {
            return byPosition;
        }

        var byDocument = string.CompareOrdinal(left.Identity.DocumentId, right.Identity.DocumentId);
        return byDocument != 0 ? byDocument : left.Identity.Incarnation.CompareTo(right.Identity.Incarnation);
    }

    private static long LogicalBytes(TerraScaleViewRow row) => Encoding.UTF8.GetByteCount(row.Value.GetRawText());

    private void RequireState(string operation, params TerraScaleViewSessionState[] allowed)
    {
        if (Array.IndexOf(allowed, state) < 0)
        {
            var expected = string.Join(", ", allowed.Select(TerraScaleViewSessionStateNames.WireName));
            throw new TerraScaleViewProtocolException(
                TerraScaleViewProtocolError.WrongState,
                $"Cannot {operation} in the {TerraScaleViewSessionStateNames.WireName(state)} state: expected {expected}.");
        }
    }
}
