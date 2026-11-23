using System.Diagnostics.Metrics;
using TerraBase.Hosting;

namespace TerraBase.Features.Qos;

internal readonly union TenantSessionOutcome(TenantSessions.TenantSession, TenantBusy);

internal sealed class TenantSessions
{
    public const string LimitMessage = "organization session limit exceeded";

    private readonly Lock _gate = new();

    private readonly Dictionary<Ulid, int> _open = [];

    private readonly Counter<long> _refusals;

    private long _active;

    private long _preauthentication;

    public TenantSessions(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(Telemetry.MeterName);
        _refusals = meter.CreateCounter<long>("terrabase.qos.session.refusals");
        _ = meter.CreateObservableGauge("terrabase.qos.active.sessions", () => Volatile.Read(ref _active));
        _ = meter.CreateObservableGauge("terrabase.qos.preauthentication.sessions", () => Volatile.Read(ref _preauthentication));
    }

    public PreauthenticationSession BeginPreauthentication()
    {
        _ = Interlocked.Increment(ref _preauthentication);
        return new PreauthenticationSession(this);
    }

    public TenantSessionOutcome TryOpen(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        lock (_gate)
        {
            var open = _open.GetValueOrDefault(tenant.OrganizationId);
            if (open >= tenant.Policy.Sessions)
            {
                _refusals.Add(1, new KeyValuePair<string, object?>("kind", "organization_cap"));
                return new TenantBusy("sessions", TimeSpan.FromSeconds(1));
            }

            _open[tenant.OrganizationId] = open + 1;
            _ = Interlocked.Increment(ref _active);
            return new TenantSession(this, tenant.OrganizationId);
        }
    }

    private void Close(Ulid organizationId)
    {
        lock (_gate)
        {
            var open = _open.GetValueOrDefault(organizationId) - 1;
            if (open <= 0)
            {
                _open.Remove(organizationId);
            }
            else
            {
                _open[organizationId] = open;
            }

            _ = Interlocked.Decrement(ref _active);
        }
    }

    private void EndPreauthentication() => _ = Interlocked.Decrement(ref _preauthentication);

    internal sealed class PreauthenticationSession(TenantSessions owner) : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                owner.EndPreauthentication();
            }
        }
    }

    internal sealed class TenantSession(TenantSessions owner, Ulid organizationId) : IDisposable, IAsyncDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                owner.Close(organizationId);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
