using System.Globalization;
using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;

namespace TerraBase.Slow.AckAfterSegment;

/// <summary>How a wait for an acknowledgement ended.</summary>
internal enum Ack
{
    /// <summary>The change-log segment holding the write is stored in the object store.</summary>
    Durable,

    /// <summary>
    /// The deadline passed first. The write committed locally and may still be stored later: the caller cannot tell, as with
    /// a timed-out request.
    /// </summary>
    Uncertain,
}

/// <summary>
/// Measurement prototype, not production: a write is acknowledged only once the change-log segment holding it is stored in
/// the object store ("remote segment before ACK"). One generation per gate: its snapshot is stored before anything is
/// acknowledged, then segments <c>{prefix}/segment-{start}-{end}</c> (16-digit byte positions of the generation's log),
/// contiguous by construction. Group commit: one flusher reads every change committed so far through the database's
/// backup tap, stores it as one segment, retrying the same key and bytes until it is stored, then releases every write
/// that committed before the read began. It owns the database's only tap: the production shipper must not run on it.
/// </summary>
internal sealed class SegmentAckGate : IAsyncDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(1);
    private readonly IWalTap _tap;
    private readonly IObjectStore _store;
    private readonly string _prefix;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Lock _gate = new();
    private List<TaskCompletionSource> _waiting = [];
    private Task _flusher = Task.CompletedTask;
    private long _position;
    private long _segments;
    private long _released;
    private long _retries;

    private SegmentAckGate(IWalTap tap, IObjectStore store, string prefix)
    {
        (_tap, _store, _prefix) = (tap, store, prefix);
    }

    /// <summary>Segments stored.</summary>
    public long Segments => Interlocked.Read(ref _segments);

    /// <summary>Waits released by stored segments (a write's wait counts once).</summary>
    public long Released => Interlocked.Read(ref _released);

    /// <summary>Failed reads or attempts to store a segment or snapshot that the gate retried (after the client's own retries).</summary>
    public long Retries => Interlocked.Read(ref _retries);

    /// <summary>Bytes of the generation's log stored as segments.</summary>
    public long Position => Interlocked.Read(ref _position);

    /// <summary>Size of the generation's snapshot.</summary>
    public long SnapshotBytes { get; private set; }

    internal IWalTap Tap => _tap;

    /// <summary>From the generation's start to its snapshot being stored: nothing can be acknowledged meanwhile.</summary>
    public TimeSpan SnapshotStall { get; private set; }

    /// <summary>Starts a generation of <paramref name="database"/>: a snapshot, stored before the gate returns.</summary>
    public static async Task<SegmentAckGate> StartAsync(
        IWalBackup backup, TenantContext database, IObjectStore store, string prefix, string scratch, TimeProvider time, CancellationToken cancellationToken)
    {
        var tap = await backup.OpenTapAsync(database, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"no database {database.DatabaseId}");
        var gate = new SegmentAckGate(tap, store, prefix);
        try
        {
            var started = time.GetTimestamp();
            var path = Path.Combine(scratch, $"snapshot-{Guid.NewGuid():N}.db");
            // Changes committed before the pin are in the copy itself: the generation's log starts after it, at 0.
            _ = await tap.SnapshotAsync(Stream.Null, path, cancellationToken).ConfigureAwait(false);
            await using (var file = File.OpenRead(path))
            {
                gate.SnapshotBytes = file.Length;
                await gate.StoreAsync($"{prefix}/snapshot", file, cancellationToken).ConfigureAwait(false);
            }

            File.Delete(path);
            gate.SnapshotStall = time.GetElapsedTime(started);
            gate._flusher = Task.Run(gate.FlushAsync, CancellationToken.None);
            return gate;
        }
        catch
        {
            await gate.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Waits until every change committed before this call is stored. Call it after the write's local commit returned:
    /// the write is then in the next segment the flusher reads, or in one it already read.
    /// </summary>
    public async Task<Ack> AckAsync(TimeSpan deadline, CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiting.Add(waiter);
        }

        _ = _signal.Release();
        try
        {
            await waiter.Task.WaitAsync(deadline, cancellationToken).ConfigureAwait(false);
            return Ack.Durable;
        }
        catch (TimeoutException)
        {
            return Ack.Uncertain;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _flusher.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _tap.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
        _signal.Dispose();
    }

    private async Task FlushAsync()
    {
        var stop = _stop.Token;
        while (true)
        {
            await _signal.WaitAsync(stop).ConfigureAwait(false);
            // Wake-ups already counted are in the batch taken below; one added after it wakes the next pass.
            while (_signal.Wait(0))
            {
            }

            List<TaskCompletionSource> batch;
            lock (_gate)
            {
                (batch, _waiting) = (_waiting, []);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            using var segment = new MemoryStream();
            WalRead read;
            while (true)
            {
                try
                {
                    read = await _tap.ReadAsync(segment, stop).ConfigureAwait(false);
                    break;
                }
                catch (StorageBusyException) when (!stop.IsCancellationRequested)
                {
                    // A failed tap read does not move its cursor; discard its partial bytes before reading them again.
                    segment.SetLength(0);
                    segment.Position = 0;
                    _ = Interlocked.Increment(ref _retries);
                    await Task.Delay(TimeSpan.FromMilliseconds(50), stop).ConfigureAwait(false);
                }
            }

            if (!read.Continuous)
            {
                // A hole ends the generation: the prototype stops rather than start a new one.
                var hole = new InvalidOperationException("the change log has a hole: the generation ended");
                foreach (var waiter in batch)
                {
                    _ = waiter.TrySetException(hole);
                }

                throw hole;
            }

            if (read.Bytes > 0)
            {
                var start = Interlocked.Read(ref _position);
                segment.Position = 0;
                await StoreAsync(SegmentKey(_prefix, start, start + read.Bytes), segment, stop).ConfigureAwait(false);
                _ = Interlocked.Add(ref _position, read.Bytes);
                _ = Interlocked.Increment(ref _segments);
            }

            foreach (var waiter in batch)
            {
                _ = waiter.TrySetResult();
            }

            _ = Interlocked.Add(ref _released, batch.Count);
        }
    }

    // The same key and bytes until stored: a lost response or a retry after an outage stores the same object again.
    private async Task StoreAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                content.Position = 0;
                await _store.PutAsync(key, content, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                _ = Interlocked.Increment(ref _retries);
                var backoff = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, 50 * Math.Pow(2, Math.Min(attempt, 10))));
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static string SegmentKey(string prefix, long start, long end) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}/segment-{start:D16}-{end:D16}");
}
