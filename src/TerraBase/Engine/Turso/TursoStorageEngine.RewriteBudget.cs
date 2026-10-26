using TerraBase.Features.Memory;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    // The 54 MB concurrent-copy baseline completes in 1.1 s. Leave substantial disk/load margin, but never pin
    // WAL/CDC, retention and the shape frontier indefinitely (0013f).
    internal const long RewriteBytesPerSecond = 4L * 1024 * 1024;
    internal static readonly TimeSpan RewriteDeadlineFloor = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan RewriteDeadlineCap = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan RewritePressureSample = TimeSpan.FromMilliseconds(100);
    private const int RewritePressureSamples = 3;
    private const long RewriteWorkingBytes = 2 * TursoCopy.ChunkBytes + 8L * 1024 * 1024;

    internal static TimeSpan RewriteDeadline(long bytes) =>
        TimeSpan.FromSeconds(Math.Min(RewriteDeadlineCap.TotalSeconds,
            RewriteDeadlineFloor.TotalSeconds + (double)bytes / RewriteBytesPerSecond));

    private sealed class RewriteBudget : IDisposable
    {
        private readonly TursoStorageEngine _engine;
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationTokenSource _pressure = new();
        private readonly CancellationTokenSource _linked;
        private readonly ITimer _watch;
        private readonly IMemoryReservation? _memory;
        private int _samples;
        private readonly Lock _gate = new();
        private bool _stopped;

        internal RewriteBudget(TursoStorageEngine engine, long bytes, CancellationToken token)
        {
            _engine = engine;
            _memory = engine._schemaMemory?.TryReserve(MemoryLane.Bulk, RewriteWorkingBytes, null);
            if (engine._schemaMemory is not null && _memory is null)
            {
                _pressure.Cancel();
            }
            // These protect real WAL/CDC pin time and storage pressure, independently of the query/domain clock.
            _deadline = new CancellationTokenSource(RewriteDeadline(bytes), TimeProvider.System);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(token, _deadline.Token, _pressure.Token);
            _watch = TimeProvider.System.CreateTimer(_ => Sample(), null, RewritePressureSample, RewritePressureSample);
        }

        internal CancellationToken Token => _linked.Token;
        internal RewriteRefusal Reason => _pressure.IsCancellationRequested ? RewriteRefusal.Pressure : RewriteRefusal.Deadline;
        internal bool Expired => _deadline.IsCancellationRequested || _pressure.IsCancellationRequested;

        private void Sample()
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }
                var io = _engine.Io;
                var pressured = io.Queued >= Math.Max(2, io.WorkerCount)
                    || (long)(io.Admitted - 1) * 10 >= (long)io.AdmissionCapacity * 9;
                using var probe = _engine._schemaMemory?.TryReserve(MemoryLane.Bulk, 4096, null);
                pressured |= _engine._schemaMemory is not null && probe is null;
                if (pressured)
                {
                    if (Interlocked.Increment(ref _samples) >= RewritePressureSamples)
                    {
                        _pressure.Cancel();
                    }
                }
                else
                {
                    Interlocked.Exchange(ref _samples, 0);
                }
            }
        }

        // Stop watching once the writer is taken: the final hold has its own, shorter deadline.
        internal void StopPressure()
        {
            lock (_gate)
            {
                _stopped = true;
                _watch.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            StopPressure();
            _watch.Dispose();
            _linked.Dispose();
            _deadline.Dispose();
            _pressure.Dispose();
            _memory?.Dispose();
        }
    }
}
