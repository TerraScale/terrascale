using System.Diagnostics.Metrics;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.Replication.V1;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Replication;

/// <summary>Whether this member may serve: a catalog follower only once it has caught up with its primary.</summary>
public interface ICatalogReadiness
{
    /// <summary>
    /// True on a catalog primary. On a follower, once it applied the primary's head in this process, and not while the primary
    /// reports a head it has not applied yet (docs/decisions/0009).
    /// </summary>
    bool IsReady { get; }
}

/// <summary>
/// Acts on the catalog a follower applied (docs/decisions/0009), on the follower's stream loop, with the catalog in the state
/// it acts on: keep it short, and never write the catalog. A failure of <see cref="AppliedAsync"/> or
/// <see cref="ResumeAsync"/> stops the follower from applying more until <see cref="ResumeAsync"/> succeeds, so work the
/// catalog still names is never skipped.
/// </summary>
public interface ICatalogApplyObserver
{
    /// <summary>The system collection whose applied documents (not deletions) <see cref="AppliedAsync"/> receives.</summary>
    string Collection { get; }

    /// <summary>Called with the documents of <see cref="Collection"/> one group or snapshot applied, after it committed.</summary>
    ValueTask AppliedAsync(IReadOnlyList<DocumentChange> documents, CancellationToken cancellationToken);

    /// <summary>Called before each stream: acts on what the catalog holds, which a stop or a failure may have left undone.</summary>
    ValueTask ResumeAsync(CancellationToken cancellationToken);

    /// <summary>Called once per process, when the follower first caught up with its primary.</summary>
    ValueTask CaughtUpAsync(CancellationToken cancellationToken);
}

/// <summary>A catalog primary is always ready.</summary>
internal sealed class CatalogPrimaryReadiness : ICatalogReadiness
{
    public bool IsReady => true;
}

