using System.Runtime.CompilerServices;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Primitives;

namespace TerraBase.Features.Backup;

/// <summary>
/// Reads a file's backup back: lists its snapshots, and rebuilds the file in scratch space from a snapshot rolled forward
/// through its generation's segments. Restores and restore drills both go through here.
/// </summary>
internal sealed class BackupReader(IWalBackup engine, IObjectStore store, DataKeys keys, TerraBaseOptions options, BackupReadObservation? observation = null)
{
    // Segments downloading at once.
    private const int Prefetch = 8;
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
    /// the file, which the caller deletes with <see cref="Delete"/>, and the log position it reached.
    /// </summary>
    /// <exception cref="BackupHistoryIncompleteException">A segment or object is missing.</exception>
    /// <exception cref="InvalidDataException">The backup fails authentication or its integrity check.</exception>
    public Task<(string Path, long Position)> BuildAsync(
        string file, string scope, SnapshotKey snapshot, DateTimeOffset at, long? until, CancellationToken cancellationToken) =>
        BuildAsync(file, scope, snapshot, at, until, next: null, cancellationToken);

    /// <summary>
    /// <see cref="BuildAsync(string, string, SnapshotKey, DateTimeOffset, long?, CancellationToken)"/>; without
    /// <paramref name="until"/>, the generation's history must also be proven complete up to <paramref name="at"/>: the
    /// snapshot was taken in <paramref name="at"/>'s millisecond, or the segments read after it, replayed only up to the
    /// transactions that ran before it, reach a transaction committed at or after it, a read mark at or after it, or
    /// exactly the log <paramref name="next"/>, the generation's following snapshot, holds.
    /// </summary>
    /// <exception cref="BackupHistoryIncompleteException">A segment is missing, or the history up to the time is unproven.</exception>
    /// <exception cref="RestorePointUnprovenException">The time cannot be placed between two transactions.</exception>
    /// <exception cref="InvalidDataException">The backup fails authentication or its integrity check.</exception>
    public async Task<(string Path, long Position)> BuildAsync(
        string file, string scope, SnapshotKey snapshot, DateTimeOffset at, long? until, SnapshotKey? next, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_scratch);
        var restored = Path.Combine(_scratch, EntityIds.New().ToString() + ".db");
        try
        {
            await using (var copy = new FileStream(restored, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                await DownloadAsync(snapshot.Key, scope, copy, cancellationToken).ConfigureAwait(false);
            }

            var reached = new StrongBox<long>(snapshot.Position);
            var segments = SegmentsAsync(file, scope, snapshot, at, until, next, reached, cancellationToken);
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

            return (restored, reached.Value);
        }
        catch
        {
            Delete(restored);
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

    /// <summary>Deletes a file <see cref="BuildAsync(string, string, SnapshotKey, DateTimeOffset, long?, SnapshotKey?, CancellationToken)"/> returned, with whatever SQLite left next to it.</summary>
    public static void Delete(string path)
    {
        foreach (var leftover in new[] { "", "-journal", "-wal", "-shm" })
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
        string file, string scope, SnapshotKey snapshot, DateTimeOffset at, long? until, SnapshotKey? next, StrongBox<long> reached,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inFlight = new Queue<(SegmentKey Segment, Task<FileStream> Log)>();
        var expected = snapshot.Position;
        SegmentKey? boundary = null;
        var marked = false;
        var listing = store.ListAsync(BackupLayout.Segments(file, snapshot.Generation),
            BackupLayout.SegmentsBefore(file, snapshot.Generation, snapshot.Position), cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await NextAsync(listing).ConfigureAwait(false) is { } segment)
            {
                if (segment.At > at || segment.Start >= until)
                {
                    boundary = segment;
                    break;
                }

                inFlight.Enqueue((segment, DownloadSegmentAsync(segment.Key, scope, stop.Token)));
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
            for (var segment = boundary; next?.Position != expected; segment = await NextAsync(listing).ConfigureAwait(false))
            {
                if (segment?.Start != expected)
                {
                    throw new BackupHistoryIncompleteException(segment is null
                        ? "the backup of this database cannot prove its history up to the requested time"
                        : "the backup of this database is missing a log segment");
                }

                var frames = await DownloadSegmentAsync(segment.Key, scope, stop.Token).ConfigureAwait(false);
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

            await listing.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<SegmentKey?> NextAsync(IAsyncEnumerator<StoredObject> listing)
    {
        while (await listing.MoveNextAsync().ConfigureAwait(false))
        {
            if (BackupLayout.ParseSegment(listing.Current.Key) is { } segment)
            {
                return segment;
            }
        }

        return null;
    }

    private static async Task<(Stream Log, long Expected)> TakeAsync(Queue<(SegmentKey Segment, Task<FileStream> Log)> inFlight, long expected)
    {
        var (segment, download) = inFlight.Dequeue();
        var frames = await download.ConfigureAwait(false);
        if (segment.Start != expected)
        {
            await frames.DisposeAsync().ConfigureAwait(false);
            throw new BackupHistoryIncompleteException("the backup of this database is missing a log segment");
        }

        return (frames, expected + frames.Length);
    }

    private async Task<FileStream> DownloadSegmentAsync(string key, string scope, CancellationToken cancellationToken)
    {
        var frames = new FileStream(Path.Combine(_scratch, EntityIds.New().ToString() + ".frames"), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            // A record cut short fails the replay; the sealed object's last-chunk flag already catches a truncated download.
            await DownloadAsync(key, scope, frames, cancellationToken).ConfigureAwait(false);
            frames.Position = 0;
            return frames;
        }
        catch
        {
            await frames.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // A stream the store drops mid-object is read again from the start, up to three attempts.
    private async Task DownloadAsync(string key, string scope, Stream output, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            output.SetLength(0);
            bool found;
            try
            {
                found = observation is null
                    ? await store.ReadAsync(key, (content, readToken) => BackupObject.ReadAsync(content, output, key, id => keys.GetAsync(scope, id, readToken), readToken),
                        cancellationToken).ConfigureAwait(false)
                    : await ReadObservedAsync(key, scope, output, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (attempt < 3 && error is not OperationCanceledException)
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
