using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using TerraBase.Configuration;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Engine.Turso;

/// <summary>A whole pinned source snapshot becomes one durable receiver transaction; incomplete streams roll back.</summary>
internal sealed class TursoReplicaSnapshotImporter(TursoStorageEngine engine, StorageChangeGroupSink sink, StampingStorageEngine stamps,
    IMemoryBudget memory, TerraBaseOptions options, IMeterFactory meters, ILogger<TursoReplicaSnapshotImporter> logger) : IReplicaSnapshotImporter
{
    internal static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan BaseReplayTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxReplayTimeout = TimeSpan.FromMinutes(5);
    // A 20k-state initial import took 6.93 s with eight runtime CPUs. Allow 2 ms/state (500/s), plus frame and byte
    // work, for slow/contended runners. This is a cooperative ceiling, not a promise of sub-second writer holds.
    private const long ReplayTicksPerState = 2 * TimeSpan.TicksPerMillisecond;
    private const long ReplayTicksPerFrame = 10 * TimeSpan.TicksPerMillisecond;
    private const long ReplayBytesPerSecond = 4 * 1024 * 1024;
    private readonly Histogram<double> _timing = meters.Create(Telemetry.MeterName)
        .CreateHistogram<double>("terrabase.replication.snapshot_import", "s", "Snapshot receive, staging and atomic replay time.");

    public async Task<bool> ImportAsync(TenantContext database, Ulid incarnation, IAsyncEnumerable<ReplicaSnapshotPage> pages,
        IReadOnlySet<ulong> origins, CancellationToken cancellationToken, TimeSpan? holdBudget = null)
    {
        using var deadline = new CancellationTokenSource(ImportTimeout, engine.HostTime);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = budget.Token;
        // One decoded/encoded page at a time, including the network enumerator's buffers. The private spool is bounded
        // separately and never survives disposal. No transaction is opened until the terminal frame AND clean EOF arrive.
        using var admission = memory.TryReserve(MemoryLane.Replication, 16L * ReplicationProtocol.MaxApplicationBytes, tenant: null);
        if (admission is null)
        {
            return false;
        }

        var store = database == ChangeGroups.System ? (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)
            : (TursoDatabaseStore?)await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        if (store is null)
        {
            return false;
        }

        var collected = store.CollectionGeneration;
        var file = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            file.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var staged = new FileStream(Path.Combine(options.DataDirectory, $".snapshot-import-{Ulid.NewUlid()}.tmp"), file);
        if (!OperatingSystem.IsWindows())
        {
            File.Delete(staged.Name); // An unlinked private spool also disappears after a process crash.
        }

        var (terminal, states, frames) = await StageAsync(staged, database, incarnation, pages, cancellationToken).ConfigureAwait(false);
        if (terminal is null)
        {
            return false;
        }

        staged.Position = 0;
        var workTicks = states * ReplayTicksPerState + frames * ReplayTicksPerFrame + staged.Length * TimeSpan.TicksPerSecond / ReplayBytesPerSecond;
        if (holdBudget is { } budgetHold && workTicks > budgetHold.Ticks)
        {
            // Staging took no writer. A whole SYSTEM file replays for minutes (sjc/fra, 2026-10-28: 115-300 s every few
            // minutes), and every create, escrow grant and replication push waits behind it.
            var work = TimeSpan.FromTicks(workTicks).TotalSeconds;
            logger.ZLogInformation($"A snapshot import of database {database.DatabaseId:@Database} was skipped before taking its writer: {states:@States} states would hold it about {work:@Seconds} s, over the caller's {budgetHold.TotalSeconds:@Budget} s");
            return false;
        }

        var replayTicks = BaseReplayTimeout.Ticks + workTicks;
        using var replayDeadline = new CancellationTokenSource(TimeSpan.FromTicks(Math.Min(replayTicks, MaxReplayTimeout.Ticks)), engine.HostTime);
        using var replay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, replayDeadline.Token);
        var started = Stopwatch.GetTimestamp();
        stamps.InvalidateAll();
        try
        {
            return await ImportHeldAsync(store, database, incarnation, ReadStagedAsync(staged, database, incarnation, terminal, replay.Token),
                origins, collected, replay.Token).ConfigureAwait(false);
        }
        finally
        {
            _timing.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("phase", "apply"));
            // Imported states and forgotten tombstones bypass the public transaction wrapper.
            stamps.InvalidateAll();
        }
    }

    private async Task<(ReplicaSnapshotPage? Terminal, long States, int Frames)> StageAsync(FileStream staged, TenantContext database, Ulid incarnation,
        IAsyncEnumerable<ReplicaSnapshotPage> pages, CancellationToken cancellationToken)
    {
        int[] buckets = [.. Enumerable.Range(0, 4096)];
        BucketCursor? after = null;
        Hlc? floor = null;
        ReplicaSnapshotPage? terminal = null;
        var count = 0;
        var states = 0L;
        var header = new byte[sizeof(int)];
        var receive = TimeSpan.Zero;
        var staging = TimeSpan.Zero;
        await using var enumerator = pages.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                var received = Stopwatch.GetTimestamp();
                bool more;
                try
                {
                    more = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                finally
                {
                    receive += Stopwatch.GetElapsedTime(received);
                }

                if (!more)
                {
                    break;
                }

                var page = enumerator.Current;
                var started = Stopwatch.GetTimestamp();
                try
                {
                    if (++count > 65_536 || terminal is not null || floor is { } previous && previous != page.CollectedBefore
                        || page.Applied is not null && (page.States.Count != 0 || page.Applied.Count > 1024))
                    {
                        throw new InvalidDataException("inconsistent or oversized snapshot");
                    }

                    floor = page.CollectedBefore;
                    TursoStorageTransaction.ValidateRefillPage(database, buckets, after, page.States);
                    if (page.Applied is not null)
                    {
                        terminal = new ReplicaSnapshotPage([], new Dictionary<ReplicaId, Hlc>(page.Applied), page.CollectedBefore);
                        continue;
                    }

                    var bytes = AntiEntropy.Encode(page.States, incarnation).ToByteArray();
                    if (bytes.Length > ReplicationProtocol.MaxMessageBytes || staged.Position + header.Length + bytes.Length > 1024L * 1024 * 1024)
                    {
                        return default;
                    }

                    BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
                    await staged.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                    await staged.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    states += page.States.Count;
                    if (page.States.Count > 0)
                    {
                        var last = page.States[^1];
                        after = new BucketCursor(TursoStorageTransaction.Bucket(last.Collection, last.Id), last.Collection, last.Id);
                    }
                }
                finally
                {
                    staging += Stopwatch.GetElapsedTime(started);
                }
            }
        }
        finally
        {
            _timing.Record(receive.TotalSeconds, new KeyValuePair<string, object?>("phase", "receive"));
            _timing.Record(staging.TotalSeconds, new KeyValuePair<string, object?>("phase", "stage"));
        }

        return (terminal, states, count);
    }

    private static async IAsyncEnumerable<ReplicaSnapshotPage> ReadStagedAsync(FileStream staged, TenantContext database, Ulid incarnation,
        ReplicaSnapshotPage terminal, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        while (staged.Position < staged.Length)
        {
            await staged.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            var bytes = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
            await staged.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            yield return new ReplicaSnapshotPage(AntiEntropy.Decode(database, Features.Replication.V1.StatePage.Parser.ParseFrom(bytes), incarnation),
                CollectedBefore: terminal.CollectedBefore);
        }

        yield return terminal;
    }

    private async Task<bool> ImportHeldAsync(TursoDatabaseStore store, TenantContext database, Ulid incarnation,
        IAsyncEnumerable<ReplicaSnapshotPage> pages, IReadOnlySet<ulong> origins, long collected, CancellationToken cancellationToken)
    {
        await using var transaction = (TursoStorageTransaction)await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false) != incarnation || store.CollectionGeneration != collected)
        {
            return false;
        }

        int[] buckets = [.. Enumerable.Range(0, 4096)];
        BucketCursor? after = null;
        ReplicaSnapshotPage? terminal = null;
        Hlc? floor = null;
        Dictionary<ReplicaId, Hlc> required = [];
        await foreach (var page in pages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (terminal is not null || floor is { } previous && previous != page.CollectedBefore
                || page.Applied is not null && page.States.Count != 0)
            {
                throw new InvalidDataException("inconsistent snapshot pages");
            }

            floor = page.CollectedBefore;
            TursoStorageTransaction.ValidateRefillPage(database, buckets, after, page.States);
            if (floor.Value.Value > 0)
            {
                var cursor = after;
                do
                {
                    // A live catalog state is forgotten provisionally, and only kept forgotten if the terminal cuts prove the
                    // source applied all of it. Every change still rolls back if that proof or clean EOF is missing.
                    if (await transaction.ReconcileRefillAsync(buckets, cursor, page.States, floor.Value, cancellationToken,
                        database == ChangeGroups.System ? TrackCollected : null).ConfigureAwait(false) is not { } result)
                    {
                        return false;
                    }

                    cursor = result.Next;
                }
                while (cursor is not null);
            }

            _ = await sink.RepairPageAsync(transaction, database, page.States, baseline: true, cancellationToken).ConfigureAwait(false);
            engine.Checkpoint("replication.snapshot.page", database);
            _ = await transaction.ReleaseStateReadsAsync(cancellationToken).ConfigureAwait(false);
            if (page.States.Count > 0)
            {
                var last = page.States[^1];
                after = new BucketCursor(TursoStorageTransaction.Bucket(last.Collection, last.Id), last.Collection, last.Id);
            }

            if (page.Applied is not null)
            {
                terminal = page;
            }
        }

        // Reaching clean EOF is part of the proof. Trailers may fail after the terminal data frame.
        if (terminal is null)
        {
            return false;
        }

        if (required.Any(need => !terminal.Applied!.TryGetValue(need.Key, out var cut) || cut.Value < need.Value.Value))
        {
            return false;
        }

        foreach (var cut in terminal.Applied!.Where(cut => origins.Contains(cut.Key.Value)))
        {
            await transaction.RecordSnapshotAppliedAsync(cut.Key, cut.Value, cancellationToken).ConfigureAwait(false);
        }

        await transaction.InheritCollectedBeforeAsync(floor!.Value, cancellationToken).ConfigureAwait(false);
        _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;

        // TS-1012: every operation still visible in a state is named by its author and stamped at most MaxStamp (a counter's
        // Changed is its last increment). A source that applied each of those authors up to MaxStamp holds every operation
        // of the state, so its collected absence dominates it: the source deleted the document and collected the
        // tombstone. The production survivors were metering documents written by gru and fra and the drop projections of a
        // deleted database's collections, not only the source's own writes.
        bool TrackCollected(DocumentState state)
        {
            var newest = state.MaxStamp;
            foreach (var author in Authors(state))
            {
                if (author.Value is 0 or ulong.MaxValue)
                {
                    return false;
                }

                required[author] = required.TryGetValue(author, out var held) && held.Value >= newest.Value ? held : newest;
            }

            return true;
        }
    }

    private static IEnumerable<ReplicaId> Authors(DocumentState state)
    {
        if (state.Clear is { } clear)
        {
            yield return clear.Version.Node;
        }

        foreach (var field in state.Fields.Values)
        {
            if (field.Base is { } value)
            {
                yield return value.Version.Node;
            }

            // A counter's epoch names the base or clear it counts from, not an operation of its own.
            foreach (var node in field.Counters.Keys)
            {
                yield return node;
            }

            foreach (var (key, dot) in field.Dots)
            {
                yield return key.Version.Node;
                if (dot.RemovedAt is { } removed)
                {
                    yield return removed.Node;
                }
            }
        }
    }
}
