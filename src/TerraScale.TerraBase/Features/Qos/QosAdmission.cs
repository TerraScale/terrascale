using System.Diagnostics.Metrics;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

internal sealed class QosAdmission(TimeProvider time, IMeterFactory meters) : IQosAdmission
{
    private const int MaxTenants = 4096;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Lane> _lanes = [];
    private readonly Counter<long> _rejected = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.qos.rejected");
    private readonly Histogram<double> _duration = meters.Create(Telemetry.MeterName).CreateHistogram<double>("terrabase.qos.request.duration", "s");

    public IDisposable? TryEnter(TenantContext tenant, out int retryAfter)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var policy = QosPolicy.For(tenant.Plan);
        retryAfter = 1;
        lock (_gate)
        {
            var now = time.GetTimestamp();
            if (!_lanes.TryGetValue(tenant.OrganizationId, out var lane))
            {
                if (_lanes.Count == MaxTenants)
                {
                    // Keep depleted buckets until fully replenished: eviction must not reset a noisy tenant's burst.
                    foreach (var pair in _lanes.ToArray())
                    {
                        var existing = pair.Value;
                        if (existing.Active == 0 && time.GetElapsedTime(existing.LastSeen, now) >= TimeSpan.FromMinutes(1))
                        {
                            _lanes.Remove(pair.Key);
                        }
                    }
                }

                if (_lanes.Count == MaxTenants)
                {
                    Reject(tenant, "capacity");
                    return null;
                }

                lane = new Lane { Tokens = policy.Burst, LastSeen = now, Plan = tenant.Plan };
                _lanes.Add(tenant.OrganizationId, lane);
            }

            lane.Tokens = Math.Min(policy.Burst, lane.Tokens + time.GetElapsedTime(lane.LastSeen, now).TotalSeconds * policy.RequestsPerSecond);
            if (lane.Plan != tenant.Plan)
            {
                // An unlimited plan has no token balance. Starting a finite bucket must not lock out its first request.
                lane.Tokens = lane.Plan < tenant.Plan || lane.Plan == PlanTier.Enterprise
                    ? policy.Burst : Math.Min(lane.Tokens, policy.Burst);
                lane.Plan = tenant.Plan;
            }

            lane.LastSeen = now;
            if (policy.Concurrency > 0 && lane.Active >= policy.Concurrency)
            {
                Reject(tenant, "concurrency");
                return null;
            }

            if (policy.RequestsPerSecond > 0 && lane.Tokens < 1)
            {
                retryAfter = Math.Max(1, (int)Math.Ceiling((1 - lane.Tokens) / policy.RequestsPerSecond));
                Reject(tenant, "rate");
                return null;
            }

            if (policy.RequestsPerSecond > 0)
            {
                lane.Tokens--;
            }

            lane.Active++;
            return new Lease(this, lane, tenant, now);
        }
    }

    // 64 stable buckets, deliberately shared on collisions; raw organization IDs never become labels.
    private static int Bucket(TenantContext tenant)
    {
        var hash = 2166136261u;
        foreach (var character in tenant.OrganizationId)
        {
            hash = unchecked((hash ^ character) * 16777619u);
        }

        return (int)(hash & 63);
    }

    private void Reject(TenantContext tenant, string reason) => _rejected.Add(1,
        new("plan", tenant.Plan.ToString().ToLowerInvariant()), new("tenant_bucket", Bucket(tenant)), new("reason", reason));

    private void Record(TenantContext tenant, long started) => _duration.Record(time.GetElapsedTime(started).TotalSeconds,
        new("plan", tenant.Plan.ToString().ToLowerInvariant()), new("tenant_bucket", Bucket(tenant)));

    private sealed class Lane
    {
        public double Tokens { get; set; }
        public long LastSeen { get; set; }
        public int Active { get; set; }
        public PlanTier Plan { get; set; }
    }

    private sealed class Lease(QosAdmission admission, Lane lane, TenantContext tenant, long started) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (admission._gate)
                {
                    lane.Active--;
                }

                admission.Record(tenant, started);
            }
        }
    }
}
