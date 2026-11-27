using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using TerraBase.Features.Callers;
using TerraBase.Hosting;

namespace TerraBase.Features.Qos;

internal readonly union TenantAdmissionOutcome(TenantAdmission.TenantLease, TenantBusy);

internal sealed class TenantAdmission(TimeProvider time, IMeterFactory meters)
{
    private const int MaxTenants = 4096;

    private readonly ConcurrentDictionary<Ulid, Lane> _lanes = new();

    private readonly Lock _evictGate = new();

    private readonly Counter<long> _rejected = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.qos.rejected");

    private readonly Histogram<double> _duration = meters.Create(Telemetry.MeterName).CreateHistogram<double>("terrabase.qos.request.duration", "s");

    private int _laneCount;

    public TenantAdmissionOutcome TryEnter(Tenant tenant) =>
        TryEnter(tenant, out var lease, out var busy)
            ? new TenantAdmissionOutcome(lease)
            : new TenantAdmissionOutcome(busy);

    internal bool TryEnter(Tenant tenant, out TenantLease lease, [NotNullWhen(false)] out TenantBusy? busy)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var policy = tenant.Policy;
        var organizationId = tenant.OrganizationId;
        while (true)
        {
            if (!_lanes.TryGetValue(organizationId, out var lane))
            {
                lane = GetOrCreateLane(organizationId, tenant, policy);
                if (lane is null)
                {
                    lease = default;
                    busy = Reject(tenant, "capacity", TimeSpan.FromSeconds(1));
                    return false;
                }
            }

            lock (lane.Gate)
            {
                if (lane.Evicted)
                {
                    continue;
                }

                var now = time.GetTimestamp();
                if (!string.Equals(lane.Plan, tenant.Plan, StringComparison.Ordinal))
                {
                    lane.Tokens = policy.Burst;
                    lane.Plan = tenant.Plan;
                }

                lane.Tokens = Math.Min(policy.Burst, lane.Tokens + (Math.Max(0, time.GetElapsedTime(lane.LastSeen, now).TotalSeconds) * policy.RequestsPerSecond));
                lane.LastSeen = Math.Max(lane.LastSeen, now);
                if (lane.Active >= policy.Concurrency)
                {
                    lease = default;
                    busy = Reject(tenant, "concurrency", TimeSpan.FromSeconds(1));
                    return false;
                }

                if (lane.Tokens < 1)
                {
                    lease = default;
                    busy = Reject(tenant, "rate", TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling((1 - lane.Tokens) / policy.RequestsPerSecond))));
                    return false;
                }

