using TerraBase.Features.Memory;
using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

public static class QosContext
{
    private static readonly AsyncLocal<TenantContext?> Tenant = new();
    private static readonly AsyncLocal<ResultMemory?> Results = new();
    private static readonly AsyncLocal<IQosAdmission?> Admission = new();

    public static TenantContext? Current => Tenant.Value;

    public static void ReserveResult(long bytes) => ReserveResult(bytes, Current);

    public static void ReserveResult(long bytes, TenantContext? tenant) => Results.Value?.Reserve(bytes, tenant);

    public static void ChargeEngine(TimeSpan spent)
    {
        if (Tenant.Value is { } tenant && Admission.Value is { } admission)
        {
            admission.ChargeEngine(tenant, spent);
        }
    }

    internal readonly record struct Snapshot(TenantContext? Tenant, ResultMemory? Results, IQosAdmission? Admission);

    internal static Snapshot Capture() => new(Tenant.Value, Results.Value, Admission.Value);

    internal static IDisposable Restore(Snapshot snapshot)
    {
        var scope = new Scope(Tenant.Value, Results.Value, null, Admission.Value);
        Tenant.Value = snapshot.Tenant;
        Results.Value = snapshot.Results;
        Admission.Value = snapshot.Admission;
        return scope;
    }

    internal static IDisposable Enter(TenantContext? tenant, IMemoryBudget? budget = null, IQosAdmission? admission = null)
    {
        var previous = Tenant.Value;
        var previousResults = Results.Value;
        var previousAdmission = Admission.Value;
        var owned = budget is not null ? new ResultMemory(budget) : null;
        Tenant.Value = tenant;
        Results.Value = owned ?? previousResults;
        Admission.Value = admission ?? previousAdmission;
        return new Scope(previous, previousResults, owned, previousAdmission);
    }

    public static IDisposable EnterFlight(TenantContext tenant, ResultMemory memory)
    {
        var scope = new Scope(Tenant.Value, Results.Value, null, Admission.Value);
        Tenant.Value = tenant;
        Results.Value = memory;
        return scope;
    }

    public static void AttachResults(ResultMemory memory) => Results.Value?.Attach(memory);

    private sealed class Scope(TenantContext? previous, ResultMemory? previousResults, ResultMemory? owned, IQosAdmission? previousAdmission) : IDisposable
    {
        public void Dispose()
        {
            owned?.Dispose();
            Results.Value = previousResults;
            Tenant.Value = previous;
            Admission.Value = previousAdmission;
        }
    }

    public sealed class ResultMemory(IMemoryBudget budget) : IDisposable
    {
        private const int MaxResults = 16_384;
        private readonly Lock _gate = new();
        private readonly List<IDisposable> _leases = [];
        private int _references = 1;
        private int _ownerReleased;
        private IMemoryReservation? _growing;
        private TenantContext? _growingTenant;

        public void Reserve(long bytes, TenantContext? tenant)
        {
            lock (_gate)
            {
                if (_references == 0)
                {
                    throw new QosMemoryException();
                }

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
