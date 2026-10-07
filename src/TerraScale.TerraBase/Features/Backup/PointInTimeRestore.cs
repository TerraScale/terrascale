using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

/// <summary>The times a database can be restored to.</summary>
/// <param name="Earliest">Its oldest snapshot.</param>
/// <param name="Latest">The newest data in the bucket.</param>
public sealed record RestoreWindow(DateTimeOffset Earliest, DateTimeOffset Latest);

/// <summary>Point-in-time restore from continuous backup (TS-633).</summary>
public interface IPointInTimeRestore
{
    /// <summary>The restore window of <paramref name="database"/>; <see langword="null"/> when no backup of it exists.</summary>
    Task<RestoreWindow?> GetWindowAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// Creates <paramref name="target"/>, a new engine database, holding <paramref name="source"/> as of <paramref name="at"/>:
    /// every transaction the backup read by then. <see langword="false"/> when no backup reaches back to <paramref name="at"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The backup is incomplete or fails authentication or its integrity check.</exception>
    Task<bool> RestoreAsync(TenantContext source, DateTimeOffset at, TenantContext target, CancellationToken cancellationToken);
}

/// <summary>Restores from the newest snapshot at or before the time, rolled forward through its generation's segments.</summary>
/// <remarks>ponytail: segments download one at a time; prefetch a few in parallel if measured restore times call for it.</remarks>
internal sealed class PointInTimeRestore(IWalBackup engine, IObjectStore store, DataKeys keys, IContinuousBackup shipper, TerraBaseOptions options)
    : IPointInTimeRestore
{
    private const int FrameHeaderBytes = 24;

    // Segments downloading at once during a restore.
    private const int Prefetch = 8;
    private readonly string _scratch = ContinuousBackup.Scratch(options);

    public async Task<RestoreWindow?> GetWindowAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var file = BackupLayout.File(database);
        SnapshotKey? first = null;
        SnapshotKey? last = null;
        await foreach (var key in store.ListAsync(BackupLayout.Snapshots(file), null, cancellationToken).ConfigureAwait(false))
        {
            if (BackupLayout.ParseSnapshot(key) is { } snapshot)
            {
                first ??= snapshot;
                last = snapshot;
            }
        }

        if (first is null || last is null)
        {
            return null;
        }

        // The shipper knows its own progress; otherwise the newest segment after the newest snapshot tells.
        var latest = shipper.LastShipped(database) ?? last.At;
        if (shipper.LastShipped(database) is null)
        {
            await foreach (var key in store.ListAsync(BackupLayout.Segments(file, last.Generation),
                BackupLayout.SegmentsBefore(file, last.Generation, last.Position), cancellationToken).ConfigureAwait(false))
            {
                if (BackupLayout.ParseSegment(key) is { } segment && segment.At > latest)
                {
                    latest = segment.At;
                }
            }
        }

        return new(first.At, latest > last.At ? latest : last.At);
    }

    public async Task<bool> RestoreAsync(TenantContext source, DateTimeOffset at, TenantContext target, CancellationToken cancellationToken)
    {
        var file = BackupLayout.File(source);
        SnapshotKey? chosen = null;
        await foreach (var key in store.ListAsync(BackupLayout.Snapshots(file), null, cancellationToken).ConfigureAwait(false))
        {
            if (BackupLayout.ParseSnapshot(key) is { } snapshot)
            {
                if (snapshot.At > at)
                {
                    break;
                }

                chosen = snapshot;
            }
        }

        if (chosen is null)
        {
            return false;
        }

        Directory.CreateDirectory(_scratch);
        var restored = Path.Combine(_scratch, Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var scope = BackupLayout.KeyScope(source);
            await using (var snapshot = new FileStream(restored, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                await DownloadAsync(chosen.Key, scope, snapshot, cancellationToken).ConfigureAwait(false);
            }

            var frameBytes = FrameHeaderBytes + await PageSizeAsync(restored, cancellationToken).ConfigureAwait(false);
            if (!await engine.RollForwardAsync(restored, SegmentsAsync(file, scope, chosen, at, frameBytes, cancellationToken), cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("the restored database fails its integrity check");
            }

            await engine.ImportDatabaseAsync(target, restored, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            foreach (var leftover in new[] { "", "-journal", "-wal", "-shm" })
            {
                File.Delete(restored + leftover);
            }
        }
    }

    // The segments of the snapshot's generation from its position, up to the time, in order, each as a scratch file the
    // caller disposes. Up to Prefetch download at once; contiguity is checked in order as they are handed out.
    private async IAsyncEnumerable<Stream> SegmentsAsync(
        string file, string scope, SnapshotKey snapshot, DateTimeOffset at, int frameBytes, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inFlight = new Queue<(SegmentKey Segment, Task<FileStream> Frames)>();
        var expected = snapshot.Position;
        try
        {
            await foreach (var key in store.ListAsync(BackupLayout.Segments(file, snapshot.Generation),
                BackupLayout.SegmentsBefore(file, snapshot.Generation, snapshot.Position), cancellationToken).ConfigureAwait(false))
            {
                if (BackupLayout.ParseSegment(key) is not { } segment)
                {
                    continue;
                }

                if (segment.At > at)
                {
                    break;
                }

                inFlight.Enqueue((segment, DownloadSegmentAsync(key, scope, frameBytes, stop.Token)));
                if (inFlight.Count == Prefetch)
                {
                    var (frames, after) = await TakeAsync(inFlight, expected, frameBytes).ConfigureAwait(false);
                    expected = after;
                    yield return frames;
                }
            }

            while (inFlight.Count > 0)
            {
                var (frames, after) = await TakeAsync(inFlight, expected, frameBytes).ConfigureAwait(false);
                expected = after;
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
        var frames = new FileStream(Path.Combine(_scratch, Guid.NewGuid().ToString("N") + ".frames"), FileMode.CreateNew, FileAccess.ReadWrite,
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
                found = await store.ReadAsync(key, content => BackupObject.ReadAsync(content, output, key, id => keys.GetAsync(scope, id, cancellationToken), cancellationToken),
                    cancellationToken).ConfigureAwait(false);
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
