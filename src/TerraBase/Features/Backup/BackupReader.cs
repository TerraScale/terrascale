using System.Buffers.Binary;
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
    private const int FrameHeaderBytes = 24;

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
    /// read by <paramref name="at"/> and, when <paramref name="until"/> is set, starting before that frame position. Returns
    /// the file, which the caller deletes with <see cref="Delete"/>, and the frame position it reached.
    /// </summary>
    /// <exception cref="InvalidDataException">The backup is incomplete or fails authentication or its integrity check.</exception>
    public async Task<(string Path, long Position)> BuildAsync(
        string file, string scope, SnapshotKey snapshot, DateTimeOffset at, long? until, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_scratch);
        var restored = Path.Combine(_scratch, EntityIds.New().ToString() + ".db");
        try
        {
            await using (var copy = new FileStream(restored, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                await DownloadAsync(snapshot.Key, scope, copy, cancellationToken).ConfigureAwait(false);
            }

            var frameBytes = FrameHeaderBytes + await PageSizeAsync(restored, cancellationToken).ConfigureAwait(false);
            var reached = new StrongBox<long>(snapshot.Position);
            var segments = SegmentsAsync(file, scope, snapshot, at, until, frameBytes, reached, cancellationToken);
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

    /// <summary>Deletes a file <see cref="BuildAsync"/> returned, with whatever SQLite left next to it.</summary>
    public static void Delete(string path)
    {
        foreach (var leftover in new[] { "", "-journal", "-wal", "-shm" })
        {
            File.Delete(path + leftover);
        }
    }

    // The segments of the snapshot's generation from its position, up to the time (and before until), in order, each as a
    // scratch file the caller disposes. Up to Prefetch download at once; contiguity is checked in order as they are handed
    // out, and reached ends at the frame position the last one handed out ends at.
    private async IAsyncEnumerable<Stream> SegmentsAsync(
        string file, string scope, SnapshotKey snapshot, DateTimeOffset at, long? until, int frameBytes, StrongBox<long> reached,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inFlight = new Queue<(SegmentKey Segment, Task<FileStream> Frames)>();
        var expected = snapshot.Position;
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
                    break;
                }

                inFlight.Enqueue((segment, DownloadSegmentAsync(item.Key, scope, frameBytes, stop.Token)));
                if (inFlight.Count == Prefetch)
                {
                    var (frames, after) = await TakeAsync(inFlight, expected, frameBytes).ConfigureAwait(false);
                    expected = reached.Value = after;
                    yield return frames;
                }
            }

            while (inFlight.Count > 0)
            {
                var (frames, after) = await TakeAsync(inFlight, expected, frameBytes).ConfigureAwait(false);
                expected = reached.Value = after;
                yield return frames;
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

    private static async Task<(Stream Frames, long Expected)> TakeAsync(Queue<(SegmentKey Segment, Task<FileStream> Frames)> inFlight, long expected, int frameBytes)
    {
        var (segment, download) = inFlight.Dequeue();
        var frames = await download.ConfigureAwait(false);
        if (segment.Start != expected)
        {
            await frames.DisposeAsync().ConfigureAwait(false);
            throw new InvalidDataException("the backup of this database is missing a log segment");
        }

        return (frames, expected + frames.Length / frameBytes);
    }

    private async Task<FileStream> DownloadSegmentAsync(string key, string scope, int frameBytes, CancellationToken cancellationToken)
    {
        var frames = new FileStream(Path.Combine(_scratch, EntityIds.New().ToString() + ".frames"), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 1 << 16, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await DownloadAsync(key, scope, frames, cancellationToken).ConfigureAwait(false);
            if (frames.Length % frameBytes != 0)
            {
                throw new InvalidDataException("a log segment of this database holds a partial frame");
            }

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
                throw new InvalidDataException("a backup object of this database disappeared during the restore");
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

    // The page size at offset 16 of the database header; 1 means 65536.
    private static async Task<int> PageSizeAsync(string path, CancellationToken cancellationToken)
    {
        using var handle = File.OpenHandle(path);
        var bytes = new byte[2];
        if (await RandomAccess.ReadAsync(handle, bytes, 16, cancellationToken).ConfigureAwait(false) != 2)
        {
            throw new InvalidDataException("the snapshot is not a database");
        }

        var size = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        return size == 1 ? 65536 : size;
    }
}
