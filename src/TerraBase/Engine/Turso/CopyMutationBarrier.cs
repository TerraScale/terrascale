namespace TerraBase.Engine.Turso;

internal sealed class CopyMutationBarrier : IDisposable
{
    internal static readonly TimeSpan CheckpointAllowance = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _thread;

    internal IDisposable Enter()
    {
        if (Volatile.Read(ref _thread) == Environment.CurrentManagedThreadId)
        {
            return Nested.Instance;
        }
        _gate.Wait();
        Volatile.Write(ref _thread, Environment.CurrentManagedThreadId);
        return new Lease(this);
    }

    internal IDisposable? TryEnter() => _gate.Wait(0) ? new Lease(this) : null;

    internal async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(this);
    }

    internal async Task<IDisposable> PublishFaultAsync(TimeSpan queryBudget, Action late)
    {
        if (!await _gate.WaitAsync(queryBudget + CheckpointAllowance).ConfigureAwait(false))
        {
            late();
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        return new Lease(this);
    }

    public void Dispose()
    {
        if (_gate.Wait(0))
        {
            _gate.Dispose();
        }
    }

    private sealed class Nested : IDisposable
    {
        internal static readonly Nested Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Lease(CopyMutationBarrier barrier) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                Volatile.Write(ref barrier._thread, 0);
                barrier._gate.Release();
            }
        }
    }
}
