using System.Diagnostics.Metrics;
using System.Globalization;
using TerraBase.Features.Callers;
using TerraBase.Hosting;

namespace TerraBase.Features.Qos;

internal readonly union TenantAdmissionOutcome(TenantAdmission.TenantLease, TenantBusy);

internal sealed class TenantAdmission(TimeProvider time, IMeterFactory meters)
{
    private const int MaxTenants = 4096;

    private readonly Lock _gate = new();

    private readonly Dictionary<Ulid, Lane> _lanes = [];

    private readonly Counter<long> _rejected = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.qos.rejected");

    private readonly Histogram<double> _duration = meters.Create(Telemetry.MeterName).CreateHistogram<double>("terrabase.qos.request.duration", "s");

    public TenantAdmissionOutcome TryEnter(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var policy = tenant.Policy;
        lock (_gate)
        {
            var now = time.GetTimestamp();
            if (!_lanes.TryGetValue(tenant.OrganizationId, out var lane))
            {
                if (_lanes.Count >= MaxTenants)
                {
                    Evict(now);
                }

                if (_lanes.Count >= MaxTenants)
                {
                    return Reject(tenant, "capacity", TimeSpan.FromSeconds(1));
                }

                lane = new Lane { Tokens = policy.Burst, LastSeen = now, Plan = tenant.Plan };
                _lanes.Add(tenant.OrganizationId, lane);
            }

            if (lane.Plan != tenant.Plan)
            {
                lane.Tokens = policy.Burst;
                lane.Plan = tenant.Plan;
            }

            lane.Tokens = Math.Min(policy.Burst, lane.Tokens + (Math.Max(0, time.GetElapsedTime(lane.LastSeen, now).TotalSeconds) * policy.RequestsPerSecond));
            lane.LastSeen = Math.Max(lane.LastSeen, now);
            if (lane.Active >= policy.Concurrency)
            {
                return Reject(tenant, "concurrency", TimeSpan.FromSeconds(1));
            }

            if (lane.Tokens < 1)
            {
                return Reject(tenant, "rate", TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling((1 - lane.Tokens) / policy.RequestsPerSecond))));
            }

            lane.Tokens--;
            lane.Active++;
            return new TenantLease(this, lane, tenant, now);
        }
    }

    private void Evict(long now)
    {
        foreach (var (organization, lane) in _lanes.ToArray())
        {
            var policy = QosPolicy.For(lane.Plan);
            if (lane.Active == 0 && lane.Tokens + (time.GetElapsedTime(lane.LastSeen, now).TotalSeconds * policy.RequestsPerSecond) >= policy.Burst)
            {
                _lanes.Remove(organization);
            }
        }
    }

    private TenantBusy Reject(Tenant tenant, string reason, TimeSpan retryAfter)
    {
        _rejected.Add(1, new("plan", tenant.Plan), new("tenant_bucket", tenant.Bucket), new("reason", reason));
        return new TenantBusy(reason, retryAfter);
    }

    private void Leave(Lane lane, Tenant tenant, long started)
    {
        lock (_gate)
        {
            lane.Active--;
        }

        _duration.Record(Math.Max(0, time.GetElapsedTime(started).TotalSeconds), new("plan", tenant.Plan), new("tenant_bucket", tenant.Bucket));
    }

    internal sealed class Lane
    {
        public double Tokens { get; set; }

        public long LastSeen { get; set; }

        public int Active { get; set; }

        public required string Plan { get; set; }
    }

    internal sealed class TenantLease(TenantAdmission admission, Lane lane, Tenant tenant, long started) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                admission.Leave(lane, tenant, started);
            }
        }
    }
}

internal sealed record CallerTenantMetadata
{
    public static CallerTenantMetadata Instance { get; } = new();
}

internal static class TenantAdmissionExtensions
{
    extension(WebApplication app)
    {
        public WebApplication UseTerraBaseTenantQos()
        {
            app.Use(async (context, next) =>
            {
                var caller = context.Caller;
                if (!Tracked(caller))
                {
                    await next(context);
                    return;
                }

                var target = await TargetAsync(context, caller, context.RequestServices.GetRequiredService<TenantDirectory>());
                await (target switch
                {
                    Tenant tenant => EnterAsync(context, next, tenant),
                    ForeignProject => EnterAsync(context, next, Tenant.Unowned),
                    NoTenant => next(context),
                    BackendUnavailable unavailable => ErrorResults.Json(StatusCodes.Status503ServiceUnavailable, unavailable.Code, unavailable.Message,
                        retryAfter: TimeSpan.FromSeconds(1)).ExecuteAsync(context),
                });
            });
            return app;
        }
    }

    private static async ValueTask<CallerTenantResolution> TargetAsync(HttpContext context, Caller caller, TenantDirectory directory)
    {
        if (context.Request.RouteValues["project"] is string reference)
        {
            return reference.Length == 26 && Ulid.TryParse(reference, out var projectId)
                ? await directory.ForCallerProjectAsync(caller, projectId, context.RequestAborted)
                : NoTenant.Instance;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<CallerTenantMetadata>() is null)
        {
            return NoTenant.Instance;
        }

        return caller switch
        {
            ApiKeyCaller key => await directory.ForOrganizationAsync(key.Key.OrganizationId, context.RequestAborted) switch
            {
                Tenant tenant => tenant,
                NoTenant none => none,
                BackendUnavailable unavailable => unavailable,
            },
            ProjectCaller token => await directory.ForCallerProjectAsync(caller, token.ProjectId, context.RequestAborted),
            EndUserCaller endUser => await directory.ForCallerProjectAsync(caller, endUser.ProjectId, context.RequestAborted),
            AnonymousCaller or PeerCaller or ServiceCaller or UserCaller => NoTenant.Instance,
        };
    }

    private static Task EnterAsync(HttpContext context, RequestDelegate next, Tenant tenant)
    {
        var admitted = context.RequestServices.GetRequiredService<TenantAdmission>().TryEnter(tenant);
        return admitted switch
        {
            TenantAdmission.TenantLease lease => AdmitAsync(context, next, lease),
            TenantBusy busy => RefuseAsync(context, busy),
        };
    }

    private static async Task AdmitAsync(HttpContext context, RequestDelegate next, TenantAdmission.TenantLease lease)
    {
        using (lease)
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                lease.Dispose();
            }

            await next(context);
        }
    }

    private static Task RefuseAsync(HttpContext context, TenantBusy busy)
    {
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(busy.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
        return ErrorResults.WriteAsync(context, StatusCodes.Status429TooManyRequests, "tenant_busy", "organization request budget exceeded");
    }

    private static bool Tracked(Caller caller) => caller switch
    {
        ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => true,
        AnonymousCaller or PeerCaller or ServiceCaller => false,
    };
}
