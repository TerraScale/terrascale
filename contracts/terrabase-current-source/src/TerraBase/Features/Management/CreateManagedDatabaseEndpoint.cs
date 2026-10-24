using System.Diagnostics;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Replication;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>POST …/projects/{project}/databases</c>: creates an empty database in a project.</summary>
public sealed class CreateManagedDatabaseEndpoint(IManagementService management, IProjectCatalog catalog, TerraBaseOptions options, IRemoteDurability durability)
    : Endpoint<DatabaseRegionsRequest, IResult>
{
    internal const string OperationName = "createDatabase";

    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Databases);
        Name(OperationName);
        Summary("Creates a database in a project");
        Description("name: a lowercase letter, then lowercase letters, digits, _ or -, at most 63 characters; unique within the project. storageName addresses the database in the data API. homeRegion (optional, the called region by default) is the region the database is created and managed in: send the request to that region, any other answers 421 wrong_region with X-TerraBase-Database-Home. replicaRegions lists the other regions that hold a full copy and serve it too (docs/cross-region.md); each must register the project under the same ids. A database is placed in at least two regions (decision 0013): without replicaRegions it is placed in every other serving region (503 remote_region_unavailable when none serves), and an empty list answers 400 placement_needs_two_regions. allRegions: true (instead of replicaRegions, 400 placement_conflict with both) places a full copy in every serving region, including regions that join later; it is valid while one region serves." + " " + ProjectApiRoute.IdentityDescription);
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

    /// <inheritdoc />
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
        var all = request.AllRegions == true;
        if (all && request.ReplicaRegions is not null)
        {
            return ErrorResponse.From(DatabaseRegions.AllOrListed);
        }

        var home = request.HomeRegion ?? options.Region;
        IReadOnlyList<string> replicas = all ? [] : request.ReplicaRegions ?? await durability.OtherRegionsAsync(cancellationToken).ConfigureAwait(false);
        if (home != options.Region)
        {
            return TerraBaseOptions.IsRegion(home)
                ? DatabaseRegions.HomeRegion(home, [home, .. replicas])
                : ErrorResponse.From(new Validation("homeRegion must name a region"));
        }

        // Every region, now and later: valid with one region, and writes are confirmed once another serves (decision 0013a).
        if (!all)
        {
            if (request.ReplicaRegions is null && replicas.Count == 0 && durability.Confirms)
            {
                return ErrorResponse.From(DatabaseRegions.NoOtherRegion);
            }

            var checkedRegions = DatabaseRegions.Check(home, replicas, durability.Confirms);
            if (checkedRegions is Validation invalid)
            {
                return ErrorResponse.From(invalid);
            }

            replicas = checkedRegions switch
            {
                IReadOnlyList<string> accepted => accepted,
                Validation => throw new UnreachableException(),
            };
        }

        if (HttpContext.Features.Get<LegacyManagementRoute>() is not null)
        {
            return await CreateAsync(name, replicas, all, cancellationToken).ConfigureAwait(false);
        }

        var restricted = HttpContext.User.FindFirst("database")?.Value;
        if (restricted is not null && restricted != name)
        {
            return ErrorResponse.From(new NotFound("the database does not exist", "unknown_database"));
        }

        var actor = ManagementRoute.UserId(HttpContext);
        var project = ManagementRoute.Value(HttpContext, "project");
        // The billing identity: ts-auth signs the project's organization, and usage, plans and allowances key on it.
        var owner = HttpContext.User.FindFirst("org_id")?.Value;
        if (owner is null)
        {
            return ErrorResponse.From(new NotFound("the database does not exist", "unknown_database"));
        }

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

        HttpContext.Request.RouteValues["organization"] = organization;
        var grant = new ProjectAccess(actor, project, organization, restricted, DatabaseId: null);
        HttpContext.Features.Set(grant);
        IResult result = null!;
        await ProjectAccess.RunAsync(grant, async () => result = await CreateAsync(name, replicas, all, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<IResult> CreateAsync(string name, IReadOnlyList<string> replicas, bool all, CancellationToken cancellationToken)
    {
        return await management.CreateDatabaseAsync(
            ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), name, cancellationToken, replicas, all) switch
        {
            ManagedDatabase value => TypedResults.Created(ManagementRoute.CreatedDatabasePath(HttpContext, value), ManagedDatabaseBody.From(value, Listeners.RegionOf(HttpContext))),
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Forbidden error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
