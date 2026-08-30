// <copyright file="BackboneHub.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Threading.Channels;
using TerraBase.Protocol;

/// <summary>
/// The backbone stream hub of the API host (SE-08): the serving half
/// behind the SSE and WebSocket subscription endpoints. One hub stream
/// owns one TBEP backbone subscription on the caller's tenant channel;
/// every notification the engine streams — ordered batches, the
/// content-free triggers of PD-2026-08-26-13 D3, and the close frame —
/// lands in one bounded queue. A consumer that falls a full depth
/// behind sheds the OLDEST queued frames: the incoming frame always
/// lands in the freed slot, and every frame actually lost is counted
/// and surfaced in the stream itself and in the host metrics. The
/// in-band drop marker carries the boundary of the dropped region —
/// the oldest sequence actually dropped, the gap start — never the
/// newest, so the surfaced resume point cannot erase the gap.
/// </summary>
public sealed class BackboneHub
{
    /// <summary>The bounded queue depth of one stream.</summary>
    public const int QueueDepth = 64;

    private readonly EngineSupervisor supervisor;
    private readonly AuditLog audit;
    private readonly object gate = new();
    private readonly Dictionary<ulong, BackboneStream> open = [];
    private ulong nextSubscriptionId;
    private long droppedTotal;

    /// <summary>Initializes the hub over the supervised engine and the audit log.</summary>
    /// <param name="supervisor">The engine supervisor.</param>
    /// <param name="audit">The lifecycle audit log.</param>
    public BackboneHub(EngineSupervisor supervisor, AuditLog audit)
    {
        this.supervisor = supervisor;
        this.audit = audit;
        supervisor.EngineLost += this.OnEngineLost;
    }

    /// <summary>The total dropped-frame count across every stream (surfaced in the metrics).</summary>
    public long DroppedTotal => Volatile.Read(ref this.droppedTotal);

    /// <summary>The currently open stream count.</summary>
    public int OpenCount
    {
        get
        {
            lock (this.gate)
            {
                return this.open.Count;
            }
        }
    }