/// <summary>
/// Holds one <c>CatalogReplication.Follow</c> stream to <see cref="TerraBaseOptions.CatalogPrimary"/> and applies what it
/// sends into the system database: a snapshot in one transaction, then each group in one transaction, after checking its
/// sequence and recomputing its chained digest. Any failure rolls back, ends the stream and reconnects with capped,
/// jittered backoff; the stream resumes from the journal head committed with the last group. A snapshot adopts the primary's
/// origin, so this catalog then follows only that primary's journal: a catalog of another origin is never replaced.
/// </summary>
internal sealed partial class CatalogFollower : BackgroundService, ICatalogReadiness
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly ISystemReplication _system;
    private readonly PeerCredentials _credentials;
    private readonly TerraBaseOptions _options;
    private readonly ILogger<CatalogFollower> _logger;
    private readonly TimeProvider _time;

    private readonly Counter<long> _failures;
    private readonly ICatalogApplyObserver[] _observers;
    private readonly HashSet<string> _observedCollections;

    // Observers acting on the first catch-up, off the stream loop (a file audit can take long); awaited when the follower stops.
    private Task _caughtUpObservers = Task.CompletedTask;

    // Documents of an observed collection the current transaction applied; only the stream loop uses it.
    private readonly List<DocumentChange> _observed = [];
    private volatile bool _caughtUp;
    private int _connected;
    private long _applied;
    private long _head;
    private Progress _progress = new(Guid.Empty, 0);

    public CatalogFollower(
        ISystemReplication system, PeerCredentials credentials, TerraBaseOptions options, ILogger<CatalogFollower> logger, TimeProvider time, IMeterFactory meters,
        IEnumerable<ICatalogApplyObserver> observers)
    {
        ArgumentNullException.ThrowIfNull(meters);
        (_system, _credentials, _options, _logger, _time, _observers) = (system, credentials, options, logger, time, observers.ToArray());
        _observedCollections = new HashSet<string>(_observers.Select(observer => observer.Collection), StringComparer.Ordinal);
        var meter = meters.Create(Telemetry.MeterName);
        meter.CreateObservableGauge("terrabase.catalog.follower.connected", () => Volatile.Read(ref _connected), "{state}",
            "One while a catalog follower holds a stream to its primary.");
        meter.CreateObservableGauge("terrabase.catalog.follower.lag", () => Math.Max(0, Interlocked.Read(ref _head) - Interlocked.Read(ref _applied)), "{group}",
            "Catalog groups the primary last reported that this follower has not applied.");
        meter.CreateObservableGauge("terrabase.catalog.applied_sequence", () => Interlocked.Read(ref _applied), "{group}",
            "The last catalog primary group this follower applied.");
        _failures = meter.CreateCounter<long>("terrabase.catalog.follower.failures", "{stream}",
            "Catalog streams that ended in failure, by reason: lineage (a populated local catalog of another origin), invalid (a frame failed validation), apply (acting on applied changes failed; retried before the next stream) or stream.");
    }

    // Not ready before the first catch-up, nor while the primary reports a head this follower has not applied (after a
    // reconnect); a primary that cannot be reached leaves the last head, so a caught-up follower keeps serving.
    public bool IsReady => _caughtUp && Interlocked.Read(ref _head) <= Interlocked.Read(ref _applied);

    /// <summary>
    /// Whether this follower holds a stream to its primary and applies the journal of <paramref name="origin"/> (16 bytes), so
    /// <see cref="WaitAppliedAsync"/> can end before it times out.
    /// </summary>
    public bool Follows(ReadOnlyMemory<byte> origin) =>
        Volatile.Read(ref _connected) == 1 && Volatile.Read(ref _progress).Origin == new Guid(origin.Span);

    /// <summary>
    /// Completes once this follower applied group <paramref name="sequence"/> of the journal of <paramref name="origin"/>
    /// (16 bytes): the primary's head after it served a forwarded request, which this follower serves from then on.
    /// </summary>
    public async Task WaitAppliedAsync(ReadOnlyMemory<byte> origin, long sequence, CancellationToken cancellationToken)
    {
        var journal = new Guid(origin.Span);
        while (Volatile.Read(ref _progress) is var progress && (progress.Origin != journal || progress.Sequence < sequence))
        {
            await progress.Next.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _caughtUpObservers.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = InitialBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var observer in _observers)
                {
                    await ActAsync(() => observer.ResumeAsync(stoppingToken)).ConfigureAwait(false);
                }

                using var credentials = _credentials.Capture();
                using var channel = PeerChannel.Create(_options.CatalogPrimary!, credentials);
                await FollowAsync(new CatalogReplication.CatalogReplicationClient(channel), stoppingToken).ConfigureAwait(false);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObserverException error)
            {
                _failures.Add(1, new KeyValuePair<string, object?>("reason", "apply"));
                LogObserverFailed(_logger, error.InnerException!);
            }
            catch (LineageException)
            {
                _failures.Add(1, new KeyValuePair<string, object?>("reason", "lineage"));
                LogLineage(_logger);
            }
            catch (InvalidDataException error)
            {
                _failures.Add(1, new KeyValuePair<string, object?>("reason", "invalid"));
                LogInvalid(_logger, error);
            }
            catch (Exception error)
            {
                _failures.Add(1, new KeyValuePair<string, object?>("reason", "stream"));
                LogStreamFailed(_logger, error);
            }

            // A stream the primary welcomed made progress: retry soon after it breaks.
            var welcomed = Interlocked.Exchange(ref _connected, 0) == 1;
            backoff = welcomed ? InitialBackoff : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            try
            {
                await Task.Delay(TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * backoff.Ticks)), _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // Ends only by throwing: the primary streams until either side stops.
    private async Task FollowAsync(CatalogReplication.CatalogReplicationClient client, CancellationToken stoppingToken)
    {
        var head = await _system.GetHeadAsync(stoppingToken).ConfigureAwait(false);
        var request = new FollowRequest
        {
            Protocol = CatalogProtocol.Version,
            Codecs = { PayloadCodec.Lz4BlockV1 },
            Origin = UnsafeByteOperations.UnsafeWrap(head.Origin),
            Sequence = (ulong)head.Sequence,
            Digest = UnsafeByteOperations.UnsafeWrap(head.Digest),
            Region = _options.Region,
        };

        using var call = client.Follow(request, cancellationToken: stoppingToken);
        var stream = new Frames(call.ResponseStream, stoppingToken);
        var welcome = (await stream.NextAsync().ConfigureAwait(false)).Welcome
            ?? throw new InvalidDataException("the catalog primary did not start with Welcome");
        if (welcome.Protocol != CatalogProtocol.Version || welcome.Origin.Length != 16)
        {
            throw new InvalidDataException("the catalog primary answered an unsupported protocol or origin");
        }

        Volatile.Write(ref _connected, 1);
        var origin = welcome.Origin.Memory;
        // Groups extend the head only when it is of this primary's journal; otherwise a snapshot comes first. The primary's
        // head counts only once this catalog is of its journal: before that, its sequences mean nothing here.
        var position = head.Origin.Span.SequenceEqual(origin.Span) ? head : null;
        if (position is not null)
        {
            Applied(position);
            Interlocked.Exchange(ref _head, (long)welcome.Head);
        }
        while (true)
        {
            var frame = await stream.NextAsync().ConfigureAwait(false);
            switch (frame.FrameCase)
            {
                case FollowResponse.FrameOneofCase.SnapshotBegin:
                    _observed.Clear();
                    position = await ApplySnapshotAsync(stream, origin, frame.SnapshotBegin, sameLineage: position is not null, stoppingToken)
                        .ConfigureAwait(false);
                    Applied(position);
                    Interlocked.Exchange(ref _head, (long)welcome.Head);
                    await NotifyAsync(stoppingToken).ConfigureAwait(false);
                    break;
                case FollowResponse.FrameOneofCase.GroupBegin:
                    _observed.Clear();
                    var replica = await _system.BeginReplicaAsync(adopt: null, stoppingToken).ConfigureAwait(false);
                    await using (replica.ConfigureAwait(false))
                    {
                        position = await ApplyGroupAsync(stream, replica, position ?? throw new InvalidDataException("a catalog group arrived before a snapshot"),
                            frame.GroupBegin, stoppingToken).ConfigureAwait(false);
                        await replica.CommitAsync(position, 0, stoppingToken).ConfigureAwait(false);
                    }

                    await NotifyAsync(stoppingToken).ConfigureAwait(false);
                    break;
                case FollowResponse.FrameOneofCase.Heartbeat:
                    Interlocked.Exchange(ref _head, (long)frame.Heartbeat.Head);
                    // The primary sends a heartbeat once it has sent everything up to its head.
                    if (position is not null && position.Sequence >= (long)frame.Heartbeat.Head && !_caughtUp)
                    {
                        _caughtUp = true;
                        LogCaughtUp(_logger, position.Sequence);
                        _caughtUpObservers = Task.Run(async () =>
                        {
                            foreach (var observer in _observers)
                            {
                                await ObserveAsync(() => observer.CaughtUpAsync(stoppingToken), stoppingToken).ConfigureAwait(false);
                            }
                        }, CancellationToken.None);
                    }

                    break;
                default:
                    throw new InvalidDataException("the catalog primary sent an unexpected frame");
            }

            if (position is not null)
            {
                Applied(position);
            }
        }
    }

    // Publishes the applied position to the lag gauge and to forwarded requests waiting for it; only the stream loop calls it.
    private void Applied(JournalPosition position)
    {
        Interlocked.Exchange(ref _applied, position.Sequence);
        var journal = new Guid(position.Origin.Span);
        if (Volatile.Read(ref _progress) is var current && (current.Origin != journal || current.Sequence != position.Sequence))
        {
            Interlocked.Exchange(ref _progress, new Progress(journal, position.Sequence)).Next.TrySetResult();
        }
    }

    private async Task<JournalPosition> ApplySnapshotAsync(
        Frames stream, ReadOnlyMemory<byte> origin, SnapshotBegin begin, bool sameLineage, CancellationToken cancellationToken)
    {
        // A catalog of another origin (this member's own, another primary's after its volume was lost, another cluster's)
        // is never discarded: only an empty one, or one of this primary's journal, takes a snapshot.
        if (!sameLineage
            && (await _system.ReadDocumentsAsync(null, null, 1, 1, cancellationToken).ConfigureAwait(false)).Documents.Count > 0)
        {
            throw new LineageException();
        }

        if (begin.Digest.Length != JournalDigest.Length)
        {
            throw new InvalidDataException("a catalog snapshot begins without a valid digest");
        }

        var position = new JournalPosition(origin, (long)begin.Sequence, begin.Digest.Memory);
        var replica = await _system.BeginReplicaAsync(adopt: origin, cancellationToken).ConfigureAwait(false);
        await using (replica.ConfigureAwait(false))
        {
            var replaying = false;
            while (true)
            {
                var frame = await stream.NextAsync().ConfigureAwait(false);
                switch (frame.FrameCase)
                {
                    case FollowResponse.FrameOneofCase.Documents when !replaying:
                        foreach (var document in CatalogProtocol.Read(frame.Documents))
                        {
                            await replica.ReplaceAsync(document.Json is null ? throw new InvalidDataException("a catalog snapshot holds a deletion") : document,
                                cancellationToken).ConfigureAwait(false);
                            Observe(document);
                        }

                        break;
                    case FollowResponse.FrameOneofCase.GroupBegin:
                        replaying = true;
                        position = await ApplyGroupAsync(stream, replica, position, frame.GroupBegin, cancellationToken).ConfigureAwait(false);
                        break;
                    case FollowResponse.FrameOneofCase.SnapshotEnd:
                        if ((long)frame.SnapshotEnd.Sequence != position.Sequence || !frame.SnapshotEnd.Digest.Span.SequenceEqual(position.Digest.Span))
                        {
                            throw new InvalidDataException("a catalog snapshot ends at another journal position than it replayed");
                        }

                        await replica.CommitAsync(position, (long)Math.Min(frame.SnapshotEnd.CommitOffset, (ulong)VersionClock.MaxVersion), cancellationToken)
                            .ConfigureAwait(false);
                        LogSnapshot(_logger, position.Sequence);
                        return position;
                    default:
                        throw new InvalidDataException("the catalog primary sent an unexpected frame in a snapshot");
                }
            }
        }
    }

    // Applies one group into replica: its sequence must follow position, and its digest must chain from position's.
    private async Task<JournalPosition> ApplyGroupAsync(
        Frames stream, ISystemReplicaTransaction replica, JournalPosition position, GroupBegin begin, CancellationToken cancellationToken)
    {
        if ((long)begin.Sequence != position.Sequence + 1 || begin.ChangeCount is 0 or > ISystemReplication.MaxGroupChanges)
        {
            throw new InvalidDataException("a catalog group does not follow the journal head or has an invalid size");
        }

        using var digest = new JournalDigest.Group(position.Origin.Span, (long)begin.Sequence, position.Digest.Span, (int)begin.ChangeCount);
        var (applied, bytes) = (0, 0L);
        while (applied < begin.ChangeCount)
        {
            var frame = await stream.NextAsync().ConfigureAwait(false);
            if (frame.FrameCase != FollowResponse.FrameOneofCase.Changes)
            {
                throw new InvalidDataException("a catalog group ended before all of its changes arrived");
            }

            foreach (var change in CatalogProtocol.Read(frame.Changes))
            {
                // Throws past the declared count.
                digest.Append(change);
                bytes += Encoding.UTF8.GetByteCount(change.Collection) + Encoding.UTF8.GetByteCount(change.Id) + (change.Json?.Length ?? 0);
                if (bytes > ISystemReplication.MaxGroupBytes)
                {
                    throw new InvalidDataException("a catalog group exceeds the system group byte limit");
                }

                await replica.ReplaceAsync(change, cancellationToken).ConfigureAwait(false);
                Observe(change);
                applied++;
            }
        }

        var end = await stream.NextAsync().ConfigureAwait(false);
        var computed = digest.Complete();
        return end.FrameCase == FollowResponse.FrameOneofCase.GroupEnd && end.GroupEnd.Digest.Span.SequenceEqual(computed)
            ? new JournalPosition(position.Origin, (long)begin.Sequence, computed)
            : throw new InvalidDataException("a catalog group's digest does not match its changes");
    }

    private void Observe(DocumentChange change)
    {
        if (change.Json is not null && _observedCollections.Contains(change.Collection))
        {
            _observed.Add(change);
        }
    }

    // After the transaction that applied them committed, on this loop.
    private async Task NotifyAsync(CancellationToken cancellationToken)
    {
        foreach (var observer in _observers)
        {
            var documents = _observed.Where(change => string.Equals(change.Collection, observer.Collection, StringComparison.Ordinal)).ToList();
            if (documents.Count > 0)
            {
                await ActAsync(() => observer.AppliedAsync(documents, cancellationToken)).ConfigureAwait(false);
            }
        }

        _observed.Clear();
    }

    // Ends the stream on a failure: what failed is retried by ResumeAsync before the next one.
    private static async Task ActAsync(Func<ValueTask> observe)
    {
        try
        {
            await observe().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new ObserverException(error);
        }
    }

    private async Task ObserveAsync(Func<ValueTask> observe, CancellationToken cancellationToken)
    {
        try
        {
            await observe().ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            LogObserverFailed(_logger, error);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    // Reads the next frame within the progress timeout; the primary heartbeats while idle.
    private sealed class Frames(IAsyncStreamReader<FollowResponse> reader, CancellationToken stoppingToken)
    {
        public async Task<FollowResponse> NextAsync()
        {
            using var progress = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            progress.CancelAfter(CatalogProtocol.ProgressTimeout);
            try
            {
                return await reader.MoveNext(progress.Token).ConfigureAwait(false)
                    ? reader.Current
                    : throw new IOException("the catalog primary ended the stream");
            }
            catch (Exception error) when (error is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled } && !stoppingToken.IsCancellationRequested)
            {
                throw new TimeoutException("the catalog primary sent nothing within the progress timeout", error);
            }
        }
    }

    // An applied journal position; Next completes when a later one replaces it.
    private sealed class Progress(Guid origin, long sequence)
    {
        public Guid Origin { get; } = origin;

        public long Sequence { get; } = sequence;

        public TaskCompletionSource Next { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ObserverException(Exception inner) : Exception("acting on applied catalog changes failed", inner);

    private sealed class LineageException() : Exception("the local catalog belongs to another origin than the catalog primary's");

    [LoggerMessage(Level = LogLevel.Warning, Message = "The catalog stream from the primary failed; reconnecting")]
    private static partial void LogStreamFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The catalog primary sent a frame that failed validation; nothing of it was applied")]
    private static partial void LogInvalid(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Error, Message = "This member's catalog is not empty and belongs to another origin than its catalog primary's journal; it is kept and this member does not follow. Point catalog-primary at the primary of this catalog, or remove its _system.db files to follow this one")]
    private static partial void LogLineage(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Acting on an applied catalog change failed; the catalog itself was applied")]
    private static partial void LogObserverFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied a catalog snapshot through primary group {Sequence}")]
    private static partial void LogSnapshot(ILogger logger, long sequence);

    [LoggerMessage(Level = LogLevel.Information, Message = "The catalog caught up with its primary at group {Sequence}; this member is ready")]
    private static partial void LogCaughtUp(ILogger logger, long sequence);
}
