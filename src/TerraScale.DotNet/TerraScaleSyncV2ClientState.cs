namespace TerraScale.DotNet;

/// <summary>
/// The client-side receive state of one sync session (SE-10): the
/// durable resumption checkpoint plus the delivery accounting of the
/// single-writer receive path. The client never merges local writes —
/// it imports server deltas in checkpoint order (delivery is
/// at-least-once and re-delivery of an absorbed batch is a detected
/// duplicate), adopts full snapshots on resync, and counts the
/// content-free triggers that drove its pulls.
/// </summary>
public sealed class TerraScaleSyncV2ClientState
{
    private readonly object gate = new();

    /// <summary>Initializes an empty session state (a fresh session: nothing absorbed).</summary>
    public TerraScaleSyncV2ClientState()
    {
    }

    /// <summary>Initializes a resuming session state from its durable checkpoint.</summary>
    /// <param name="resume">The checkpoint the session presents at setup.</param>
    internal TerraScaleSyncV2ClientState(TerraScaleSyncV2Checkpoint resume)
    {
        checkpoint = resume;
    }

    /// <summary>The session's durable resumption checkpoint: the position retransmission is bounded by.</summary>
    public TerraScaleSyncV2Checkpoint Checkpoint
    {
        get
        {
            lock (gate)
            {
                return checkpoint;
            }
        }

        private set
        {
            lock (gate)
            {
                checkpoint = value;
            }
        }
    }

    private TerraScaleSyncV2Checkpoint checkpoint = TerraScaleSyncV2Checkpoint.Initial;

    /// <summary>The checkpoint the server stated at setup, or <c>null</c> before setup.</summary>
    public TerraScaleSyncV2Checkpoint? ServerCheckpoint { get; private set; }

    /// <summary>The retained floor the server stated at setup, or <c>null</c> before setup.</summary>
    public TerraScaleSyncV2Checkpoint? RetainedFloor { get; private set; }

    /// <summary>The last adopted snapshot: the codec identity, the payload, and the checkpoint it corresponds to.</summary>
    public (TerraScaleSyncV2PayloadCodec Codec, ReadOnlyMemory<byte> Payload, TerraScaleSyncV2Checkpoint Checkpoint)? Snapshot { get; private set; }

    /// <summary>How many delta batches the session applied (duplicates excluded).</summary>
    public ulong DeltasApplied { get; private set; }

    /// <summary>How many re-delivered batches were detected as already absorbed (at-least-once duplicates).</summary>
    public ulong DuplicateDeltas { get; private set; }

    /// <summary>How many payload bytes the session absorbed through the delta path.</summary>
    public ulong DeliveryBytes { get; private set; }

    /// <summary>How many content-free trigger notifications the session observed.</summary>
    public ulong TriggersObserved { get; private set; }

    /// <summary>How many full-state snapshots the session adopted (initial sync and forced resyncs).</summary>
    public ulong ResyncsAdopted { get; private set; }

    /// <summary>How many mutations the host durably applied (applied outcomes only).</summary>
    public ulong MutationsAcknowledged { get; private set; }

    /// <summary>How many mutations the host refused (typed rejections, never acknowledged).</summary>
    public ulong MutationsRejected { get; private set; }

    /// <summary>Records the server's setup answer: its checkpoint and the retained floor.</summary>
    /// <param name="server">The server's checkpoint.</param>
    /// <param name="floor">The retained floor.</param>
    public void ObserveSetup(TerraScaleSyncV2Checkpoint server, TerraScaleSyncV2Checkpoint floor)
    {
        lock (gate)
        {
            ServerCheckpoint = server;
            RetainedFloor = floor;
        }
    }

    /// <summary>
    /// Applies one delta batch in checkpoint order. A batch whose next
    /// checkpoint does not advance the session's position is a detected
    /// duplicate (at-least-once re-delivery): it applies nothing and
    /// counts as a duplicate.
    /// </summary>
    /// <param name="batch">The batch the server served.</param>
    /// <returns>Whether the batch advanced the session's position.</returns>
    public bool ApplyDelta(TerraScaleSyncV2DeltaBatchBody batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (gate)
        {
            if (batch.Next.CompareTo(checkpoint) <= 0)
            {
                DuplicateDeltas++;
                return false;
            }

            checkpoint = batch.Next;
            DeltasApplied++;
            DeliveryBytes += (ulong)batch.Payload.Length;
            return true;
        }
    }

    /// <summary>Adopts one full-state answer: the snapshot replaces the session's position.</summary>
    /// <param name="codec">The payload codec (always <c>loro-snapshot/1</c>).</param>
    /// <param name="payload">The encoded snapshot bytes.</param>
    /// <param name="adopted">The checkpoint the snapshot corresponds to.</param>
    public void AdoptSnapshot(TerraScaleSyncV2PayloadCodec codec, ReadOnlyMemory<byte> payload, TerraScaleSyncV2Checkpoint adopted)
    {
        lock (gate)
        {
            Snapshot = (codec, payload, adopted);
            checkpoint = adopted;
            ResyncsAdopted++;
        }
    }

    /// <summary>Counts one observed trigger notification (content-free; the pull is authoritative).</summary>
    /// <param name="collection">The collection the commit landed in.</param>
    /// <param name="sequence">The commit's storage sequence.</param>
    /// <param name="operationId">The commit's stable operation identity.</param>
    public void ObserveTrigger(string collection, ulong sequence, string operationId)
    {
        lock (gate)
        {
            TriggersObserved++;
            LastTrigger = (collection, sequence, operationId);
        }
    }

    /// <summary>The last observed trigger, or <c>null</c> before the first one.</summary>
    public (string Collection, ulong Sequence, string OperationId)? LastTrigger { get; private set; }

    /// <summary>
    /// Counts one mutation answer (server-ack-only; nothing applies
    /// locally): applied outcomes acknowledge, refusals reject — a
    /// rejection is never an acknowledged mutation.
    /// </summary>
    /// <param name="outcome">The host's answer to the mutation.</param>
    public void CountMutation(TerraScaleSyncV2MutationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        lock (gate)
        {
            if (outcome is TerraScaleSyncV2MutationApplied)
            {
                MutationsAcknowledged++;
            }
            else
            {
                MutationsRejected++;
            }
        }
    }
}
