using TerraBase.Features.Memory;
using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

/// <summary>The authenticated request's organization, independent of cached database store handles.</summary>
public static class QosContext
{
    private static readonly AsyncLocal<TenantContext?> Tenant = new();
    private static readonly AsyncLocal<ResultMemory?> Results = new();

    /// <summary>The request's current plan; null for system and background work.</summary>
    public static TenantContext? Current => Tenant.Value;

    /// <summary>Admits detached result buffers before allocation, retaining admission through HTTP serialization.</summary>
    public static void ReserveResult(long bytes) => ReserveResult(bytes, Current);

    /// <summary>Admits against the explicit tenant, or only the process when null, retaining the current result owner.</summary>
    public static void ReserveResult(long bytes, TenantContext? tenant) => Results.Value?.Reserve(bytes, tenant);

    internal readonly record struct Snapshot(TenantContext? Tenant, ResultMemory? Results);

    internal static Snapshot Capture() => new(Tenant.Value, Results.Value);

    internal static IDisposable Restore(Snapshot snapshot)
    {
        var scope = new Scope(Tenant.Value, Results.Value, null);
        Tenant.Value = snapshot.Tenant;
        Results.Value = snapshot.Results;
        return scope;
    }

    internal static IDisposable Enter(TenantContext? tenant, IMemoryBudget? budget = null)
    {
        var previous = Tenant.Value;
        var previousResults = Results.Value;
        var owned = budget is not null ? new ResultMemory(budget) : null;
        Tenant.Value = tenant;
        Results.Value = owned ?? previousResults;
        return new Scope(previous, previousResults, owned);
    }

    /// <summary>Uses independently owned admission for a shared read.</summary>
    public static IDisposable EnterFlight(TenantContext tenant, ResultMemory memory)
    {
        var scope = new Scope(Tenant.Value, Results.Value, null);
        Tenant.Value = tenant;
        Results.Value = memory;
        return scope;
    }

    /// <summary>Retains shared read admission through this consumer's response serialization.</summary>
    public static void AttachResults(ResultMemory memory) => Results.Value?.Attach(memory);

    private sealed class Scope(TenantContext? previous, ResultMemory? previousResults, ResultMemory? owned) : IDisposable
    {
        public void Dispose()
        {
            owned?.Dispose();
            Results.Value = previousResults;
            Tenant.Value = previous;
        }
    }

    /// <summary>A shared read owns one reference until it finishes; each consumer retains another through serialization.</summary>
    public sealed class ResultMemory(IMemoryBudget budget) : IDisposable
    {
        private const int MaxResults = 16_384;
        private readonly Lock _gate = new();
        private readonly List<IDisposable> _leases = [];
        private int _references = 1;
        private int _ownerReleased;
        private IMemoryReservation? _growing;
        private TenantContext? _growingTenant;

        /// <summary>Reserves result buffers before allocating them.</summary>
        public void Reserve(long bytes, TenantContext? tenant)
        {
            lock (_gate)
            {
                if (_references == 0)
                {
                    throw new QosMemoryException();
                }

                // An owner retains every read until it ends (a catalog import reads each state it holds in one transaction),
                // so reads of one tenant grow one lease: the byte limits still bound them, the reservation tables do not.
                if (_growing is not null && Equals(_growingTenant, tenant) && budget.TryGrow(_growing, bytes))
                {
                    return;
                }

                if (_leases.Count == MaxResults || budget.TryReserve(MemoryLane.Foreground, bytes, tenant) is not { } lease)
                {
                    throw new QosMemoryException();
                }

                _leases.Add(lease);
                (_growing, _growingTenant) = (lease, tenant);
            }
        }

        /// <summary>Retains another result owner until this owner's final release.</summary>
        public void Attach(ResultMemory memory)
        {
            lock (_gate)
            {
                if (_references == 0 || _leases.Count == MaxResults)
                {
                    throw new QosMemoryException();
                }

                lock (memory._gate)
                {
                    if (memory._references == 0)
                    {
                        throw new QosMemoryException();
                    }

                    memory._references++;
                }

                _leases.Add(new Reference(memory));
            }
        }

        /// <summary>Releases this owner's reference once; consumers can continue retaining its buffers.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ownerReleased, 1) == 0)
            {
                Release();
            }
        }

        private void Release()
        {
            lock (_gate)
            {
                if (--_references != 0)
                {
                    return;
                }

                foreach (var lease in _leases)
                {
                    lease.Dispose();
                }

                _leases.Clear();
            }
        }

        private sealed class Reference(ResultMemory memory) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    memory.Release();
                }
            }
        }
    }
}
