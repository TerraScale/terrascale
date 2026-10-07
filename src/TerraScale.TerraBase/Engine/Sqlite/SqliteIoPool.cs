using System.Diagnostics.Metrics;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Runs SQLite calls on dedicated threads. Microsoft.Data.Sqlite has no true async I/O: its async
/// methods block the calling thread, so request threads hand the calls to this pool instead.
/// </summary>
/// <remarks>
/// <para>
/// Admission rule: an admission is held from <see cref="Admit"/> until the resource it protects is fully
/// released. A transaction holds one until its session is back in the pool or closed; creating or copying
/// a database holds one through file publication and opening the store; a first open holds one through
/// the migration. Shutdown (<see cref="DisposeAsync"/>) refuses new admissions and waits for every held one.
/// </para>
/// ponytail: <c>storage-threads</c> fixed threads shared by every database and at most
/// <c>storage-queue</c> admitted transactions, each with at most one queued call, so the queue is
/// bounded by admission; beyond that callers get <see cref="StorageBusyException"/>. One long call
/// (<c>VACUUM INTO</c> for a branch, a wait on another process's lock) holds a thread for its whole
/// duration. Add per-database fairness, priority lanes or a separate maintenance thread when one hot
/// database or a large copy must not starve the others.
/// </remarks>
internal sealed class SqliteIoPool : IAsyncDisposable
{
    // Lifecycle intent callbacks finish their sequential catalog transactions before returning.
    // They reuse the maintenance admission, so even storage-queue=1 can record an intent.
    private readonly AsyncLocal<AdmissionScope?> _currentAdmission = new();

    internal bool HasAdmission => _currentAdmission.Value is { Active: true };