    /// <summary>Opens one stream: subscribes the engine backbone for the caller's tenant.</summary>
    /// <param name="tenant">The backend-supplied tenant.</param>
    /// <param name="principal">The backend-supplied principal.</param>
    /// <param name="collections">The collection filter, or <c>null</c> for every collection.</param>
    /// <param name="fromSequence">The replay start (checkpoint/resume), or <c>null</c> for live-only.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The open stream.</returns>
    /// <exception cref="TbepException">The engine refused the subscription.</exception>
    /// <exception cref="EngineUnavailableException">The engine generation is down.</exception>
    public async Task<BackboneStream> OpenAsync(
        string tenant,
        string principal,
        string[]? collections,
        ulong? fromSequence,
        CancellationToken cancellationToken = default)
    {
        var channel = await this.supervisor.ChannelForAsync(tenant, principal, cancellationToken).ConfigureAwait(false);
        ulong subscriptionId;
        BackboneStream stream;
        lock (this.gate)
        {
            subscriptionId = ++this.nextSubscriptionId;
            stream = new BackboneStream(this, subscriptionId, tenant, collections);
            this.open[subscriptionId] = stream;
        }

        try
        {
            var answer = await channel.BackboneSubscribeAsync(
                subscriptionId,
                collections,
                fromSequence,
                sink: stream,
                cancellationToken).ConfigureAwait(false);
            stream.StartSequence = answer.StartSequence;
            this.audit.Record(
                AuditKind.SubscriptionOpened,
                tenant,
                principal,
                operation: "backbone.subscribe",
                outcome: AuditOutcome.Accepted,
                detail: $"subscription {subscriptionId} from {answer.StartSequence}");
            return stream;
        }
        catch
        {
            await this.CloseAsync(stream, reason: "subscribe-failed").ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Closes one stream: unsubscribes the engine and ends the queue.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="reason">The machine-readable close reason.</param>
    /// <returns>A task that completes when the engine side is unsubscribed.</returns>
    public async Task CloseAsync(BackboneStream stream, string reason)
    {
        lock (this.gate)
        {
            this.open.Remove(stream.SubscriptionId);
        }

        stream.Complete();
        try
        {
            var channel = await this.supervisor
                .ChannelForAsync(stream.Tenant, principal: stream.Principal ?? "stream-owner")
                .ConfigureAwait(false);
            await channel.BackboneUnsubscribeAsync(stream.SubscriptionId).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is TbepException or EngineUnavailableException or IOException)
        {
            // The channel or the generation is gone; the engine side ends
            // with it. The stream is closed locally either way — never
            // orphaned on the host.
        }

        this.audit.Record(
            AuditKind.SubscriptionClosed,
            stream.Tenant,
            stream.Principal,
            operation: "backbone.unsubscribe",
            outcome: AuditOutcome.Noted,
            detail: $"subscription {stream.SubscriptionId}: {reason}, dropped {stream.Dropped}");
    }

    /// <summary>Ends every open stream of a lost engine generation; nothing orphans.</summary>
    /// <param name="generation">The lost generation.</param>
    public void OnEngineLost(int generation)
    {
        List<BackboneStream> streams;
        lock (this.gate)
        {
            streams = [.. this.open.Values];
            this.open.Clear();
        }

        foreach (var stream in streams)
        {
            stream.Complete(generationLost: true);
            this.audit.Record(
                AuditKind.SubscriptionClosed,
                stream.Tenant,
                stream.Principal,
                operation: "backbone.unsubscribe",
                outcome: AuditOutcome.Noted,
                detail: $"subscription {stream.SubscriptionId} ended with engine generation {generation}");
        }
    }

    internal void OnDrop(BackboneStream stream)
    {
        Interlocked.Increment(ref this.droppedTotal);
        this.audit.Record(
            AuditKind.StreamDropped,
            stream.Tenant,
            stream.Principal,
            operation: "stream.queue",
            outcome: AuditOutcome.Noted,
            detail: $"subscription {stream.SubscriptionId} dropped a frame at the queue bound {QueueDepth}");
    }
}

/// <summary>
/// One open backbone stream of the hub: the registered TBEP sink and
/// the bounded queue the SSE/WebSocket endpoint drains. Only a consumer
/// that falls a full depth behind sheds frames — oldest first, every
/// shed frame counted, the drop surfaced in-band and in the metrics —
/// and the frame that triggered the shed always lands.
/// </summary>
public sealed class BackboneStream : TbepChannel.ITbepNotificationSink
{
    private readonly BackboneHub hub;
    private readonly Channel<StreamEvent> queue = Channel.CreateBounded<StreamEvent>(
        new BoundedChannelOptions(BackboneHub.QueueDepth) { SingleReader = true, SingleWriter = false });
    private long dropped;
    private ulong lastSequence;
    private ulong gapStart;
    private bool markerQueued;
    private bool completing;

    internal BackboneStream(BackboneHub hub, ulong subscriptionId, string tenant, string[]? collections)
    {
        this.hub = hub;
        this.SubscriptionId = subscriptionId;
        this.Tenant = tenant;
        this.Collections = collections;
    }

    /// <summary>The engine subscription identity.</summary>
    public ulong SubscriptionId { get; }

    /// <summary>The tenant whose channel subscribed (engine-enforced isolation).</summary>
    public string Tenant { get; }

    /// <summary>The principal that opened the stream, when recorded.</summary>
    public string? Principal { get; internal set; }

    /// <summary>The collection filter, or <c>null</c> for every collection.</summary>
    public string[]? Collections { get; }

    /// <summary>The sequence the engine started serving from (the resume point of this stream).</summary>
    public ulong StartSequence { get; internal set; }

    /// <summary>The last sequence this stream handed the consumer.</summary>
    public ulong LastSequence => Volatile.Read(ref this.lastSequence);

    /// <summary>The dropped-frame count of this stream (surfaced, never hidden).</summary>
    public long Dropped => Volatile.Read(ref this.dropped);

    /// <summary>Waits for the next frame of the stream.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The frame, or <c>null</c> when the stream ended.</returns>
    public async Task<StreamEvent?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (await this.queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var frame = await this.queue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame is StreamEvent.Dropped)
            {
                // The marker reached its consumer: the region it describes
                // is surfaced and the next drop may queue a fresh marker.
                // gapStart is deliberately NOT reset here: frames shed
                // while this marker sat queued-but-unread folded their
                // boundaries into it, and wiping them would narrow the
                // next marker's boundary past sequences never surfaced
                // in-band. Carrying the older boundary forward is the safe
                // direction — a consumer rewinds further, never less. (The
                // count, not the marker, remains the accounting of record.)
                Volatile.Write(ref this.markerQueued, false);
            }

            return frame;
        }

        return null;
    }

    /// <inheritdoc/>
    void TbepChannel.ITbepNotificationSink.OnBatch(SubscriptionBatch batch)
    {
        Volatile.Write(ref this.lastSequence, batch.NextSequence > 0 ? batch.NextSequence - 1 : 0);
        this.Offer(new StreamEvent.Batch(batch));
    }

    /// <inheritdoc/>
    void TbepChannel.ITbepNotificationSink.OnTrigger(TriggerNotification trigger)
    {
        Volatile.Write(ref this.lastSequence, Math.Max(this.LastSequence, trigger.Sequence));
        this.Offer(new StreamEvent.Trigger(trigger));
    }

    /// <inheritdoc/>
    void TbepChannel.ITbepNotificationSink.OnClosed(SubscriptionClosed closed) =>
        this.Offer(new StreamEvent.Closed(closed));

    internal void Complete(bool generationLost = false)
    {
        if (generationLost)
        {
            this.Offer(new StreamEvent.GenerationLost());
        }

        Volatile.Write(ref this.completing, true);
        this.queue.Writer.TryComplete();
    }

