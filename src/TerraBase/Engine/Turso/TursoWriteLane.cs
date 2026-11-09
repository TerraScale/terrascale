namespace TerraBase.Engine.Turso;

internal sealed class TursoWriteLane(string database, TursoIoPool io) : TursoRunner
{
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);

    private readonly LinkedList<Action> _work = [];
    private readonly object _gate = new();
    private bool _started;
    private int _executing;

    private int _pending;

    [ThreadStatic]
    private static TursoWriteLane? t_lane;

    private static readonly AsyncLocal<TursoWriteLane?> ResidentFlow = new();

    internal void Resident() => ResidentFlow.Value = this;

    internal int Queued
    {
        get
        {
            lock (_gate)
            {
                return _work.Count;
            }
        }
    }

    internal bool Running => Volatile.Read(ref _executing) > 0;

    public override Task<T> RunAsync<T>(Func<T> call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resident = ResidentFlow.Value == this;
        if (resident && t_lane == this)
        {
            Interlocked.Increment(ref _executing);
            try
            {
                return Task.FromResult(call());
            }
            catch (Exception error)
            {
                return Task.FromException<T>(error);
            }
            finally
            {
                Interlocked.Decrement(ref _executing);
            }
        }

        return QueueAsync(call, resident, cancellationToken);
    }

    private async Task<T> QueueAsync<T>(Func<T> call, bool resident, CancellationToken cancellationToken)
    {
        using var diagnostic = Diagnostics?.Submit();
        var result = new TaskCompletionSource<T>(resident ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);

        var context = io.CaptureContext();
        var state = 0;
        var node = new LinkedListNode<Action>(Run);
        using var cancellation = cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                lock (_gate)
                {
                    if (node.List is not null)
                    {
                        _work.Remove(node);
                        _pending = _work.Count;
                    }
                }

                diagnostic?.Cancel();
                if (resident)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(_ => result.TrySetCanceled(cancellationToken), null);
                }
                else
                {
                    result.TrySetCanceled(cancellationToken);
                }
            }
        });
        lock (_gate)
        {
            if (Volatile.Read(ref state) == 0)
            {
                _work.AddLast(node);
                Volatile.Write(ref _pending, _work.Count);
                if (_started)
                {
                    Monitor.Pulse(_gate);
                }
                else
                {
                    _started = true;
                    using (ExecutionContext.SuppressFlow())
                    {
                        new Thread(Work) { IsBackground = true, Name = "terrabase-turso-writer" }.Start();
                    }
                }
            }
        }

        return await result.Task.ConfigureAwait(false);

        void Run()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            {
                return;
            }

            Interlocked.Increment(ref _executing);
            diagnostic?.Begin();
            T? value = default;
            Exception? failure = null;
            try
            {
                using var scope = io.RestoreContext(context);
                value = call();
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                diagnostic?.Complete();
                Interlocked.Decrement(ref _executing);
            }

            if (failure is not null)
            {
                result.TrySetException(failure);
            }
            else
            {
                result.TrySetResult(value!);
            }
        }
    }

    public override string ToString() => $"write lane of {database}";

    private void Work()
    {
        t_lane = this;
        while (true)
        {
            var spin = new SpinWait();
            while (Volatile.Read(ref _pending) == 0 && !spin.NextSpinWillYield)
            {
                spin.SpinOnce();
            }

            Action next;
            lock (_gate)
            {
                while (_work.Count == 0)
                {
                    if (!Monitor.Wait(_gate, IdleTimeout) && _work.Count == 0)
                    {
                        _started = false;
                        return;
                    }
                }

                next = _work.First!.Value;
                _work.RemoveFirst();
                _pending = _work.Count;
            }

            next();
        }
    }
}
