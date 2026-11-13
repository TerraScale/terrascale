namespace TerraBase.Engine.Turso;

internal sealed class CopyMutationBarrier(TimeProvider time) : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _thread;

    internal IDisposable Enter()
    {
        if (Volatile.Read(ref _thread) == Environment.CurrentManagedThreadId)
        {
            return new Nested();
        }
        if (!_gate.Wait(Deadline))
        {
            Environment.FailFast("A copy mutation did not release its barrier within 30 seconds; files and intents are retained for recovery.");
        }
        Volatile.Write(ref _thread, Environment.CurrentManagedThreadId);
        return new Lease(this, time);
    }

    internal IDisposable TryPublish()
    {
        if (!_gate.Wait(0))
        {
            throw new StorageBusyException();
        }
        return new Lease(this, time);
    }

    internal async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(Deadline, cancellationToken).ConfigureAwait(false))
        {
            Environment.FailFast("A copy mutation did not release its barrier within 30 seconds; files and intents are retained for recovery.");
        }
        return new Lease(this, time);
    }

    public void Dispose() => _gate.Dispose();

    private sealed class Nested : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class Lease : IDisposable
    {
        private readonly CopyMutationBarrier _barrier;
        private readonly ITimer _deadline;
        private int _ended;

        internal Lease(CopyMutationBarrier barrier, TimeProvider time)
        {
            _barrier = barrier;
            _deadline = time.CreateTimer(_ => Expired(), null, Deadline, Timeout.InfiniteTimeSpan);
        }

        private void Expired()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                Environment.FailFast("A copy mutation exceeded 30 seconds; files and intents are retained for recovery.");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                _deadline.Dispose();
                Volatile.Write(ref _barrier._thread, 0);
                _barrier._gate.Release();
            }
        }
    }
}
