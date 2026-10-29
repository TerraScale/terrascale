using System.Runtime.CompilerServices;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Primitives;

namespace TerraBase.Features.Backup;

/// <summary>
/// Reads a file's backup back: lists its snapshots, and rebuilds the file in scratch space from a snapshot rolled forward
/// through its generation's segments. Restores and restore drills both go through here.
/// </summary>
internal sealed class BackupReader(IWalBackup engine, IObjectStore store, DataKeys keys, TerraBaseOptions options, IDiskBudget disk, BackupReadObservation? observation = null)
{
    // Segments downloading at once.
    private const int Prefetch = 8;
    // More than a day's one-second segments, with a separate bound on retained object-name memory.
    private const int MaxSizingSegments = 131_072;
    private const long MaxSizingKeyBytes = 32L << 20;
    private readonly string _scratch = ContinuousBackup.Scratch(options);

    /// <summary>Every snapshot of <paramref name="file"/> (a <see cref="BackupLayout.File"/> directory), oldest first.</summary>
    public async IAsyncEnumerable<SnapshotKey> SnapshotsAsync(string file, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in store.ListAsync(BackupLayout.Snapshots(file), null, cancellationToken).ConfigureAwait(false))
        {
            if (BackupLayout.ParseSnapshot(item.Key) is { } snapshot)
            {
                yield return snapshot;
            }
        }
    }

    /// <summary>
    /// Rebuilds <paramref name="file"/> in a new scratch file: <paramref name="snapshot"/> rolled forward through the segments
    /// read by <paramref name="at"/> and, when <paramref name="until"/> is set, starting before that log position. Returns
    /// an owned file and the log position it reached; disposal deletes scratch before releasing admission.
    /// </summary>
    /// <exception cref="BackupHistoryIncompleteException">A segment or object is missing.</exception>
    /// <exception cref="InvalidDataException">The backup fails authentication or its integrity check.</exception>
    public Task<RestoredBackup> BuildAsync(
        string file, string scope, SnapshotKey snapshot, DateTimeOffset at, long? until, CancellationToken cancellationToken) =>
        BuildAsync(file, scope, snapshot, at, until, next: null, cancellationToken);

    /// <summary>
    /// <see cref="BuildAsync(string, string, SnapshotKey, DateTimeOffset, long?, CancellationToken)"/>; without
    /// <paramref name="until"/>, the generation's history must also be proven complete up to <paramref name="at"/>: the
    /// snapshot was taken in <paramref name="at"/>'s millisecond, or the segments read after it, replayed only up to the
    /// transactions that ran before it, reach a transaction committed at or after it, a read mark at or after it, or
    /// exactly the log <paramref name="next"/>, the generation's following snapshot, holds.
    /// </summary>
    /// <exception cref="BackupHistoryIncompleteException">
    /// A segment is missing, or the history up to the time is unproven; for the system database, any time but a snapshot's.
    /// </exception>
    /// <exception cref="RestorePointUnprovenException">The time cannot be placed between two transactions.</exception>
    /// <exception cref="InvalidDataException">The backup fails authentication or its integrity check.</exception>
    public async Task<RestoredBackup> BuildAsync(
        string file, string scope, SnapshotKey snapshot, DateTimeOffset at, long? until, SnapshotKey? next, CancellationToken cancellationToken)
    {
        if (until is null && file.StartsWith(BackupLayout.FileRoot(null) + "/", StringComparison.Ordinal) && !PointInTimeRestore.Covers(snapshot.At, at))
        {
            // A catalog follower applies the leader's writes to its system database without capture, so neither its log
            // nor a later snapshot at the same position shows what was written after a snapshot: only snapshot times hold.
            throw new BackupHistoryIncompleteException("the system database's backup proves only its snapshot times");
        }

        // Decode before creating scratch: stored LZ4 sizes do not bound decoded bytes. Freeze the inventory so
        // later segments cannot allocate outside this claim, including candidates used only to prove the restore time.
        var snapshotBytes = await DecodedBytesAsync(snapshot.Key, scope, cancellationToken).ConfigureAwait(false);
        var bytes = snapshotBytes;
        List<SegmentKey> inventory = [];
        Dictionary<string, Task<BackupLogSummary>> sizes = [with(StringComparer.Ordinal)];
        using var sizingStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new Queue<(SegmentKey Segment, Task<BackupLogSummary> Size)>(Prefetch);
        long keyBytes = 0;
        long creates = 0;
        var expected = snapshot.Position;
        var proving = false;
        var proven = false;
        var lastAt = snapshot.At;
        long? endMark = null;

        async Task<bool> AcceptAsync()
        {
            var (segment, size) = pending.Dequeue();
            var summary = await SizedAsync(size).ConfigureAwait(false);
            inventory.Add(segment);
            sizes.Add(segment.Key, size);
            bytes = checked(bytes + summary.Bytes);
            creates = checked(creates + summary.Creates);
            expected = checked(expected + summary.Bytes);
            (lastAt, endMark) = (segment.At, summary.EndMark);
            return proving && (summary.Boundary || next?.Position == expected);
        }

        try
        {
            await foreach (var item in store.ListAsync(BackupLayout.Segments(file, snapshot.Generation),
                BackupLayout.SegmentsBefore(file, snapshot.Generation, snapshot.Position), sizingStop.Token).ConfigureAwait(false))
            {
                if (BackupLayout.ParseSegment(item.Key) is not { } segment)
                {
                    continue;
                }

                if (segment.Start >= until)
                {
                    break;
                }

                if (!proving && segment.At > at)
                {
                    // Finish the ordinary replay range before deciding whether it already proves this time.
                    while (pending.Count > 0)
                    {
                        _ = await AcceptAsync().ConfigureAwait(false);
                    }

                    if (until is not null || PointInTimeRestore.Covers(snapshot.At, at) || next?.Position == expected
                        || PointInTimeRestore.Covers(lastAt, at) && endMark >= at.ToUnixTimeMilliseconds())
                    {
                        break;
                    }

                    proving = true;
                }

                keyBytes = checked(keyBytes + segment.Key.Length * sizeof(char));
                if (inventory.Count + pending.Count == MaxSizingSegments || keyBytes > MaxSizingKeyBytes)
                {
                    throw new BackupInventoryLimitException();
                }

                pending.Enqueue((segment, SummarizeAsync(segment.Key, scope, at, sizingStop.Token)));
                if (pending.Count == Prefetch && await AcceptAsync().ConfigureAwait(false))
                {
                    proven = true;
                    break;
                }
            }

            while (!proven && pending.Count > 0)
            {
                proven = await AcceptAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await sizingStop.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                foreach (var (_, size) in pending)
                {
                    await ((Task)size).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }

        // Every Create may add an index over four fields, each as large as the data. Charge that full-data estimate
        // per Create, including creates later dropped; WorkspaceBytes also allows DB/WAL/native sorting and import.
        // This is conservative admission, not a filesystem quota against concurrent growth or native amplification.
        var claim = disk.Reserve(DiskBudget.WorkspaceBytes(checked(bytes * (1 + 4 * creates))));
        var restored = Path.Combine(_scratch, EntityIds.New().ToString() + ".db");
        try
        {
            Directory.CreateDirectory(_scratch);
            await using (var bundle = new BackupScratchStream(restored + ".snapshot", null, snapshotBytes))
            {
                await DownloadAsync(snapshot.Key, scope, bundle, cancellationToken).ConfigureAwait(false);
                bundle.Position = 0;
                await SnapshotBundle.ReadAsync(bundle, restored, cancellationToken).ConfigureAwait(false);
            }

            var reached = new StrongBox<long>(snapshot.Position);
            var segments = SegmentsAsync(scope, snapshot, at, until, next, reached, inventory, sizes, cancellationToken);
            using var replay = observation?.Enter(BackupReadObservation.Phase.Replay);
            if (observation is not null)
            {
                segments = ObserveSegmentsAsync(segments, replay.GetValueOrDefault(), cancellationToken);
            }

            if (!await engine.RollForwardAsync(restored, segments, cancellationToken)
                .ConfigureAwait(false))
            {
                throw new InvalidDataException("the restored database fails its integrity check");
            }

            return new RestoredBackup(restored, reached.Value, claim);
        }
        catch
        {
            Delete(restored);
            claim.Dispose();
            throw;
        }
    }

    private async IAsyncEnumerable<Stream> ObserveSegmentsAsync(IAsyncEnumerable<Stream> segments, BackupReadObservation.Scope replay,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var iterator = segments.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool next;
                replay.Pause();
                try
                {
                    using var wait = observation!.Enter(BackupReadObservation.Phase.ReplayWait);
                    next = await iterator.MoveNextAsync().ConfigureAwait(false);
                }
                finally
                {
                    replay.Resume();
                }

                if (!next)
                {
                    yield break;
                }

                yield return iterator.Current;
            }
        }
        finally
        {
            replay.Pause();
            try
            {
                using var wait = observation!.Enter(BackupReadObservation.Phase.ReplayWait);
                await iterator.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                replay.Resume();
            }
        }
    }

    /// <summary>
    /// When the read that appended the end of the segment <paramref name="key"/> started, if that read reached the newest
    /// commit (a read mark): every transaction committed before then is in the log up to the segment's end.
    /// </summary>
    /// <exception cref="BackupHistoryIncompleteException">The segment is gone.</exception>
    /// <exception cref="InvalidDataException">The segment fails authentication or is malformed.</exception>
    public async Task<DateTimeOffset?> ReadMarkAsync(string key, string scope, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_scratch);
        await using var frames = await DownloadSegmentAsync(key, scope, cancellationToken).ConfigureAwait(false);
        return engine.ReadMark(frames);
    }

    /// <summary>Deletes a file <see cref="BuildAsync(string, string, SnapshotKey, DateTimeOffset, long?, SnapshotKey?, CancellationToken)"/> returned, with whatever Turso left next to it.</summary>
    public static void Delete(string path)
    {
        foreach (var leftover in new[] { "", "-journal", "-wal", "-shm", ".key" })
        {
            File.Delete(path + leftover);
        }
    }

    // The segments of the snapshot's generation from its position, up to the time (and before until), in order, each as a
    // scratch file the caller disposes. Up to Prefetch download at once; contiguity is checked in order as they are handed
    // out, and reached ends at the log position the last one handed out ends at. Without until (a restore), the history
    // must then be proven complete up to the time: the snapshot was taken in the time's millisecond (with every writer
    // held), or the segments read after the time are handed out one by one, cut to the transactions that ran before it,
    // up to a transaction committed at or after it, a read mark at or after it, or exactly where the generation's next
    // snapshot is. A segment's own time is when its read finished, so it proves nothing about commits during the read.
    private async IAsyncEnumerable<Stream> SegmentsAsync(
        string scope, SnapshotKey snapshot, DateTimeOffset at, long? until, SnapshotKey? next, StrongBox<long> reached, IReadOnlyList<SegmentKey> inventory,
        Dictionary<string, Task<BackupLogSummary>> sizes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inFlight = new Queue<(SegmentKey Segment, Task<FileStream> Log)>();
        var expected = snapshot.Position;
        SegmentKey? boundary = null;
        var marked = false;
        using var listing = inventory.GetEnumerator();
        try
        {
            while (Next(listing) is { } segment)
            {
                if (segment.At > at || segment.Start >= until)
                {
                    boundary = segment;
                    break;
                }

                inFlight.Enqueue((segment, DownloadSegmentAsync(segment.Key, scope, stop.Token, sizes[segment.Key])));
                if (inFlight.Count == Prefetch)
                {
                    var (frames, after) = await TakeAsync(inFlight, expected).ConfigureAwait(false);
                    expected = reached.Value = after;
                    yield return frames;
                }
            }

            while (inFlight.Count > 0)
            {
                var last = inFlight.Peek().Segment;
                var (frames, after) = await TakeAsync(inFlight, expected).ConfigureAwait(false);
                expected = reached.Value = after;
                if (until is null && inFlight.Count == 0 && PointInTimeRestore.Covers(last.At, at))
                {
                    // Read in the time's millisecond: a read mark at its end at or after the time ends the history.
                    try
                    {
                        marked = engine.ReadMark(frames) is { } mark && PointInTimeRestore.Covers(mark, at);
                        frames.Position = 0;
                    }
                    catch
                    {
                        await frames.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }

                yield return frames;
            }

            // A drill checks its end itself.
            if (until is not null || marked || PointInTimeRestore.Covers(snapshot.At, at))
            {
                yield break;
            }

            long? previous = null;
            for (var segment = boundary; next?.Position != expected; segment = Next(listing))
            {
                if (segment?.Start != expected)
                {
                    throw new BackupHistoryIncompleteException(segment is null
                        ? "the backup of this database cannot prove its history up to the requested time"
                        : "the backup of this database is missing a log segment");
                }

                var frames = await DownloadSegmentAsync(segment.Key, scope, stop.Token, sizes[segment.Key]).ConfigureAwait(false);
                LogCut cut;
                try
                {
                    cut = engine.CutLog(frames, at, previous);
                    if (cut.Unproven is { } reason)
                    {
                        throw new RestorePointUnprovenException(reason);
                    }

                    frames.SetLength(cut.Bytes);
                    frames.Position = 0;
                }
                catch
                {
                    await frames.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                expected = reached.Value = expected + cut.Bytes;
                previous = cut.Last;
                yield return frames;
                if (cut.Reached)
                {
                    yield break;
                }
            }
        }
        finally
        {
            // Stopped early (a failure or a hole): close what is still downloading.
            await stop.CancelAsync().ConfigureAwait(false);
            foreach (var (_, download) in inFlight)
            {
                await Task.WhenAny(download).ConfigureAwait(false);
                if (download.IsCompletedSuccessfully)
                {
                    await download.Result.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    _ = download.Exception;
                }
            }
        }
    }

    private static SegmentKey? Next(IEnumerator<SegmentKey> listing) => listing.MoveNext() ? listing.Current : null;

    private static async Task<(Stream Log, long Expected)> TakeAsync(Queue<(SegmentKey Segment, Task<FileStream> Log)> inFlight, long expected)
    {
        if (inFlight.Peek().Segment.Start != expected)
        {
            // A gap or an overlap, whatever it holds (a copy under another key does not even authenticate); its download
            // stays queued for the caller to cancel.
            throw new BackupHistoryIncompleteException("the backup of this database is missing a log segment");
        }

        var frames = await inFlight.Dequeue().Log.ConfigureAwait(false);
        return (frames, expected + frames.Length);
    }

    private async Task<FileStream> DownloadSegmentAsync(string key, string scope, CancellationToken cancellationToken, Task<BackupLogSummary>? preflight = null)
    {
        var bytes = preflight is null
            ? await DecodedBytesAsync(key, scope, cancellationToken).ConfigureAwait(false)
            : (await preflight.ConfigureAwait(false)).Bytes;
        var claim = preflight is null ? disk.Reserve(bytes) : null;
        BackupScratchStream? frames = null;
        try
        {
            frames = new BackupScratchStream(Path.Combine(_scratch, EntityIds.New().ToString() + ".frames"), claim, bytes);
            // A record cut short fails the replay; the sealed object's last-chunk flag already catches a truncated download.
            await DownloadAsync(key, scope, frames, cancellationToken).ConfigureAwait(false);
            frames.Position = 0;
            return frames;
        }
        catch
        {
            if (frames is not null)
            {
                await frames.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                claim?.Dispose();
            }

            throw;
        }
    }

    private static async Task<BackupLogSummary> SizedAsync(Task<BackupLogSummary> size)
    {
        try
        {
            return await size.ConfigureAwait(false);
        }
        catch (Exception error) when (error is not (OperationCanceledException or DiskCapacityException or OverflowException))
        {
            // A later proof candidate may be unnecessary. Its original task surfaces failure only if replay reaches it.
            return default;
        }
    }

    private async Task<BackupLogSummary> SummarizeAsync(string key, string scope, DateTimeOffset at, CancellationToken cancellationToken)
    {
        using var summary = new BackupLogSummaryStream(at);
        await DownloadAsync(key, scope, summary, cancellationToken).ConfigureAwait(false);
        return summary.Complete();
    }

    private async Task<long> DecodedBytesAsync(string key, string scope, CancellationToken cancellationToken)
    {
        using var count = new BackupCountingStream();
        await DownloadAsync(key, scope, count, cancellationToken).ConfigureAwait(false);
        return count.Length;
    }

    // A stream the store drops mid-object is read again from the start, up to three attempts.
    private async Task DownloadAsync(string key, string scope, Stream output, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            output.SetLength(0);
            if (output.CanSeek)
            {
                output.Position = 0;
            }

            bool found;
            try
            {
                found = observation is null
                    ? await store.ReadAsync(key, (content, readToken) => BackupObject.ReadAsync(content, output, key, id => keys.GetAsync(scope, id, readToken), readToken),
                        cancellationToken).ConfigureAwait(false)
                    : await ReadObservedAsync(key, scope, output, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (attempt < 3 && error is not (OperationCanceledException or DiskCapacityException))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!found)
            {
                throw new BackupHistoryIncompleteException("a backup object of this database disappeared during the restore");
            }

            return;
        }
    }

    private async Task<bool> ReadObservedAsync(string key, string scope, Stream output, CancellationToken cancellationToken)
    {
        using var fetch = observation!.Enter(BackupReadObservation.Phase.Fetch);
        return await store.ReadAsync(key, async (content, readToken) =>
        {
            fetch.Pause();
            try
            {
                using var body = observation.Enter(BackupReadObservation.Phase.Body);
                await BackupObject.ReadAsync(content, output, key, id => keys.GetAsync(scope, id, readToken), readToken, observation).ConfigureAwait(false);
            }
            finally
            {
                // Fetch owns store completion and response cleanup outside the body callback.
                fetch.Resume();
            }
        }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A backup cannot prove a file's history complete up to the requested time or log position.</summary>
/// <param name="message">What is missing or unproven.</param>
internal sealed class BackupHistoryIncompleteException(string message) : Exception(message);

/// <summary>A backup holds a file's history around the requested time but cannot place the time between two transactions.</summary>
/// <param name="message">Why: a transaction within the time's second, one without recorded times, or times that go back.</param>
internal sealed class RestorePointUnprovenException(string message) : Exception(message);

/// <summary>A restored database owns its scratch and admission through import or checksum.</summary>
internal sealed class RestoredBackup(string path, long position, IDisposable claim) : IDisposable
{
    public string Path { get; } = path;
    public long Position { get; } = position;
    public void Dispose()
    {
        BackupReader.Delete(Path);
        claim.Dispose();
    }
}

/// <summary>A delete-on-close scratch file releases admission only after its handle closes.</summary>
internal sealed class BackupScratchStream(string path, IDisposable? claim, long limit = long.MaxValue)
    : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.DeleteOnClose)
{
    private readonly List<IDisposable> _claims = claim is null ? [] : [claim];
    public long Limit { get; set; } = limit;

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        buffer.Length > Limit - Position
            ? throw new InvalidDataException("backup scratch exceeds its preflight size")
            : base.WriteAsync(buffer, cancellationToken);

    public void TakeClaims(List<IDisposable> claims)
    {
        _claims.AddRange(claims);
        claims.Clear();
    }

    private void ReleaseClaims()
    {
        foreach (var owned in _claims)
        {
            owned.Dispose();
        }

        _claims.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            ReleaseClaims();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        ReleaseClaims();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Authenticates and measures decoded objects without allocating scratch.</summary>
internal class BackupCountingStream : Stream
{
    private long _length;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => _length = value;
    public override void Write(byte[] buffer, int offset, int count) => _length = checked(_length + count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _length = checked(_length + buffer.Length);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A restore's sizing inventory cannot fit its fixed metadata allowance.</summary>
internal sealed class BackupInventoryLimitException() : IOException(
    "Backup sizing exceeds 131072 segments or 32 MiB of object names. Restore from a newer snapshot or contact support for backup recovery.");
