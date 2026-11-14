using System.Diagnostics.Metrics;
using System.Net;
using TerraBase.Hosting;
using TerraBase.Features.Platform;
using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

internal sealed class QosSessions
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(30);
    public const int MinimumMessageBytesPerSecond = 16 * 1024;
    public static TimeSpan SendTimeout(long bytes) => MessageTimeout + TimeSpan.FromSeconds((double)bytes / MinimumMessageBytesPerSecond);
    private const int MaxSessions = 4096;
    private const int MaxRelays = MaxSessions / 2;
    public const string ConnectionAttemptsMessage = "too many concurrent connection attempts from this address";
    private static readonly int MaxPreauthenticationPerAddress = QosPolicy.For(PlanTier.Enterprise).Sessions;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (int Count, int Limit)> _counts = [];
    private readonly Dictionary<IPAddress, int> _preauthentication = [];
    private readonly Counter<long> _refusals;
    private readonly Counter<long> _admissions;
    private readonly Counter<long> _preauthenticationAdmissions;
    private int _active;
    private int _relays;

    public QosSessions(IMeterFactory meters)
    {
        var meter = meters.Create(Telemetry.MeterName);
        meter.CreateObservableGauge("terrabase.qos.relay.sessions", () => Volatile.Read(ref _relays));
        _refusals = meter.CreateCounter<long>("terrabase.qos.session.refusals");
        _admissions = meter.CreateCounter<long>("terrabase.qos.session.admissions");
        _preauthenticationAdmissions = meter.CreateCounter<long>("terrabase.qos.preauthentication.admissions");
        meter.CreateObservableGauge("terrabase.qos.active.sessions", () => Volatile.Read(ref _active));
        meter.CreateObservableGauge("terrabase.qos.preauthentication.sessions", PreauthenticationCount);
        meter.CreateObservableGauge("terrabase.qos.organization.session.utilization", Utilization);
    }

    private double Utilization()
    {
        lock (_gate)
        {
            return _counts.Count == 0 ? 0 : _counts.Values.Max(entry => (double)entry.Count / entry.Limit);
        }
    }

    private int PreauthenticationCount()
    {
        lock (_gate)
        {
            return _preauthentication.Values.Sum();
        }
    }

    public IDisposable? TryEnter(TenantContext tenant, bool relay = false)
    {
        if (PlatformDatabases.IsPlatform(tenant.OrganizationId))
        {
            return new Lease(() => { });
        }

        lock (_gate)
        {
            var entry = _counts.GetValueOrDefault(tenant.OrganizationId);
            var limit = QosPolicy.For(tenant.Plan).Sessions;
            var kind = entry.Count >= limit ? "organization_cap"
                : _active >= MaxSessions ? "process_ceiling"
                : relay && _relays >= MaxRelays ? "relay_ceiling" : null;
            if (kind is not null)
            {
                _refusals.Add(1, new KeyValuePair<string, object?>("kind", kind));
                return null;
            }

            _counts[tenant.OrganizationId] = (entry.Count + 1, limit);
            _active++;
            _admissions.Add(1);
            if (relay)
            {
                _relays++;
            }

            return new Lease(() => Leave(tenant.OrganizationId, relay));
        }
    }

    public SessionPreauthentication? TryPreauthenticate(IPAddress? address, TimeSpan deadline, CancellationToken cancellationToken, string? verifiedOrganization = null)
    {
        if (PlatformDatabases.IsPlatform(verifiedOrganization))
        {
            return new SessionPreauthentication(new Lease(() => { }), deadline, cancellationToken);
        }

        address = (address ?? IPAddress.None).MapToIPv6();
        lock (_gate)
        {
            var count = _preauthentication.GetValueOrDefault(address);
            if (count >= MaxPreauthenticationPerAddress)
            {
                _refusals.Add(1, new KeyValuePair<string, object?>("kind", "preauthentication_allowance"));
                return null;
            }

            _preauthentication[address] = count + 1;
            _preauthenticationAdmissions.Add(1);
        }

        return new SessionPreauthentication(new Lease(() => LeavePreauthentication(address)), deadline, cancellationToken);
    }

    private void LeavePreauthentication(IPAddress address)
    {
        lock (_gate)
        {
            if (--_preauthentication[address] == 0)
            {
                _preauthentication.Remove(address);
            }
        }
    }

    private void Leave(string organization, bool relay)
    {
        lock (_gate)
        {
            _active--;
            if (relay)
            {
                _relays--;
            }

            var entry = _counts[organization];
            if (entry.Count == 1)
            {
                _counts.Remove(organization);
            }
            else
            {
                _counts[organization] = (entry.Count - 1, entry.Limit);
            }
        }
    }

    private sealed class Lease(Action leave) : IDisposable
    {
        private Action? _leave = leave;
        public void Dispose() => Interlocked.Exchange(ref _leave, null)?.Invoke();
    }
}
