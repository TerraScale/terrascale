// <copyright file="DirectSurface.cs" company="TerraScale">
// TerraBaseV2 (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli;

using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using TerraBase.Protocol;

/// <summary>
/// The direct-TBEP mode: the same typed operation set as the host
/// mode, served against a local engine process through the SE-14
/// <see cref="TbepChannel"/> over the required UDS binding
/// (PD-2026-08-26-11 D1) — the shared client library, never a raw
/// socket and never a C ABI. One channel is bound once to the
/// backend-supplied context and multiplexed between the typed calls
/// and the backbone subscription's unsolicited notifications.
/// </summary>
public sealed class DirectTbepSurface : ITerraBaseSurface
{
    private readonly CliConfiguration configuration;
    private TbepChannel channel;
    private ulong nextIdentity;
    private bool disposed;

    private DirectTbepSurface(CliConfiguration configuration, TbepChannel channel)
    {
        this.configuration = configuration;
        this.channel = channel;
    }

    /// <summary>Connects and binds the channel to the backend-supplied context.</summary>
    /// <param name="configuration">The resolved configuration.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The bound surface.</returns>
    public static async Task<DirectTbepSurface> ConnectAsync(CliConfiguration configuration, CancellationToken cancellationToken)
    {
        var channel = await TryAsync(configuration, () => Task.FromResult(TbepChannel.Connect(configuration.SocketPath))).ConfigureAwait(false);
        await TryAsync(
            configuration,
            () => channel.BindChannelAsync(configuration.Principal, configuration.Tenant, cancellationToken)).ConfigureAwait(false);
        return new DirectTbepSurface(configuration, channel);
    }

