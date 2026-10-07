namespace TerraBase.Engine.Sqlite;

/// <summary>
/// One database's write thread: its write transactions run their SQLite calls here, apart from the shared read pool,
/// so a long read (a <c>runSql</c>, a branch copy) never holds up the database's writes. The thread starts with the
/// first call and exits after it has been idle for <see cref="IdleTimeout"/>, so only databases being written hold one.
/// </summary>
internal sealed class SqliteWriteLane(string database) : SqliteRunner
{
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);

    // A linked list lets cancellation unlink a queued call at once, as SqliteIoPool does.
    private readonly LinkedList<Action> _work = [];
    private readonly object _gate = new();
    private bool _started;
    private int _executing;

    // Calls queued, readable without the lock while the thread spins.
    private int _pending;

    /// <summary>Calls waiting for the thread, for tests.</summary>
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

    /// <summary>Whether a call is running, for tests.</summary>
    internal bool Running => Volatile.Read(ref _executing) > 0;

    public override async Task<T> RunAsync<T>(Func<T> call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 0 queued, 1 running, 2 cancelled before it ran.
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

                result.TrySetCanceled(cancellationToken);
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
                    new Thread(Work) { IsBackground = true, Name = "terrabase-sqlite-writer" }.Start();
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
            T? value = default;
            Exception? failure = null;
            try
            {
                value = call();
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                Interlocked.Decrement(ref _executing);
            }

            // An awaiting caller can release the writer immediately; publish only after this call is marked finished.
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
        while (true)
        {
            // A transaction's next call usually arrives within microseconds of the previous one returning: spin
            // briefly before sleeping, so back-to-back calls do not pay a thread wake-up each.
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
