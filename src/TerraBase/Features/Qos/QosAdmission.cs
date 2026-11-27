using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using TerraBase.Hosting;

namespace TerraBase.Features.Qos;

internal readonly struct QosLease : IDisposable
{
    private readonly QosAdmission.DatabaseGate? _gate;

    internal QosLease(QosAdmission.DatabaseGate gate) => _gate = gate;

    public bool IsAcquired => _gate is not null;

    public void Dispose() => _gate?.Release();
}

internal sealed class QosAdmission : IAsyncDisposable
{
    public const int PermitsPerDatabase = 64;

    public const int QueuePerDatabase = 256;

    private readonly ConcurrentDictionary<Ulid, DatabaseGate> _gates = new();

    private readonly Counter<long> _refused;

    private bool _disposed;

    public QosAdmission(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _refused = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.qos.refused");
    }

    public ValueTask<QosLease> AdmitAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed))
        {
            return ValueTask.FromResult(default(QosLease));
        }

        var gate = _gates.GetOrAdd(databaseId, static _ => new DatabaseGate());
        if (gate.TryAcquireImmediate())
        {
            return ValueTask.FromResult(new QosLease(gate));
        }

        return AdmitSlowAsync(gate, cancellationToken);
    }

    private async ValueTask<QosLease> AdmitSlowAsync(DatabaseGate gate, CancellationToken cancellationToken)
    {
        var lease = await gate.AcquireQueuedAsync(cancellationToken);
        if (!lease.IsAcquired)
        {
            _refused.Add(1);
        }

        return lease;
    }

    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref _disposed, true);
        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    internal sealed class DatabaseGate
    {
        private const long WaiterUnit = 1L << 32;

        private const long ActiveMask = 0xFFFF_FFFFL;

        private readonly Lock _queueGate = new();

        private long _state;

        private Waiter? _head;

        private Waiter? _tail;

        private int _waiterCount;

        private bool _disposed;

        public bool TryAcquireImmediate()
        {
            var current = Volatile.Read(ref _state);
            while (true)
            {
                var active = (int)(current & ActiveMask);
                var waiters = (int)(current >> 32);
                if (active >= PermitsPerDatabase || waiters > 0)
                {
                    return false;
                }

                var prior = Interlocked.CompareExchange(ref _state, current + 1, current);
                if (prior == current)
                {
                    return true;
                }

                current = prior;
            }
        }

        public ValueTask<QosLease> AcquireQueuedAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<QosLease>(cancellationToken);
            }

            Waiter waiter;
            Waiter? otherPromoted = null;
            lock (_queueGate)
            {
                if (_disposed)
                {
                    return ValueTask.FromResult(default(QosLease));
                }

                if (TryAcquireImmediate())
                {
                    return ValueTask.FromResult(new QosLease(this));
                }

                if (_waiterCount >= QueuePerDatabase)
                {
                    return ValueTask.FromResult(default(QosLease));
                }

                waiter = new Waiter(this);
                AppendLocked(waiter);
                _ = Interlocked.Add(ref _state, WaiterUnit);
                if (TryPromoteHeadLocked(out var promoted))
                {
                    if (ReferenceEquals(promoted, waiter))
                    {
                        return ValueTask.FromResult(new QosLease(this));
                    }

                    otherPromoted = promoted;
                }
            }

            otherPromoted?.CompleteGranted();
            return AwaitWaiterAsync(waiter, cancellationToken);
        }

        public void Release()
        {
            var current = Volatile.Read(ref _state);
            while (true)
            {
                var waiters = (int)(current >> 32);
                if (waiters > 0)
                {
                    ReleaseToWaiter();
                    return;
                }

                var prior = Interlocked.CompareExchange(ref _state, current - 1, current);
                if (prior == current)
                {
                    return;
                }

                current = prior;
            }
        }

        public void Dispose()
        {
            Waiter? head;
            lock (_queueGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                head = _head;
                _head = null;
                _tail = null;
                var count = _waiterCount;
                _waiterCount = 0;
                if (count > 0)
                {
                    _ = Interlocked.Add(ref _state, -count * WaiterUnit);
                }

                for (var node = head; node is not null; node = node.Next)
                {
                    node.RefuseLocked();
                }
            }

            for (var node = head; node is not null; node = node.Next)
            {
                node.CompleteRefused();
            }
        }

        private async ValueTask<QosLease> AwaitWaiterAsync(Waiter waiter, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.UnsafeRegister(static (state, token) =>
                {
                    if (state is Waiter queued)
                    {
                        queued.Cancel(token);
                    }
                }, waiter)
                : default;
            return await waiter.Task ? new QosLease(this) : default;
        }

        private void ReleaseToWaiter()
        {
            Waiter? handedOff = null;
            lock (_queueGate)
            {
                if (_head is { } head)
                {
                    RemoveLocked(head);
                    _ = Interlocked.Add(ref _state, -WaiterUnit);
                    head.GrantLocked();
                    handedOff = head;
                }
                else
                {
                    _ = Interlocked.Decrement(ref _state);
                }
            }

            handedOff?.CompleteGranted();
        }

        private bool TryPromoteHeadLocked([NotNullWhen(true)] out Waiter? promoted)
        {
            while (_head is { } head)
            {
                var current = Volatile.Read(ref _state);
                var active = (int)(current & ActiveMask);
                if (active >= PermitsPerDatabase)
                {
                    promoted = null;
                    return false;
                }

                var prior = Interlocked.CompareExchange(ref _state, current - WaiterUnit + 1, current);
                if (prior == current)
                {
                    RemoveLocked(head);
                    head.GrantLocked();
                    promoted = head;
                    return true;
                }
            }

            promoted = null;
            return false;
        }

        private void AppendLocked(Waiter node)
        {
            node.Previous = _tail;
            node.Next = null;
            if (_tail is { } tail)
            {
                tail.Next = node;
            }
            else
            {
                _head = node;
            }

            _tail = node;
            _waiterCount++;
        }

        private void RemoveLocked(Waiter node)
        {
            if (node.Previous is { } previous)
            {
                previous.Next = node.Next;
            }
            else
            {
                _head = node.Next;
            }

            if (node.Next is { } next)
            {
                next.Previous = node.Previous;
            }
            else
            {
                _tail = node.Previous;
            }

            node.Previous = null;
            node.Next = null;
            _waiterCount--;
        }

        private sealed class Waiter(DatabaseGate owner)
        {
            private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            private int _state;

            public Waiter? Previous;

            public Waiter? Next;

            public Task<bool> Task => _completion.Task;

            public void GrantLocked() => _state = 1;

            public void RefuseLocked() => _state = 2;

            public void Cancel(CancellationToken cancellationToken)
            {
                var canceled = false;
                lock (owner._queueGate)
                {
                    if (_state == 0)
                    {
                        _state = 2;
                        owner.RemoveLocked(this);
                        _ = Interlocked.Add(ref owner._state, -WaiterUnit);
                        canceled = true;
                    }
                }

                if (canceled)
                {
                    _ = _completion.TrySetCanceled(cancellationToken);
                }
            }

            public void CompleteGranted() => _ = _completion.TrySetResult(true);

            public void CompleteRefused() => _ = _completion.TrySetResult(false);
        }
    }
}
