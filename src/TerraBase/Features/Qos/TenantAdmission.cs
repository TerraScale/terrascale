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

        if (_duration.Enabled)
        {
            _duration.Record(Math.Max(0, time.GetElapsedTime(started).TotalSeconds), new("plan", tenant.Plan), new("tenant_bucket", tenant.Bucket));
        }
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

internal sealed record UpgradeAdmission(IDisposable Lease)
{
    public static void Release(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Features.Get<UpgradeAdmission>()?.Lease.Dispose();
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
            var directory = app.Services.GetRequiredService<TenantDirectory>();
            var admission = app.Services.GetRequiredService<TenantAdmission>();
            app.Use(async (context, next) =>
            {
                var target = await TargetAsync(context, context.Caller, directory);
                await (target switch
                {
                    Tenant tenant => EnterAsync(context, next, admission, tenant),
                    ForeignProject => EnterAsync(context, next, admission, Tenant.Unowned),
                    NoTenant => next(context),
                    BackendUnavailable unavailable => ErrorResults.Json(StatusCodes.Status503ServiceUnavailable, unavailable.Code, unavailable.Message,
                        retryAfter: TimeSpan.FromSeconds(1)).ExecuteAsync(context),
                });
            });
            return app;
        }
    }

    private static ValueTask<CallerTenantResolution> TargetAsync(HttpContext context, Caller caller, TenantDirectory directory)
    {
        var routeProject = context.Request.RouteValues["project"] as string;
        var callerScoped = routeProject is null && context.GetEndpoint()?.Metadata.GetMetadata<CallerTenantMetadata>() is not null;
        if (caller is PeerCaller or ServiceCaller || (routeProject is null && !callerScoped))
        {
            return ValueTask.FromResult<CallerTenantResolution>(NoTenant.Instance);
        }

        if (caller is AnonymousCaller)
        {
            return ValueTask.FromResult<CallerTenantResolution>(Tenant.Anonymous);
        }

        if (routeProject is { } reference)
        {
            return reference.Length == 26 && Ulid.TryParse(reference, out var projectId)
                ? directory.ForCallerProjectAsync(caller, projectId, context.RequestAborted)
                : ValueTask.FromResult<CallerTenantResolution>(Tenant.Unowned);
        }

        return caller switch
        {
            ApiKeyCaller key => TargetApiKeyAsync(directory, key.Key.OrganizationId, context.RequestAborted),
            ProjectCaller token => directory.ForCallerProjectAsync(caller, token.ProjectId, context.RequestAborted),
            EndUserCaller endUser => directory.ForCallerProjectAsync(caller, endUser.ProjectId, context.RequestAborted),
            AnonymousCaller or PeerCaller or ServiceCaller or UserCaller => ValueTask.FromResult<CallerTenantResolution>(NoTenant.Instance),
        };
    }

    private static async ValueTask<CallerTenantResolution> TargetApiKeyAsync(TenantDirectory directory, Ulid organizationId, CancellationToken cancellationToken) =>
        await directory.ForOrganizationAsync(organizationId, cancellationToken) switch
        {
            Tenant tenant => tenant,
            NoTenant none => none,
            BackendUnavailable unavailable => unavailable,
        };

    private static Task EnterAsync(HttpContext context, RequestDelegate next, TenantAdmission admission, Tenant tenant)
    {
        var admitted = admission.TryEnter(tenant);
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
                context.Features.Set(new UpgradeAdmission(lease));
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
}
