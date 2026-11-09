using System.Diagnostics;
using System.Diagnostics.Metrics;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed class TursoIoPool : TursoRunner, IAsyncDisposable
{
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

    private int _runningBulk;

    private readonly int _maxTenantRunning;
    private readonly Dictionary<string, int> _runningByTenant = [];

    private sealed record WorkItem(string? Organization, long Finish, bool Hobby, bool Bulk, Action Run);

    private readonly object _queueGate = new();
    private bool _completed;
    private readonly Counter<long> _rejected;
    private readonly Histogram<double> _queueWait;
    private readonly Task[] _threads;

    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _admitted;
    private int _running;

    private int _closed;
    private readonly Lazy<Task> _disposal;

    public TursoIoPool(int threads, int maxAdmitted, Meter meter)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threads);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAdmitted);
        AdmissionCapacity = maxAdmitted;
        _maxHobbyRunning = Math.Max(1, threads - 1);
        _maxTenantRunning = Math.Max(1, Math.Min(threads / 2, threads - 2));
        _disposal = new(DrainAsync);
        meter.CreateObservableGauge(
            "terrabase.storage.admitted", () => Volatile.Read(ref _admitted), description: "Storage transactions and maintenance calls admitted.");
        meter.CreateObservableGauge(
            "terrabase.storage.running", () => Volatile.Read(ref _running), description: "Turso calls running on the I/O threads.");
        meter.CreateObservableGauge("terrabase.storage.queued", () => Queued, description: "Turso calls waiting for an I/O thread.");
        _rejected = meter.CreateCounter<long>("terrabase.storage.rejected", description: "Storage work rejected as busy (HTTP 503).");
        _queueWait = meter.CreateHistogram<double>("terrabase.storage.queue_wait", "s",
            "Time a Turso call waited for an I/O thread, by lane: hobby, bulk or other (every other plan, system work).",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.0001, 0.0005, 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1] });
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
                var worker = new Thread(Run);
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

            for (var i = 0; i < started; i++)
            {
                workers[i].Join();
            }

            throw;
        }
    }

    public void Admit()
    {
        var admitted = Interlocked.Increment(ref _admitted);
        if (Volatile.Read(ref _closed) != 0)
        {
            Release();
            throw new ObjectDisposedException(nameof(TursoIoPool));
        }

        var limit = QosContext.Current?.Plan switch
        {
            PlanTier.Hobby when AdmissionCapacity > 1 => AdmissionCapacity - Math.Max(AdmissionCapacity > 2 ? 2 : 1, AdmissionCapacity / 4),
            PlanTier.Starter or PlanTier.Pro or PlanTier.Team when AdmissionCapacity > 2 => AdmissionCapacity - Math.Max(1, AdmissionCapacity / 8),
            _ => AdmissionCapacity,
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

    public StorageBusyException Busy()
    {
        _rejected.Add(1);
        return new StorageBusyException();
    }

    internal int AdmissionCapacity { get; }
    internal int WorkerCount => _threads.Length;

    internal int Admitted => Volatile.Read(ref _admitted);

    internal int Running => Volatile.Read(ref _running);

    internal bool Closed => Volatile.Read(ref _closed) != 0;

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

    public override async Task<T> RunAsync<T>(Func<T> call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var diagnostic = Diagnostics?.Submit();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    public ValueTask DisposeAsync()
    {
        return new(_disposal.Value);
    }

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
                            || candidate.Value.Bulk && _runningBulk >= _maxHobbyRunning
                            || candidate.Value.Organization is { } organization && _runningByTenant.GetValueOrDefault(organization) >= _maxTenantRunning)
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
                if (item.Organization is { } started)
                {
                    _runningByTenant[started] = _runningByTenant.GetValueOrDefault(started) + 1;
                }
            }

            try
            {
                item.Run();
            }
            finally
            {
                if (item.Hobby || item.Bulk || item.Organization is not null)
                {
                    lock (_queueGate)
                    {
                        _runningHobby -= item.Hobby ? 1 : 0;
                        _runningBulk -= item.Bulk ? 1 : 0;
                        if (item.Organization is { } finished && --_runningByTenant[finished] == 0)
                        {
                            _runningByTenant.Remove(finished);
                        }

                        Monitor.PulseAll(_queueGate);
                    }
                }
            }
        }
    }
}
