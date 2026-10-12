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
    /// first later segment starts where the replayed ones end, the last replayed segment (or the snapshot) was read in the
    /// same millisecond as <paramref name="at"/>, or <paramref name="next"/>, the generation's following snapshot, holds
    /// exactly the replayed log.
    /// </summary>
    /// <exception cref="BackupHistoryIncompleteException">A segment is missing, or the history up to the time is unproven.</exception>
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
    // out, and reached ends at the log position the last one handed out ends at. Once they are all handed out (without
    // until), the history must be proven to reach the time: the first later segment starts exactly there, the last one
    // handed out (or the snapshot) was read at the time, or the generation's next snapshot holds exactly that log.
    private async IAsyncEnumerable<Stream> SegmentsAsync(
        string file, string scope, SnapshotKey snapshot, DateTimeOffset at, long? until, SnapshotKey? next, StrongBox<long> reached,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inFlight = new Queue<(SegmentKey Segment, Task<FileStream> Log)>();
        var expected = snapshot.Position;
        var readAt = snapshot.At;
        SegmentKey? boundary = null;
        try
        {
            await foreach (var item in store.ListAsync(BackupLayout.Segments(file, snapshot.Generation),
                BackupLayout.SegmentsBefore(file, snapshot.Generation, snapshot.Position), cancellationToken).ConfigureAwait(false))
            {
                if (BackupLayout.ParseSegment(item.Key) is not { } segment)
                {
                    continue;
                }

                if (segment.At > at || segment.Start >= until)
                {
                    boundary = segment;
                    break;
                }

                readAt = segment.At;
                inFlight.Enqueue((segment, DownloadSegmentAsync(item.Key, scope, stop.Token)));
                if (inFlight.Count == Prefetch)
                {
                    var (frames, after) = await TakeAsync(inFlight, expected).ConfigureAwait(false);
                    expected = reached.Value = after;
                    yield return frames;
                }
            }

            while (inFlight.Count > 0)
            {
                var (frames, after) = await TakeAsync(inFlight, expected).ConfigureAwait(false);
                expected = reached.Value = after;
                yield return frames;
            }

            // Read at the time, the replayed log is the whole state then, whatever follows; a drill checks its end itself.
            if (until is null && boundary?.Start != expected && !PointInTimeRestore.Covers(readAt, at) && next?.Position != expected)
            {
                throw new BackupHistoryIncompleteException(boundary is null
                    ? "the backup of this database cannot prove its history up to the requested time"
                    : "the backup of this database is missing a log segment");
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
