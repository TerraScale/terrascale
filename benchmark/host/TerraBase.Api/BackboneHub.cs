// <copyright file="BackboneHub.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
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
/// behind drops the oldest queued frame; the drop is counted and
/// surfaced in the stream itself and in the host metrics, with the
/// sequence the consumer must resume from.
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
            detail: $"subscription {stream.SubscriptionId} dropped a frame at depth {QueueDepth}");
    }
}

/// <summary>
/// One open backbone stream of the hub: the registered TBEP sink and
/// the bounded queue the SSE/WebSocket endpoint drains. Frames are
/// dropped (oldest first, counted, surfaced) only when the consumer
/// falls a full depth behind.
/// </summary>
public sealed class BackboneStream : TbepChannel.ITbepNotificationSink
{
    private readonly BackboneHub hub;
    private readonly Channel<StreamEvent> queue = Channel.CreateBounded<StreamEvent>(
        new BoundedChannelOptions(BackboneHub.QueueDepth) { SingleReader = true, SingleWriter = false });
    private long dropped;
    private ulong lastSequence;

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
            return await this.queue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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

        this.queue.Writer.TryComplete();
    }

    private void Offer(StreamEvent frame)
    {
        if (this.queue.Writer.TryWrite(frame))
        {
            return;
        }

        // A full bounded queue drops the oldest queued frame to take this
        // one; the drop is counted, surfaced in-band (the resume point it
        // implies), audited, and summed into the host metrics — never
        // hidden, never unbounded.
        Interlocked.Increment(ref this.dropped);
        this.hub.OnDrop(this);
        if (this.queue.Reader.TryRead(out _))
        {
            this.queue.Writer.TryWrite(new StreamEvent.Dropped(this.LastSequence));
        }

        this.queue.Writer.TryWrite(frame);
    }
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
    /// <param name="ResumeFrom">The last sequence still served before the drop.</param>
    public sealed record Dropped(ulong ResumeFrom) : StreamEvent;

    /// <summary>The engine generation was lost; the consumer reconnects and resumes from its last sequence.</summary>
    public sealed record GenerationLost() : StreamEvent;
}
