using System.Runtime.CompilerServices;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Primitives;

namespace TerraBase.Features.Backup;

internal sealed class BackupReader(IWalBackup engine, IObjectStore store, DataKeys keys, TerraBaseOptions options, IDiskBudget disk)
{
    private const int Prefetch = 8;
    private const int MaxSizingSegments = 131_072;
    private const long MaxSizingKeyBytes = 32L << 20;
    private readonly string _scratch = ContinuousBackup.Scratch(options);

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

    public Task<RestoredBackup> BuildAsync(
        string file, string scope, SnapshotKey snapshot, DateTimeOffset at, long? until, CancellationToken cancellationToken) =>
        BuildAsync(file, scope, snapshot, at, until, next: null, cancellationToken);

    public async Task<RestoredBackup> BuildAsync(
        string file, string scope, SnapshotKey snapshot, DateTimeOffset at, long? until, SnapshotKey? next, CancellationToken cancellationToken)
    {
        if (until is null && file.StartsWith(BackupLayout.FileRoot(null) + "/", StringComparison.Ordinal) && !PointInTimeRestore.Covers(snapshot.At, at))
        {
            throw new BackupHistoryIncompleteException("the system database's backup proves only its snapshot times");
        }

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

    public async Task<DateTimeOffset?> ReadMarkAsync(string key, string scope, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_scratch);
        await using var frames = await DownloadSegmentAsync(key, scope, cancellationToken).ConfigureAwait(false);
        return engine.ReadMark(frames);
    }

    public static void Delete(string path)
    {
        foreach (var leftover in new[] { "", "-journal", "-wal", "-shm", ".key" })
        {
            File.Delete(path + leftover);
        }
    }

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
                found = await store.ReadAsync(key, (content, readToken) =>
                    BackupObject.ReadAsync(content, output, key, id => keys.GetAsync(scope, id, readToken), readToken), cancellationToken).ConfigureAwait(false);
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
}

internal sealed class BackupHistoryIncompleteException(string message) : Exception(message);

internal sealed class RestorePointUnprovenException(string message) : Exception(message);

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

internal sealed class BackupInventoryLimitException() : IOException(
    "Backup sizing exceeds 131072 segments or 32 MiB of object names. Restore from a newer snapshot or contact support for backup recovery.");