                lane.Tokens--;
                lane.Active++;
                lease = new TenantLease(this, lane, tenant, now);
                busy = null;
                return true;
            }
        }
    }

    private Lane? GetOrCreateLane(Ulid organizationId, Tenant tenant, QosPolicy policy)
    {
        lock (_evictGate)
        {
            if (_lanes.TryGetValue(organizationId, out var existing))
            {
                return existing;
            }

            var now = time.GetTimestamp();
            if (_laneCount >= MaxTenants)
            {
                EvictLocked(now);
                if (_laneCount >= MaxTenants)
                {
                    return null;
                }
            }

            var created = new Lane(policy.Burst, now, tenant.Plan);
            if (_lanes.TryAdd(organizationId, created))
            {
                _laneCount++;
            }

            return created;
        }
    }

    private void EvictLocked(long now)
    {
        foreach (var (organization, lane) in _lanes)
        {
            lock (lane.Gate)
            {
                if (lane.Evicted || lane.Active != 0)
                {
                    continue;
                }

                var policy = QosPolicy.For(lane.Plan);
                if (lane.Tokens + (Math.Max(0, time.GetElapsedTime(lane.LastSeen, now).TotalSeconds) * policy.RequestsPerSecond) < policy.Burst)
                {
                    continue;
                }

                if (_lanes.TryRemove(KeyValuePair.Create(organization, lane)))
                {
                    lane.Evicted = true;
                    _laneCount--;
                }
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
        lock (lane.Gate)
        {
            lane.Active--;
        }

        if (_duration.Enabled)
        {
            _duration.Record(Math.Max(0, time.GetElapsedTime(started).TotalSeconds), new("plan", tenant.Plan), new("tenant_bucket", tenant.Bucket));
        }
    }

    internal sealed class Lane(double tokens, long lastSeen, string plan)
    {
        public Lock Gate { get; } = new();

        public double Tokens { get; set; } = tokens;

        public long LastSeen { get; set; } = lastSeen;

        public int Active { get; set; }

        public string Plan { get; set; } = plan;

        public bool Evicted { get; set; }
    }

    internal readonly struct TenantLease(TenantAdmission admission, Lane lane, Tenant tenant, long started) : IDisposable
    {
        public void Dispose()
        {
            if (admission is not null)
            {
                admission.Leave(lane, tenant, started);
            }
        }

        internal SharedLease ToUpgradeLease() => new(admission, lane, tenant, started);
    }

    internal sealed class SharedLease(TenantAdmission admission, Lane lane, Tenant tenant, long started) : IDisposable
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
            app.Use((context, next) =>
                TryResolveTarget(context, context.Caller, directory, out var target)
                    ? DispatchAsync(context, next, admission, target)
                    : ResolveAndDispatchAsync(context, next, directory, admission));
            return app;
        }
    }

    private static bool TryResolveTarget(HttpContext context, Caller caller, TenantDirectory directory, out CallerTenantResolution resolved)
    {
        var routeProject = context.Request.RouteValues["project"] as string;
        var callerScoped = routeProject is null && context.GetEndpoint()?.Metadata.GetMetadata<CallerTenantMetadata>() is not null;
        if (caller is PeerCaller or ServiceCaller || (routeProject is null && !callerScoped))
        {
            resolved = NoTenant.Instance;
            return true;
        }

        if (caller is AnonymousCaller)
        {
            resolved = Tenant.Anonymous;
            return true;
        }

        if (routeProject is { } reference)
        {
            if (reference.Length == 26 && Ulid.TryParse(reference, out var projectId))
            {
                return directory.TryForCallerProject(caller, projectId, out resolved);
            }

            resolved = Tenant.Unowned;
            return true;
        }

        return caller switch
        {
            ApiKeyCaller key => TryResolveApiKey(directory, key.Key.OrganizationId, out resolved),
            ProjectCaller token => directory.TryForCallerProject(caller, token.ProjectId, out resolved),
            EndUserCaller endUser => directory.TryForCallerProject(caller, endUser.ProjectId, out resolved),
            AnonymousCaller or PeerCaller or ServiceCaller or UserCaller => Resolved(NoTenant.Instance, out resolved),
        };
    }

    private static bool TryResolveApiKey(TenantDirectory directory, Ulid organizationId, out CallerTenantResolution resolved)
    {
        if (directory.TryForOrganization(organizationId, out var organization))
        {
            resolved = organization switch
            {
                Tenant tenant => tenant,
                NoTenant none => none,
                BackendUnavailable unavailable => unavailable,
            };
            return true;
        }

        resolved = default;
        return false;
    }

    private static bool Resolved(CallerTenantResolution value, out CallerTenantResolution resolved)
    {
        resolved = value;
        return true;
    }

    private static async Task ResolveAndDispatchAsync(HttpContext context, RequestDelegate next, TenantDirectory directory, TenantAdmission admission)
    {
        var target = await TargetAsync(context, context.Caller, directory);
        await DispatchAsync(context, next, admission, target);
    }

    private static Task DispatchAsync(HttpContext context, RequestDelegate next, TenantAdmission admission, CallerTenantResolution target) =>
        target switch
        {
            Tenant tenant => EnterAsync(context, next, admission, tenant),
            ForeignProject => EnterAsync(context, next, admission, Tenant.Unowned),
            NoTenant => next(context),
            BackendUnavailable unavailable => ErrorResults.Json(StatusCodes.Status503ServiceUnavailable, unavailable.Code, unavailable.Message,
                retryAfter: TimeSpan.FromSeconds(1)).ExecuteAsync(context),
        };

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
        if (!admission.TryEnter(tenant, out var lease, out var busy))
        {
            return RefuseAsync(context, busy);
        }

        return context.WebSockets.IsWebSocketRequest
            ? AdmitUpgradeAsync(context, next, lease.ToUpgradeLease())
            : AdmitHttpAsync(context, next, lease);
    }

    private static async Task AdmitHttpAsync(HttpContext context, RequestDelegate next, TenantAdmission.TenantLease lease)
    {
        using (lease)
        {
            await next(context);
        }
    }

    private static async Task AdmitUpgradeAsync(HttpContext context, RequestDelegate next, TenantAdmission.SharedLease lease)
    {
        using (lease)
        {
            context.Features.Set(new UpgradeAdmission(lease));
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