    internal async ValueTask<T> WithAdmissionAsync<T>(Func<ValueTask<T>> call)
    {
        var previous = _currentAdmission.Value;
        var scope = new AdmissionScope();
        _currentAdmission.Value = scope;
        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            scope.Active = false;
            _currentAdmission.Value = previous;
        }
    }

    private sealed class AdmissionScope
    {
        internal bool Active { get; set; } = true;
    }

    private readonly LinkedList<Action> _work = [];
    // ponytail: one short queue lock across storage threads; per-worker queues if contention is measured.
    // LinkedList lets cancellation unlink a node immediately; BlockingCollection retains cancelled closures.
    private readonly object _queueGate = new();
    private bool _completed;
    private readonly int _maxAdmitted;
    private readonly Counter<long> _rejected;
    private readonly Task[] _threads;

    // Completed once the pool is closed and the last admission is released.
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _admitted;
    private int _running;

    // 1 once shutdown starts. Written with Interlocked.Exchange (a full fence) so the drain and Admit, which
    // increments before it reads this, cannot both miss each other.
    private int _closed;
    private readonly Lazy<Task> _disposal;

    public SqliteIoPool(int threads, int maxAdmitted, Meter meter, Func<ThreadStart, Thread>? createWorker = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threads);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAdmitted);
        _maxAdmitted = maxAdmitted;
        _disposal = new(DrainAsync);
        meter.CreateObservableGauge(
            "terrabase.storage.admitted", () => Volatile.Read(ref _admitted), description: "Storage transactions and maintenance calls admitted.");
        meter.CreateObservableGauge(
            "terrabase.storage.running", () => Volatile.Read(ref _running), description: "SQLite calls running on the I/O threads.");
        _rejected = meter.CreateCounter<long>("terrabase.storage.rejected", description: "Storage work rejected as busy (HTTP 503).");
        // Allocate bookkeeping before any launch: even a later allocation failure can join every started worker.
        _threads = new Task[threads];
        var workers = new Thread[threads];
        var started = 0;
        try
        {
            for (var i = 0; i < threads; i++)
            {
                var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Run()
                {
                    try
                    {
                        Work();
                    }
                    finally
                    {
                        exited.SetResult();
                    }
                }
                var worker = createWorker is null ? new Thread(Run) : createWorker(Run);
                worker.IsBackground = true;
                worker.Name = "terrabase-sqlite";
                _threads[i] = exited.Task;
                workers[i] = worker;
                worker.Start();
                started++;
            }
        }
        catch
        {
            lock (_queueGate)
            {
                _completed = true;
                Monitor.PulseAll(_queueGate);
            }

            // Construction runs before requests and cannot await disposal of an object that never returned.
            // Join actual threads, not completion signals: ownership cannot be released before worker exit.
            for (var i = 0; i < started; i++)
            {
                workers[i].Join();
            }

            throw;
        }
    }

    /// <summary>Admits one transaction or engine operation (create, copy, first open); <see cref="Release"/> it when it ends.</summary>
    /// <exception cref="StorageBusyException">The pool already admitted <c>storage-queue</c> transactions.</exception>
    /// <exception cref="ObjectDisposedException">The engine is shutting down.</exception>
    public void Admit()
    {
        var admitted = Interlocked.Increment(ref _admitted);
        if (Volatile.Read(ref _closed) != 0)
        {
            Release();
            throw new ObjectDisposedException(nameof(SqliteIoPool));
        }

        if (admitted > _maxAdmitted)
        {
            Release();
            throw Busy();
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _admitted) == 0 && Volatile.Read(ref _closed) != 0)
        {
            _drained.TrySetResult();
        }
    }

    /// <summary>Counts a refusal and returns the exception that answers it with 503.</summary>
    public StorageBusyException Busy()
    {
        _rejected.Add(1);
        return new StorageBusyException();
    }

    /// <summary>Admitted transactions and maintenance calls, for tests.</summary>
    internal int Admitted => Volatile.Read(ref _admitted);

    /// <summary>Calls running on the threads, for tests.</summary>
    internal int Running => Volatile.Read(ref _running);

    /// <summary>New admissions are refused once shutdown starts.</summary>
    internal bool Closed => Volatile.Read(ref _closed) != 0;

    /// <summary>Calls waiting for a worker, for tests.</summary>
    internal int Queued
    {
        get
        {
            lock (_queueGate)
            {
                return _work.Count;
            }
        }
    }

    /// <summary>Runs <paramref name="call"/> on a pool thread; the caller must hold an admission.</summary>
    public async Task<T> RunAsync<T>(Func<T> call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Continuations must not run on (and so block) a pool thread.
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Queued (0), started (1) or cancelled (2), decided once: a cancelled call never starts, and a started
        // one is awaited to the end, because the caller still owns its connection while it runs.
        var state = 0;
        var node = new LinkedListNode<Action>(Run);
        using var cancellation = cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                lock (_queueGate)
                {
                    if (node.List is not null)
                    {
                        _work.Remove(node);
                    }
                }

                result.TrySetCanceled(cancellationToken);
            }
        });
        lock (_queueGate)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            if (Volatile.Read(ref state) == 0)
            {
                _work.AddLast(node);
                Monitor.Pulse(_queueGate);
            }
        }

        return await result.Task.ConfigureAwait(false);

        void Run()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            {
                // Cancelled while queued: the caller already moved on.
                return;
            }

            Interlocked.Increment(ref _running);
            try
            {
                result.TrySetResult(call());
            }
            catch (Exception error)
            {
                result.TrySetException(error);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    public Task RunAsync(Action call, CancellationToken cancellationToken) => RunAsync(() =>
    {
        call();
        return true;
    }, cancellationToken);

    /// <summary>
    /// Admits nothing more, lets admitted transactions finish (their calls still run), then stops the threads.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        return new(_disposal.Value);
    }

    /// <summary>Refuses new admissions; admitted operations can keep submitting work until fully released.</summary>
    internal Task DrainAdmissionsAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        if (Volatile.Read(ref _admitted) == 0)
        {
            _drained.TrySetResult();
        }

        return _drained.Task;
    }

    private async Task DrainAsync()
    {
        // No deadline: maintenance can need several SQLite calls even after a long VACUUM finishes.
        await DrainAdmissionsAsync().ConfigureAwait(false);

        lock (_queueGate)
        {
            _completed = true;
            Monitor.PulseAll(_queueGate);
        }

        await Task.WhenAll(_threads).ConfigureAwait(false);
    }

    private void Work()
    {
        while (true)
        {
            Action call;
            lock (_queueGate)
            {
                while (_work.Count == 0 && !_completed)
                {
                    Monitor.Wait(_queueGate);
                }

                if (_work.First is not { } node)
                {
                    return;
                }

                call = node.Value;
                _work.RemoveFirst();
            }

            call();
        }
    }
}