    /// <inheritdoc/>
    public async Task<StatusReport> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var health = await this.CallAsync(channel => channel.SupervisionHealthAsync(cancellationToken)).ConfigureAwait(false);
        var admin = await this.CallAsync(channel => channel.AdminStatusAsync(cancellationToken)).ConfigureAwait(false);
        var topology = await this.CallAsync(channel => channel.TopologyReadAsync(cancellationToken)).ConfigureAwait(false);
        var state = health.State.Variant;
        return new StatusReport(
            HealthStatus: state is "Serving" or "Starting" ? "serving" : "unavailable",
            EngineState: state,
            Sessions: health.Sessions,
            Subscriptions: health.Subscriptions,
            FramesServed: health.FramesServed,
            Generation: 1,
            Restarts: 0,
            Detail: state is "Serving" or "Starting" ? null : $"the engine reports {state.SnakeCase()}",
            RegistryVersion: admin.RegistryVersion,
            Scopes: admin.Scopes,
            Binding: topology.Binding.Variant,
            Subsystems: topology.Subsystems,
            EngineTopologyAvailable: topology.EngineTopologyAvailable);
    }

    /// <inheritdoc/>
    public async Task<DocumentReadOutcome> ReadDocumentAsync(string collection, string document, CancellationToken cancellationToken)
    {
        var answer = await this.CallAsync(channel => channel.DocumentReadAsync(collection, document, cancellationToken)).ConfigureAwait(false);
        return answer.Found && answer.Document is { } body
            ? new DocumentReadOutcome(true, body.Id, body.Collection, body.Version, Values.ToPlain(body.Body))
            : new DocumentReadOutcome(false, null, null, null, null);
    }

    /// <inheritdoc/>
    public async Task<WriteOutcome> WriteDocumentAsync(string collection, string document, JsonElement body, string? expected, string idempotencyKey, CancellationToken cancellationToken)
    {
        var put = new DocumentPut { Id = document, Collection = collection, Body = Values.ToValue(body) };
        var answer = await this.CallAsync(
            channel => channel.DocumentWriteAsync(put, ExpectedVersionOf(expected), idempotencyKey, idempotencyKey, cancellationToken)).ConfigureAwait(false);
        return OutcomeOf(answer.Outcome);
    }

    /// <inheritdoc/>
    public async Task<WriteOutcome> DeleteDocumentAsync(string collection, string document, string? expected, string idempotencyKey, CancellationToken cancellationToken)
    {
        var answer = await this.CallAsync(
            channel => channel.DocumentDeleteAsync(collection, document, ExpectedVersionOf(expected), idempotencyKey, idempotencyKey, cancellationToken)).ConfigureAwait(false);
        return OutcomeOf(answer.Outcome);
    }

    /// <inheritdoc/>
    public async Task<QueryOutcome> ExecuteQueryAsync(string text, uint maxItems, CancellationToken cancellationToken)
    {
        var queryId = this.NextIdentity();
        var answer = await this.CallAsync(channel => channel.QueryExecuteAsync(queryId, text, maxItems, cancellationToken)).ConfigureAwait(false);
        return new QueryOutcome(
            [.. answer.Documents.Select(body => new DocumentReadOutcome(true, body.Id, body.Collection, body.Version, Values.ToPlain(body.Body)))],
            answer.Count,
            answer.Terminal.Variant.SnakeCase());
    }

    /// <inheritdoc/>
    public async Task<string> CancelQueryAsync(ulong queryId, CancellationToken cancellationToken)
    {
        var answer = await this.CallAsync(channel => channel.QueryCancelAsync(queryId, cancellationToken)).ConfigureAwait(false);
        return answer.Outcome.Variant.SnakeCase();
    }

    /// <inheritdoc/>
    public async Task<IChangesTail> OpenChangesAsync(ulong from, string[]? collections, CancellationToken cancellationToken)
    {
        var subscriptionId = this.NextIdentity();
        var sink = new Sink();
        var answer = await this.CallAsync(
            channel => channel.BackboneSubscribeAsync(subscriptionId, collections, from, sink, cancellationToken)).ConfigureAwait(false);
        sink.Open(answer.StartSequence);
        return new DirectChangesTail(this, sink, subscriptionId);
    }

    /// <inheritdoc/>
    public async Task<SessionsReport> ReadSessionsAsync(CancellationToken cancellationToken)
    {
        // Honest, not invented: the session registry is the API host's
        // tracked state; a direct channel reports the engine's counters.
        var admin = await this.CallAsync(channel => channel.AdminStatusAsync(cancellationToken)).ConfigureAwait(false);
        return new SessionsReport(false, null, admin.Sessions);
    }

    /// <inheritdoc/>
    public Task<JsonElement> ReadAuditAsync(int limit, CancellationToken cancellationToken) =>
        ThrowHostOnly();

    /// <inheritdoc/>
    public Task<JsonElement> ReadMetricsAsync(CancellationToken cancellationToken) =>
        ThrowHostOnly();

    /// <inheritdoc/>
    public async Task<ShutdownReport> ShutdownAsync(string reason, CancellationToken cancellationToken)
    {
        var why = reason == "supervisor" ? ShutdownReason.Supervisor() : ShutdownReason.Operator();
        var answer = await this.CallAsync(channel => channel.SupervisionShutdownAsync(why, cancellationToken)).ConfigureAwait(false);
        return new ShutdownReport(answer.State.Variant, answer.InFlight);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        this.disposed = true;
        await this.channel.DisposeAsync().ConfigureAwait(false);
    }

    private static Task<JsonElement> ThrowHostOnly() =>
        throw new SurfaceUsageException("the audit log and metrics are the API host's surfaces; run them in host mode");

    private static ExpectedVersion ExpectedVersionOf(string? expected) => expected switch
    {
        null => ExpectedVersion.Absent(),
        "any" => ExpectedVersion.Any(),
        { } text when ulong.TryParse(text, out var version) => ExpectedVersion.Exact(version),
        _ => throw new SurfaceUsageException($"--expected is any or an exact version, not \"{expected}\""),
    };

    private static WriteOutcome OutcomeOf(TerraBase.Protocol.WriteOutcome outcome) => outcome.Variant switch
    {
        "Committed" when outcome.Payload is CommitReceipt receipt =>
            new WriteOutcome("committed", receipt.Sequence, receipt.Version, receipt.OperationId, null),
        "Conflict" => new WriteOutcome("conflict", null, null, null, null),
        "Rejected" when outcome.Payload is WriteReason reason =>
            new WriteOutcome("rejected", null, null, null, reason.Variant.SnakeCase()),
        _ => new WriteOutcome(outcome.Variant.SnakeCase(), null, null, null, null),
    };

    private static async Task<T> TryAsync<T>(CliConfiguration configuration, Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (TbepException failure) when (failure.Code is not null)
        {
            throw new SurfaceRefusalException(failure.Code.Variant.SnakeCase(), failure.Detail);
        }
        catch (Exception failure) when (failure is TbepException or SocketException or IOException)
        {
            throw new SurfaceUnavailableException($"the engine at {configuration.SocketPath} cannot be reached: {failure.Message}");
        }
    }

    private ulong NextIdentity() => ++this.nextIdentity;

    private async Task<T> CallAsync<T>(Func<TbepChannel, Task<T>> call)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(DirectTbepSurface));
        }

        return await TryAsync(this.configuration, () => call(this.channel)).ConfigureAwait(false);
    }

    /// <summary>
    /// The direct tail: the subscription's sink feeds a channel of tail
    /// frames; the follow loop reads it. When the engine channel is
    /// lost, the read surfaces the drop exactly as the host mode's
    /// transport loss does, so the shared resume path runs unchanged.
    /// </summary>
    private sealed class Sink : TbepChannel.ITbepNotificationSink
    {
        private readonly Channel<TailFrame?> frames = Channel.CreateUnbounded<TailFrame?>();

        public void Open(ulong startSequence) =>
            this.frames.Writer.TryWrite(new TailFrame("opened", startSequence, null, null, null, null, null, null, null));

        public void OnBatch(SubscriptionBatch batch)
        {
            foreach (var change in batch.Events)
            {
                this.frames.Writer.TryWrite(new TailFrame(
                    "change-event",
                    change.Sequence,
                    change.Kind.Variant.SnakeCase(),
                    change.Collection,
                    change.Document,
                    change.OperationId,
                    null,
                    null,
                    batch.NextSequence));
            }
        }

        public void OnTrigger(TriggerNotification trigger) =>
            this.frames.Writer.TryWrite(new TailFrame(
                "change-notification",
                trigger.Sequence,
                null,
                trigger.Collection,
                null,
                trigger.OperationId,
                null,
                null,
                null));

        public void OnClosed(SubscriptionClosed closed)
        {
            this.frames.Writer.TryWrite(new TailFrame(
                "closed",
                null,
                null,
                null,
                null,
                null,
                null,
                closed.Reason.Variant.SnakeCase(),
                null));
            this.frames.Writer.TryWrite(null);
        }

        public async Task<TailFrame?> ReadAsync(TbepChannel channel, CancellationToken cancellationToken)
        {
            if (this.frames.Reader.TryRead(out var frame))
            {
                return frame;
            }

            if (channel.IsClosed)
            {
                // The engine channel is gone and no frame is buffered:
                // surface the drop so the shared resume path runs.
                throw new ConnectionDroppedException("the engine channel failed under the subscription");
            }

            return await this.frames.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class DirectChangesTail(DirectTbepSurface surface, Sink sink, ulong subscriptionId) : IChangesTail
    {
        public async Task<TailFrame?> ReadAsync(CancellationToken cancellationToken) =>
            await sink.ReadAsync(surface.channel, cancellationToken).ConfigureAwait(false);

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            try
            {
                await surface.CallAsync(channel => channel.BackboneUnsubscribeAsync(subscriptionId, cancellationToken)).ConfigureAwait(false);
            }
            catch (SurfaceUnavailableException)
            {
                // The bounded close is best effort: the channel's loss
                // already ended the subscription on the engine.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ValueTask.CompletedTask.ConfigureAwait(false);
        }
    }
}
