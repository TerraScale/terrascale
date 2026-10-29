using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

public sealed record DatabaseRegionsRequest(string? Name, string? HomeRegion, IReadOnlyList<string>? ReplicaRegions, bool? AllRegions = null);

public static class DatabaseRegions
{
    internal const int MaxReplicas = 8;

    internal readonly union HomeRegionCheck(Done, Misdirected, NotFound, Unavailable);

    public sealed class PlacementAware;

    internal static Valid<IReadOnlyList<string>> Check(IReadOnlyList<string> replicas) =>
        replicas.Count > 0 ? new Validation("databases serve only in their home region until replicas are available", "replicas_unavailable")
        : replicas;

    internal static Validation? Unserved(string field, IEnumerable<string> regions, IReadOnlyList<string> serving) =>
        regions.FirstOrDefault(region => !serving.Contains(region, StringComparer.Ordinal)) is { } unknown
            ? new Validation($"{field} names {unknown}, a region TerraBase does not serve; choose from: {string.Join(", ", serving)}")
            : null;

    internal static IResult WrongRegion(string home, IEnumerable<string> regions) => new Misdirected(home, string.Join(',', regions), homeOnly: false);

    internal static IResult HomeRegion(string home, IEnumerable<string> regions) => new Misdirected(home, string.Join(',', regions), homeOnly: true);

    internal static IResult RecoveryRegion(string region, ManagedDatabase database) =>
        new Misdirected(database.Home(region), string.Join(',', database.Regions(region)), homeOnly: true, replayRegion: region);

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

    public static WebApplication UseTerraBaseDatabasePlacement(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
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

    internal sealed class Misdirected(string home, string regions, bool homeOnly, string? replayRegion = null) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers["X-TerraBase-Database-Home"] = home;
            httpContext.Response.Headers["X-TerraBase-Database-Regions"] = regions;
            Listeners.ReplayIn(httpContext, replayRegion ?? (homeOnly ? home : regions));
            return BadRequests.WriteAsync(httpContext, StatusCodes.Status421MisdirectedRequest, "wrong_region", $"the database is served in {home}", op: null);
        }
    }
}
