using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

internal sealed class QosSessions
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private const int MaxSessions = 4096;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _counts = [];
    private int _active;

    public IDisposable? TryEnter(TenantContext tenant)
    {
        lock (_gate)
        {
            var count = _counts.GetValueOrDefault(tenant.OrganizationId);
            if (_active >= MaxSessions || count >= QosPolicy.For(tenant.Plan).Sessions)
            {
                return null;
            }

            _counts[tenant.OrganizationId] = count + 1;
            _active++;
            return new Lease(this, tenant.OrganizationId);
        }
    }

    private void Leave(string organization)
    {
        lock (_gate)
        {
            var count = _counts[organization];
            if (count == 1)
            {
                _counts.Remove(organization);
            }
            else
            {
                _counts[organization] = count - 1;
            }

            _active--;
        }
    }

    private sealed class Lease(QosSessions owner, string organization) : IDisposable
    {
        private QosSessions? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave(organization);
    }
}
