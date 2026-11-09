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
                    foreach (var pair in _lanes.ToArray())
                    {
                        var existing = pair.Value;
                        var existingPolicy = QosPolicy.For(existing.Plan);
                        var replenished = existing.Tokens + Math.Max(0, time.GetElapsedTime(existing.LastSeen, now).TotalSeconds) * existingPolicy.RequestsPerSecond;
                        if (existing.Active == 0 && (existingPolicy.RequestsPerSecond == 0 || replenished >= existingPolicy.Burst)
                            && (existingPolicy.EngineSeconds == 0 || Engine(existing, existingPolicy, now) >= existingPolicy.EngineBurst))
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

                lane = new Lane { Tokens = policy.Burst, Engine = policy.EngineBurst, LastSeen = now, Plan = tenant.Plan };
                _lanes.Add(tenant.OrganizationId, lane);
            }

            lane.Tokens = Math.Min(policy.Burst, lane.Tokens + Math.Max(0, time.GetElapsedTime(lane.LastSeen, now).TotalSeconds) * policy.RequestsPerSecond);
            lane.Engine = Engine(lane, policy, now);
            if (lane.Plan != tenant.Plan)
            {
                var upgraded = lane.Plan < tenant.Plan || lane.Plan == PlanTier.Enterprise;
                lane.Tokens = upgraded ? policy.Burst : Math.Min(lane.Tokens, policy.Burst);
                lane.Engine = upgraded ? policy.EngineBurst : Math.Min(lane.Engine, policy.EngineBurst);
                lane.Plan = tenant.Plan;
            }

            lane.LastSeen = Math.Max(lane.LastSeen, now);
            if (policy.EngineSeconds > 0 && lane.Engine <= 0)
            {
                retryAfter = Math.Max(1, (int)Math.Ceiling(-lane.Engine / policy.EngineSeconds));
                Reject(tenant, "engine");
                return null;
            }

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

    public void ChargeEngine(TenantContext tenant, TimeSpan spent)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        lock (_gate)
        {
            if (!_lanes.TryGetValue(tenant.OrganizationId, out var lane))
            {
                return;
            }

            var policy = QosPolicy.For(lane.Plan);
            if (policy.EngineSeconds == 0)
            {
                return;
            }

            var now = time.GetTimestamp();
            lane.Tokens = Math.Min(policy.Burst, lane.Tokens + Math.Max(0, time.GetElapsedTime(lane.LastSeen, now).TotalSeconds) * policy.RequestsPerSecond);
            lane.Engine = Engine(lane, policy, now) - Math.Max(0, spent.TotalSeconds);
            lane.LastSeen = Math.Max(lane.LastSeen, now);
        }
    }

    private double Engine(Lane lane, QosPolicy policy, long now) =>
        Math.Min(policy.EngineBurst, lane.Engine + Math.Max(0, time.GetElapsedTime(lane.LastSeen, now).TotalSeconds) * policy.EngineSeconds);

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
        new("plan", tenant.Plan.JsonName), new("tenant_bucket", Bucket(tenant)), new("reason", reason));

    private void Record(TenantContext tenant, long started) => _duration.Record(Math.Max(0, time.GetElapsedTime(started).TotalSeconds),
        new("plan", tenant.Plan.JsonName), new("tenant_bucket", Bucket(tenant)));

    private sealed class Lane
    {
        public double Tokens { get; set; }
        public double Engine { get; set; }
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
