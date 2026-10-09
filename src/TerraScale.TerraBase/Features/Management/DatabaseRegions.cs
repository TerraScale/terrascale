using Microsoft.AspNetCore.Authorization;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

/// <summary>A database's placement as a create or update request names it (docs/cross-region.md).</summary>
/// <param name="Name">Database name, on create.</param>
/// <param name="HomeRegion">The region it is created and managed in; the called region when absent. It cannot change.</param>
/// <param name="ReplicaRegions">The other regions that hold a full copy; none when absent.</param>
public sealed record DatabaseRegionsRequest(string? Name, string? HomeRegion, IReadOnlyList<string>? ReplicaRegions);

/// <summary>
/// Per-database region placement (docs/cross-region.md): the checks of a requested placement, and the <c>421 wrong_region</c>
/// a region outside a database's placement answers with, naming the regions that serve it.
/// </summary>
public static class DatabaseRegions
{
    internal const int MaxReplicas = 8;

    internal readonly union HomeRegionCheck(Done, Misdirected, NotFound, Unavailable);

    /// <summary>Marks an endpoint that answers for databases placed elsewhere itself: reading, updating and deleting the record.</summary>
    public sealed class PlacementAware;

    /// <summary>Why <paramref name="replicas"/> cannot be the replica regions of a database at home in this region, or the accepted regions.</summary>
    internal static Valid<IReadOnlyList<string>> Check(TerraBaseOptions options, string home, IReadOnlyList<string> replicas) =>
        replicas.Count > MaxReplicas ? new Validation("replicaRegions holds at most 8 regions")
        : replicas.Any(region => region != options.Region && options.Wan?.Remotes.ContainsKey(region) != true)
            ? new Validation("replicaRegions must name regions this deployment replicates with")
        : replicas.Distinct(StringComparer.Ordinal).Count() != replicas.Count ? new Validation("replicaRegions must not repeat a region")
        : replicas.Contains(home) ? new Validation("replicaRegions must not name the home region")
        : replicas;

    /// <summary>
    /// <c>421 wrong_region</c>: the database is served in <paramref name="regions"/>, its home <paramref name="home"/> first; clients
    /// resolve one of them through Atlas and retry there.
    /// </summary>
    internal static IResult WrongRegion(string home, IEnumerable<string> regions) => new Misdirected(home, string.Join(',', regions));

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
            ManagedDatabase record when record.Home(region) != region => new Misdirected(record.Home(region), string.Join(',', record.Regions(region))),
            ManagedDatabase => new Done(),
            NotFound error => error,
            Unavailable error => error,
        };
    }

    /// <summary>
    /// Answers <c>421 wrong_region</c> for a database this region records but does not serve, on the client listener: data
    /// routes (an API key's organization) and database-scoped Management routes (the user's membership). Only a node that
    /// replicates across regions (<c>wan-regions</c>) refuses; members of one region serve its databases alike. Register it
    /// after authorization.
    /// </summary>
    public static WebApplication UseTerraBaseDatabasePlacement(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetRequiredService<TerraBaseOptions>().Wan is null)
        {
            return app;
        }

        app.Use(async (context, next) =>
        {
            if (Listeners.ArrivedOn(context) == TerraBaseListener.Client
                && context.GetEndpoint() is { } endpoint && endpoint.Metadata.GetMetadata<PlacementAware>() is null
                && context.Request.RouteValues.TryGetValue("project", out var projectValue) && projectValue is string project
                && context.Request.RouteValues.TryGetValue("database", out var databaseValue) && databaseValue is string database)
            {
                var region = context.RequestServices.GetRequiredService<TerraBaseOptions>().Region;
                await (await RecordAsync(context, project, database).ConfigureAwait(false) switch
                {
                    ManagedDatabase record when !record.PlacedIn(region) => WrongRegion(record.Home(region), record.Regions(region)).ExecuteAsync(context),
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
        // Subscriptions end at signed expiration without authentication skew; their endpoint returns 401.
        if (context.GetEndpoint()?.Metadata.GetMetadata<IAuthorizeData>()?.Policy == UserAuthExtensions.SubscriptionPolicy
            && context.Features.Get<ProjectToken>() is { } token
            && token.ExpiresAt <= context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow())
        {
            return new NotFound();
        }

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

    internal sealed class Misdirected(string home, string regions) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers["X-TerraBase-Database-Home"] = home;
            httpContext.Response.Headers["X-TerraBase-Database-Regions"] = regions;
            return BadRequests.WriteAsync(httpContext, StatusCodes.Status421MisdirectedRequest, "wrong_region", $"the database is served in {home}", op: null);
        }
    }
}
