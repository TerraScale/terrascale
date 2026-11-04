namespace TerraBase.Engine.Turso;

internal sealed class ReplicaGate : IDisposable
{
    private readonly SemaphoreSlim _entry = new(1, 1);
    private readonly Lock _lock = new();
    private int _readers;
    private TaskCompletionSource? _drained;

    public async ValueTask<IDisposable> ReadAsync(CancellationToken cancellationToken)
    {
        await _entry.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _readers++;
        }

        _ = _entry.Release();
        return new Lease(this, exclusive: false);
    }

    public async ValueTask<IDisposable> ExclusiveAsync(CancellationToken cancellationToken)
    {
        await _entry.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task drained;
            lock (_lock)
            {
                if (_readers == 0)
                {
                    return new Lease(this, exclusive: true);
                }

                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drained = _drained.Task;
            }

            await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, exclusive: true);
        }
        catch
        {
            lock (_lock)
            {
                _drained = null;
            }

            _ = _entry.Release();
            throw;
        }
    }

    public void Dispose() => _entry.Dispose();

    private void Leave(bool exclusive)
    {
        if (exclusive)
        {
            _ = _entry.Release();
            return;
        }

        lock (_lock)
        {
            if (--_readers == 0 && _drained is { } drained)
            {
                _drained = null;
                drained.TrySetResult();
            }
        }
    }

    private sealed class Lease(ReplicaGate gate, bool exclusive) : IDisposable
    {
        private int _left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
            {
                gate.Leave(exclusive);
            }
        }
    }
}
