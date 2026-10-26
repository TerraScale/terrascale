using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Regions;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

/// <summary>A database's placement as a create or update request names it (docs/cross-region.md).</summary>
/// <param name="Name">Database name, on create.</param>
/// <param name="HomeRegion">The region it is created and managed in; the called region when absent. It cannot change.</param>
/// <param name="ReplicaRegions">The other regions that hold a full copy; none when absent.</param>
/// <param name="AllRegions">Whether every serving region holds a full copy, including regions that join later; excludes <paramref name="ReplicaRegions"/>.</param>
public sealed record DatabaseRegionsRequest(string? Name, string? HomeRegion, IReadOnlyList<string>? ReplicaRegions, bool? AllRegions = null);

/// <summary>
/// Per-database region placement (docs/cross-region.md): the checks of a requested placement, and the <c>421 wrong_region</c>
/// a region outside a database's placement answers with, naming the regions that serve it.
/// </summary>
public static class DatabaseRegions
{
    internal const int MaxReplicas = 8;

    /// <summary>A placement of one region where writes are confirmed in another (decision 0013).</summary>
    internal static readonly Validation TwoRegions =
        new("a database is placed in at least two regions: replicaRegions names one or more regions besides the home region", "placement_needs_two_regions");

    /// <summary>A placement naming both every region and a list of them.</summary>
    internal static readonly Validation AllOrListed = new("allRegions and replicaRegions cannot both be set", "placement_conflict");

    /// <summary>A new database with no replica region named, where writes are confirmed in another and no other region serves.</summary>
    internal static readonly Unavailable NoOtherRegion =
        new("no region besides this one serves now, and a database is placed in at least two regions; name replicaRegions or retry", "remote_region_unavailable");

    internal readonly union HomeRegionCheck(Done, Misdirected, NotFound, Unavailable);

    /// <summary>Marks an endpoint that answers for databases placed elsewhere itself: reading, updating and deleting the record.</summary>
    public sealed class PlacementAware;

    /// <summary>
    /// Why <paramref name="replicas"/> cannot be the replica regions of a database at home in this region, or the accepted
    /// regions. A new database of a cell (<paramref name="created"/> where writes are confirmed in another region, decision
    /// 0013a) names one; an update may remove them all, and the database then takes no writes until one is added back.
    /// </summary>
    // Commits apply this check everywhere alike, so it accepts any well-formed region; requests also check Unserved.
    internal static Valid<IReadOnlyList<string>> Check(string home, IReadOnlyList<string> replicas, bool created) =>
        replicas.Count == 0 && created ? TwoRegions
        : replicas.Count > MaxReplicas ? new Validation("replicaRegions holds at most 8 regions")
        : !replicas.All(TerraBaseOptions.IsRegion) ? new Validation("replicaRegions must name regions")
        : replicas.Distinct(StringComparer.Ordinal).Count() != replicas.Count ? new Validation("replicaRegions must not repeat a region")
        : replicas.Contains(home) ? new Validation("replicaRegions must not name the home region")
        : replicas;

    /// <summary>
    /// <c>400 invalid_request</c> naming the serving regions when <paramref name="regions"/> names one outside
    /// <paramref name="serving"/> (the region catalog, and on update the database's current regions); otherwise null.
    /// </summary>
    internal static Validation? Unserved(string field, IEnumerable<string> regions, IReadOnlyList<string> serving) =>
        regions.FirstOrDefault(region => !serving.Contains(region, StringComparer.Ordinal)) is { } unknown
            ? new Validation($"{field} names {unknown}, a region TerraBase does not serve; choose from: {string.Join(", ", serving)}")
            : null;

    /// <summary>
    /// <c>421 wrong_region</c>: the database is served in <paramref name="regions"/>, its home <paramref name="home"/> first; clients
    /// resolve one of them through Atlas and retry there, and the edge replays to the nearest of them.
    /// </summary>
    internal static IResult WrongRegion(string home, IEnumerable<string> regions) => new Misdirected(home, string.Join(',', regions), homeOnly: false);

    /// <summary><c>421 wrong_region</c> for a request only the home <paramref name="home"/> answers: the edge replays it there.</summary>
    internal static IResult HomeRegion(string home, IEnumerable<string> regions) => new Misdirected(home, string.Join(',', regions), homeOnly: true);

    /// <summary>
    /// <c>421 wrong_region</c> when the Management route's database is managed in another region (its home), or
    /// a success case: branching, updating and deleting a database happen in its home region.
    /// </summary>
    internal static async ValueTask<HomeRegionCheck> ElsewhereAsync(HttpContext context, IManagementService management, string region)
    {
        var database = await management.GetDatabaseAsync(
            ManagementRoute.UserId(context), ManagementRoute.OrganizationId(context), ManagementRoute.Value(context, "project"), ManagementRoute.Value(context, "database"),
            context.RequestAborted).ConfigureAwait(false);
        return database switch
        {
            ManagedDatabase record when record.Home(region) != region => new Misdirected(record.Home(region), string.Join(',', record.Regions(region)), homeOnly: true),
            ManagedDatabase => new Done(),
            NotFound error => error,
            Unavailable error => error,
        };
    }

