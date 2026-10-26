using System.Diagnostics;
using System.Diagnostics.Metrics;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

/// <summary>
/// Runs Turso calls on dedicated threads. The provider has no true async I/O: its async
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
/// duration. Scheduling weights apply to calls, not elapsed time: long calls are not preempted. With at least two
/// workers, Hobby can occupy at most all but one, and so can bulk work (<see cref="StorageWork.Bulk"/>); a one-worker pool cannot
/// isolate running calls.
/// </remarks>
internal sealed class TursoIoPool : TursoRunner, IAsyncDisposable
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

    internal sealed class AdmissionScope
    {
        internal bool Active { get; set; } = true;
    }

    internal readonly record struct CallContext(QosContext.Snapshot Qos, AdmissionScope? Admission);

    internal CallContext CaptureContext() => new(QosContext.Capture(), _currentAdmission.Value);

    internal IDisposable RestoreContext(CallContext context)
    {
        var previous = _currentAdmission.Value;
        var qos = QosContext.Restore(context.Qos);
        _currentAdmission.Value = context.Admission;
        return new ContextScope(this, previous, qos);
    }

    private sealed class ContextScope(TursoIoPool pool, AdmissionScope? previous, IDisposable qos) : IDisposable
    {
        public void Dispose()
        {
            pool._currentAdmission.Value = previous;
            qos.Dispose();
        }
    }

    private readonly LinkedList<WorkItem> _work = [];
    private long _virtualTime;
    private readonly int _maxHobbyRunning;
    private int _runningHobby;

    // Bulk calls running (StorageWork.Bulk); capped as Hobby's are.
    private int _runningBulk;

    private sealed record WorkItem(string? Organization, long Finish, bool Hobby, bool Bulk, Action Run);

    // ponytail: one short queue lock across storage threads; per-worker queues if contention is measured.
    // LinkedList lets cancellation unlink a node immediately; BlockingCollection retains cancelled closures.
    private readonly object _queueGate = new();
    private bool _completed;
    private readonly int _maxAdmitted;
    private readonly Counter<long> _rejected;
    private readonly Histogram<double> _queueWait;
    private readonly Task[] _threads;

    // Completed once the pool is closed and the last admission is released.
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _admitted;
    private int _running;

    // 1 once shutdown starts. Written with Interlocked.Exchange (a full fence) so the drain and Admit, which
    // increments before it reads this, cannot both miss each other.
    private int _closed;
    private readonly Lazy<Task> _disposal;

    public TursoIoPool(int threads, int maxAdmitted, Meter meter, Func<ThreadStart, Thread>? createWorker = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threads);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAdmitted);
        _maxAdmitted = maxAdmitted;
        _maxHobbyRunning = Math.Max(1, threads - 1);
        _disposal = new(DrainAsync);
        meter.CreateObservableGauge(
            "terrabase.storage.admitted", () => Volatile.Read(ref _admitted), description: "Storage transactions and maintenance calls admitted.");
        meter.CreateObservableGauge(
            "terrabase.storage.running", () => Volatile.Read(ref _running), description: "Turso calls running on the I/O threads.");
        // A saturated disk shows here first: reads queue for a thread while writers wait on what queued them (batch 8).
        meter.CreateObservableGauge("terrabase.storage.queued", () => Queued, description: "Turso calls waiting for an I/O thread.");
        _rejected = meter.CreateCounter<long>("terrabase.storage.rejected", description: "Storage work rejected as busy (HTTP 503).");
        _queueWait = meter.CreateHistogram<double>("terrabase.storage.queue_wait", "s",
            "Time a Turso call waited for an I/O thread, by lane: hobby, bulk or other (every other plan, system work).",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.0001, 0.0005, 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1] });
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
                worker.Name = "terrabase-turso";
                _threads[i] = exited.Task;
                workers[i] = worker;
                if (ExecutionContext.IsFlowSuppressed())
                {
                    worker.Start();
                }
                else
                {
                    using (ExecutionContext.SuppressFlow())
                    {
                        worker.Start();
                    }
                }
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
            throw new ObjectDisposedException(nameof(TursoIoPool));
        }

        // Reserve a quarter for Starter/Pro/Team/Enterprise/system, and an eighth for Enterprise/system.
        // With a one-slot pool every plan can still make progress.
        var limit = QosContext.Current?.Plan switch
        {
            PlanTier.Hobby when _maxAdmitted > 1 => _maxAdmitted - Math.Max(_maxAdmitted > 2 ? 2 : 1, _maxAdmitted / 4),
            PlanTier.Starter or PlanTier.Pro or PlanTier.Team when _maxAdmitted > 2 => _maxAdmitted - Math.Max(1, _maxAdmitted / 8),
            _ => _maxAdmitted,
        };
        if (admitted > limit)
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
    public override async Task<T> RunAsync<T>(Func<T> call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var diagnostic = Diagnostics?.Submit();
        // Continuations must not run on (and so block) a pool thread.
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Queued (0), started (1) or cancelled (2), decided once: a cancelled call never starts, and a started
        // one is awaited to the end, because the caller still owns its connection while it runs.
        var state = 0;
        var tenant = QosContext.Current;
        var context = CaptureContext();
        var weight = tenant is null ? 16 : QosPolicy.For(tenant.Plan).Weight;
        var lane = new KeyValuePair<string, object?>("lane", tenant?.Plan == PlanTier.Hobby ? "hobby" : StorageWork.IsBulk ? "bulk" : "other");
        var queuedAt = Stopwatch.GetTimestamp();
        var node = new LinkedListNode<WorkItem>(new WorkItem(tenant?.OrganizationId, 0, tenant?.Plan == PlanTier.Hobby, StorageWork.IsBulk, Run));
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

                diagnostic?.Cancel();
                result.TrySetCanceled(cancellationToken);
            }
        });
        lock (_queueGate)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            if (Volatile.Read(ref state) == 0)
            {
                // ponytail: bounded queue scans under one lock; use a heap if queue contention is measured.
                var finish = _virtualTime;
                foreach (var queued in _work)
                {
                    if (queued.Organization == tenant?.OrganizationId)
                    {
                        finish = Math.Max(finish, queued.Finish);
                    }
                }

                node.Value = node.Value with { Finish = finish + 65536 / weight };
                _work.AddLast(node);
                Monitor.PulseAll(_queueGate);
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
            _queueWait.Record(Stopwatch.GetElapsedTime(queuedAt).TotalSeconds, lane);
            diagnostic?.Begin();
            try
            {
                using var scope = RestoreContext(context);
                T value;
                try
                {
                    value = call();
                }
                finally
                {
                    // Record callback completion before making its asynchronous continuation runnable.
                    diagnostic?.Complete();
                }

                result.TrySetResult(value);
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
        // No deadline: maintenance can need several Turso calls even after a long VACUUM finishes.
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
            WorkItem item;
            lock (_queueGate)
            {
                LinkedListNode<WorkItem>? chosen;
                while (true)
                {
                    chosen = null;
                    for (var candidate = _work.First; candidate is not null; candidate = candidate.Next)
                    {
                        if (candidate.Value.Hobby && _runningHobby >= _maxHobbyRunning
                            || candidate.Value.Bulk && _runningBulk >= _maxHobbyRunning)
                        {
                            continue;
                        }

                        if (chosen is null || candidate.Value.Finish < chosen.Value.Finish)
                        {
                            chosen = candidate;
                        }
                    }

                    if (chosen is not null)
                    {
                        break;
                    }

                    if (_completed)
                    {
                        return;
                    }

                    Monitor.Wait(_queueGate);
                }

                item = chosen.Value;
                _virtualTime = Math.Max(_virtualTime, item.Finish);
                _work.Remove(chosen);
                _runningHobby += item.Hobby ? 1 : 0;
                _runningBulk += item.Bulk ? 1 : 0;
            }

            try
            {
                item.Run();
            }
            finally
            {
                if (item.Hobby || item.Bulk)
                {
                    lock (_queueGate)
                    {
                        _runningHobby -= item.Hobby ? 1 : 0;
                        _runningBulk -= item.Bulk ? 1 : 0;
                        Monitor.PulseAll(_queueGate);
                    }
                }
            }
        }
    }
}