    private void Offer(StreamEvent frame)
    {
        if (this.queue.Writer.TryWrite(frame))
        {
            return;
        }

        if (Volatile.Read(ref this.completing))
        {
            // The stream ended under this offer: the frame cannot be
            // delivered — it is counted, never lost silently.
            this.ShedFrame();
            return;
        }

        // A full bounded queue sheds the OLDEST queued frames to take
        // this one — the incoming frame always lands in the freed slot,
        // never the frame the queue just accepted responsibility for.
        // Every shed frame is counted, audited, and summed into the
        // host metrics: offered equals delivered plus dropped, exactly.
        while (!this.queue.Writer.TryWrite(frame))
        {
            if (!this.queue.Reader.TryRead(out var oldest))
            {
                // The queue completed under this offer: the frame cannot
                // be delivered — count it.
                this.ShedFrame();
                return;
            }

            this.Shed(oldest);
        }

        if (Volatile.Read(ref this.markerQueued))
        {
            // An unsurfaced marker is already queued and carries the
            // oldest not-yet-surfaced boundary: one marker per dropped
            // region, coalesced.
            return;
        }

        // The drop is surfaced in-band: the marker carries the boundary
        // of the dropped region. Its slot is shed the same way — the
        // marker never evicts a frame uncounted.
        while (!this.queue.Writer.TryWrite(new StreamEvent.Dropped(Volatile.Read(ref this.gapStart))))
        {
            if (!this.queue.Reader.TryRead(out var oldest))
            {
                return;
            }

            this.Shed(oldest);
        }

        Volatile.Write(ref this.gapStart, 0ul);
        Volatile.Write(ref this.markerQueued, true);
    }

    /// <summary>Sheds one queued item: a frame is counted and folds its gap boundary; a marker only folds.</summary>
    /// <param name="item">The shed queue item.</param>
    private void Shed(StreamEvent item)
    {
        if (item is StreamEvent.Dropped dropped)
        {
            // A shed marker is not engine data and never counts as a
            // dropped frame; its unsurfaced gap boundary folds forward
            // into the next marker.
            this.FoldGap(dropped.ResumeFrom);
            Volatile.Write(ref this.markerQueued, false);
            return;
        }

        this.ShedFrame();
        this.FoldGap(FirstSequenceOf(item));
    }

    private void ShedFrame()
    {
        Interlocked.Increment(ref this.dropped);
        this.hub.OnDrop(this);
    }

    /// <summary>Folds a boundary into the tracked gap, keeping the oldest (the start of the dropped region).</summary>
    /// <param name="sequence">The boundary to fold.</param>
    private void FoldGap(ulong sequence)
    {
        if (sequence == 0)
        {
            return;
        }

        while (true)
        {
            var current = Volatile.Read(ref this.gapStart);
            if (current != 0 && current <= sequence)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref this.gapStart, sequence, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>The first sequence of a frame: the boundary a shed of it opens (0 when the frame carries none).</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>The frame's oldest sequence, or 0.</returns>
    private static ulong FirstSequenceOf(StreamEvent frame) => frame switch
    {
        StreamEvent.Batch batch => batch.Body.Events is { Length: > 0 } events ? events[0].Sequence : batch.Body.FromSequence,
        StreamEvent.Trigger trigger => trigger.Body.Sequence,
        _ => 0,
    };
}

/// <summary>One frame of a backbone stream, in arrival order.</summary>
public abstract record StreamEvent
{
    private StreamEvent()
    {
    }

    /// <summary>An ordered, bounded batch of change events.</summary>
    /// <param name="Body">The batch.</param>
    public sealed record Batch(SubscriptionBatch Body) : StreamEvent;

    /// <summary>The content-free trigger (PD-2026-08-26-13 D3): push the hint, pull the delta.</summary>
    /// <param name="Body">The trigger.</param>
    public sealed record Trigger(TriggerNotification Body) : StreamEvent;

    /// <summary>The engine closed the subscription.</summary>
    /// <param name="Body">The close frame.</param>
    public sealed record Closed(SubscriptionClosed Body) : StreamEvent;

    /// <summary>The bounded queue dropped a frame; the consumer resumes from the carried sequence.</summary>
    /// <param name="ResumeFrom">
    /// The boundary of the dropped region: the oldest sequence actually
    /// dropped — the gap start, never the newest sequence (which would
    /// erase the gap). The consumer replays from its own checkpoint when
    /// that checkpoint is older than this boundary; a checkpoint at or
    /// past it cannot be trusted for the gap, so the consumer rewinds:
    /// on reconnect it asks the engine for the inclusive replay start
    /// <c>from</c> = <paramref name="ResumeFrom"/> (or sends
    /// <c>Last-Event-ID</c> = <paramref name="ResumeFrom"/> - 1), and
    /// the missed range is re-delivered, at-least-once.
    /// </param>
    public sealed record Dropped(ulong ResumeFrom) : StreamEvent;

    /// <summary>The engine generation was lost; the consumer reconnects and resumes from its last sequence.</summary>
    public sealed record GenerationLost() : StreamEvent;
}
