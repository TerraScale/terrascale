using System.Diagnostics;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.HomeApply;
using TerraBase.Features.Regions;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class CreateManagedDatabaseEndpoint(
    IManagementService management, IProjectCatalog catalog, TerraBaseOptions options, IRegionCatalog regions, IHomeApply apply, EnrollmentFanout enrollment)
    : Endpoint<DatabaseRegionsRequest, IResult>
{
    internal const string OperationName = "createDatabase";

    public override void Configure()
    {
        Post(ProjectApiRoute.Databases);
        Name(OperationName);
        Summary("Creates a database in a project");
        Description("Creates a database in its home region. replicaRegions names up to 8 other regions that hold read replicas: they pull the home's pages and serve SQL reads, while writes go to the home. allRegions places a replica in every serving region, including regions that join later." + DatabaseTtl.Description + " " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ManagedDatabaseBody>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(DatabaseRegionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ManagementRoute.CheckDatabaseName(new NameRequest(request.Name)) switch
        {
            Validation error => ErrorResponse.From(error),
            string name => await CreateAsync(request, name, cancellationToken),
        };
    }

    private async ValueTask<IResult> CreateAsync(DatabaseRegionsRequest request, string name, CancellationToken cancellationToken)
    {
        if (DatabaseTtl.Invalid(request.TtlSeconds) is { } invalidTtl)
        {
            return ErrorResponse.From(invalidTtl);
        }

        var all = request.AllRegions == true;
        if (all && request.ReplicaRegions is not null)
        {
            return ErrorResponse.From(new Validation("send replicaRegions or allRegions, not both"));
        }

        var home = request.HomeRegion ?? options.Region;
        var serving = await regions.ServingAsync(cancellationToken).ConfigureAwait(false);
        if (TerraBaseOptions.IsRegion(home) && DatabaseRegions.Unserved("homeRegion", [home], serving) is { } unservedHome)
        {
            return ErrorResponse.From(unservedHome);
        }

        IReadOnlyList<string> replicas = all ? DatabaseRegions.AllBut(serving, home) : request.ReplicaRegions ?? [];
        if (home != options.Region)
        {
            return TerraBaseOptions.IsRegion(home)
                ? DatabaseRegions.HomeRegion(home, [home, .. replicas])
                : ErrorResponse.From(new Validation("homeRegion must name a region"));
        }

        if (!all)
        {
            var checkedRegions = DatabaseRegions.Check(replicas, home);
            if (checkedRegions is Validation invalid)
            {
                return ErrorResponse.From(invalid);
            }

            if (DatabaseRegions.Unserved("replicaRegions", replicas, serving) is { } unserved)
            {
                return ErrorResponse.From(unserved);
            }

            replicas = checkedRegions switch
            {
                IReadOnlyList<string> accepted => accepted,
                Validation => throw new UnreachableException(),
            };
        }

        var restricted = HttpContext.User.FindFirst("database")?.Value;
        if (restricted is not null && restricted != name)
        {
            return ErrorResponse.From(new NotFound("the database does not exist", "unknown_database"));
        }

        var actor = ManagementRoute.UserId(HttpContext);
        var project = ManagementRoute.Value(HttpContext, "project");
        if (HttpContext.User.FindFirst("project_id") is null)
        {
            return await catalog.ResolveProjectAsync(project, actor, createIn: null, cancellationToken).ConfigureAwait(false) switch
            {
                string mapped => await ManagementCreateAsync(mapped, name, replicas, all, request.TtlSeconds, cancellationToken).ConfigureAwait(false),
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            };
        }

        var owner = HttpContext.User.FindFirst("org_id")?.Value;
        if (owner is null)
        {
            return ErrorResponse.From(new NotFound("the database does not exist", "unknown_database"));
        }

        var firstDatabase = await catalog.ProjectOrganizationAsync(project, cancellationToken).ConfigureAwait(false) is not string;
        var resolved = await catalog.ResolveProjectAsync(project, actor, owner, cancellationToken).ConfigureAwait(false);
        if (resolved is not string organization)
        {
            return resolved switch
            {
                NotFound => ErrorResponse.From(new NotFound("the database does not exist", "unknown_database")),
                Unavailable error => ErrorResponse.From(error),
                string => throw new UnreachableException(),
            };
        }

        if (firstDatabase)
        {
            _ = enrollment.EnrollAsync(project, organization, CancellationToken.None);
        }

        HttpContext.Request.RouteValues["organization"] = organization;
        var grant = new ProjectAccess(actor, project, organization, restricted, DatabaseId: null);
        HttpContext.Features.Set(grant);
        IResult result = null!;
        await ProjectAccess.RunAsync(grant, async () => result = await CreateAsync(name, replicas, all, request.TtlSeconds, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<IResult> ManagementCreateAsync(
        string organization, string name, IReadOnlyList<string> replicas, bool all, long? ttlSeconds, CancellationToken cancellationToken)
    {
        HttpContext.Request.RouteValues["organization"] = organization;
        return await CreateAsync(name, replicas, all, ttlSeconds, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IResult> CreateAsync(string name, IReadOnlyList<string> replicas, bool all, long? ttlSeconds, CancellationToken cancellationToken)
    {
        var organization = ManagementRoute.OrganizationId(HttpContext);
        return await apply.PrepareReplicatedAsync(organization, options.Region, await management.CreateDatabaseAsync(
            ManagementRoute.UserId(HttpContext), organization, ManagementRoute.Value(HttpContext, "project"), name, cancellationToken, replicas, all, ttlSeconds),
            cancellationToken) switch
        {
            ManagedDatabase value => TypedResults.Created(ManagementRoute.CreatedDatabasePath(HttpContext, value), ManagedDatabaseBody.From(value, Listeners.RegionOf(HttpContext))),
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Forbidden error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