    /// <summary>
    /// Answers <c>421 wrong_region</c> for a database this region records but does not serve, on the client listener: data
    /// routes (an API key's organization) and database-scoped Management routes (the user's membership); members of one
    /// region serve its databases alike. Register it after authorization.
    /// </summary>
    public static WebApplication UseTerraBaseDatabasePlacement(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            if (Listeners.ArrivedOn(context) == TerraBaseListener.Client
                && context.GetEndpoint() is { } endpoint && (endpoint.Metadata.GetMetadata<PlacementAware>() is null || context.Features.Get<RequestedRegion>() is not null)
                && context.Request.RouteValues.TryGetValue("project", out var projectValue) && projectValue is string project
                && context.Request.RouteValues.TryGetValue("database", out var databaseValue) && databaseValue is string database)
            {
                var region = context.RequestServices.GetRequiredService<TerraBaseOptions>().Region;
                var record = await RecordAsync(context, project, database).ConfigureAwait(false);
                var pin = context.Features.Get<RequestedRegion>()?.Region;
                if (record is ManagedDatabase placed && (pin is not null || !placed.PlacedIn(region)))
                {
                    var regions = await RegionGuidance.RegionsAsync(placed, region, context.RequestServices.GetRequiredService<IRegionCatalog>(), context.RequestAborted).ConfigureAwait(false);
                    await new Misdirected(placed.Home(region), string.Join(',', regions), homeOnly: false, placed.AllRegions).ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }

                await (record switch
                {
                    NotFound when pin is not null => RegionGuidance.RefuseAsync(context, pin),
                    ManagedDatabase => next(context),
                    NotFound => next(context),
                    Unavailable error => ErrorResponse.From(error).ExecuteAsync(context),
                }).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        return app;
    }

    // The database a request names, when its caller reaches it: by id for a member of the route's organization, by name
    // for an API key that reaches it. Anything else is left to the endpoint, which answers 404.
    private static async ValueTask<FindDatabaseRecordResult> RecordAsync(HttpContext context, string project, string database)
    {
        var management = context.RequestServices.GetRequiredService<IManagementService>();
        if (context.Request.RouteValues.TryGetValue("organization", out var organization) && organization is string organizationId)
        {
            return context.User.Identity?.IsAuthenticated == true
                ? await management.GetDatabaseAsync(ManagementRoute.UserId(context), organizationId, project, database, context.RequestAborted).ConfigureAwait(false) switch
                {
                    ManagedDatabase record => record,
                    NotFound error => error,
                    Unavailable error => error,
                }
                : new NotFound();
        }

        if (context.Features.Get<ProjectAccess>() is { } access)
        {
            return await management.FindDatabaseRecordAsync(access.OrganizationId, project, database, context.RequestAborted).ConfigureAwait(false);
        }

        if (context.Features.Get<ApiKey>() is not { } key)
        {
            return new NotFound();
        }

        return await management.FindDatabaseRecordAsync(key.OrganizationId, project, database, context.RequestAborted).ConfigureAwait(false) switch
        {
            ManagedDatabase record when key.Reaches(new Tenancy.TenantContext(key.OrganizationId, Tenancy.PlanTier.Hobby, project, record.Id)) => record,
            ManagedDatabase => new NotFound(),
            NotFound error => error,
            Unavailable error => error,
        };
    }

    internal sealed class Misdirected(string home, string regions, bool homeOnly, bool allRegions = false) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            if (!allRegions)
            {
                httpContext.Response.Headers["X-TerraBase-Database-Home"] = home;
            }
            httpContext.Response.Headers["X-TerraBase-Database-Regions"] = regions;
            var choices = regions.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var pin = httpContext.Features.Get<RequestedRegion>()?.Region
                ?? (Listeners.IsRegionalHost(httpContext.Request.Host.Host, out var host) ? host
                    : httpContext.Request.Headers["X-TerraScale-Region-Pin"] is [{ } value] ? value : null);
            var target = pin is not null && choices.Contains(pin, StringComparer.Ordinal) && !homeOnly ? pin : allRegions ? choices[0] : home;
            if (pin is null || target == pin)
            {
                Listeners.ReplayIn(httpContext, pin ?? (homeOnly ? home : regions),
                    fallbackSelf: pin is not null && Listeners.IsRegionalHost(httpContext.Request.Host.Host, out _));
            }

            return RegionGuidance.RefuseAsync(httpContext, target, choices);
        }
    }
}
