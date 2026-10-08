using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>POST …/projects/{project}/databases</c>: creates an empty database in a project.</summary>
public sealed class CreateManagedDatabaseEndpoint(IManagementService management, IProjectCatalog catalog, TerraBaseOptions options) : Endpoint<DatabaseRegionsRequest, IResult>
{
    internal const string OperationName = "createDatabase";

    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Databases);
        Name(OperationName);
        Summary("Creates a database in a project");
        Description("name: a lowercase letter, then lowercase letters, digits, _ or -, at most 63 characters; unique within the project. storageName addresses the database in the data API. homeRegion (optional, the called region by default) is the region the database is created and managed in: send the request to that region, any other answers 421 wrong_region with X-TerraBase-Database-Home. replicaRegions (optional) lists the other regions that hold a full copy and serve it too (docs/cross-region.md); each must register the project under the same ids." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ManagedDatabaseBody>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(DatabaseRegionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = ManagementRoute.DatabaseName(new NameRequest(request.Name));
        var home = request.HomeRegion ?? options.Region;
        var replicas = request.ReplicaRegions ?? [];
        if (home != options.Region)
        {
            return options.Wan?.Remotes.ContainsKey(home) == true
                ? DatabaseRegions.WrongRegion(home, [home, .. replicas])
                : ErrorResponse.From(new Validation("homeRegion must name a region this deployment replicates with"));
        }

        if (DatabaseRegions.Check(options, home, replicas) is { } invalid)
        {
            return ErrorResponse.From(invalid);
        }

        if (HttpContext.Features.Get<LegacyManagementRoute>() is not null)
        {
            return await CreateAsync(name, replicas, cancellationToken).ConfigureAwait(false);
        }

        var restricted = HttpContext.User.FindFirst("database")?.Value;
        if (restricted is not null && restricted != name)
        {
            return ErrorResponse.From(new NotFound("the database does not exist", "unknown_database"));
        }

        var actor = ManagementRoute.UserId(HttpContext);
        var project = ManagementRoute.Value(HttpContext, "project");
        if (await catalog.ResolveProjectAsync(project, actor, create: true, cancellationToken).ConfigureAwait(false) is not { } organization)
        {
            return ErrorResponse.From(new NotFound("the database does not exist", "unknown_database"));
        }

        HttpContext.Request.RouteValues["organization"] = organization;
        var grant = new ProjectAccess(actor, project, organization, restricted, DatabaseId: null);
        HttpContext.Features.Set(grant);
        IResult result = null!;
        await ProjectAccess.RunAsync(grant, async () => result = await CreateAsync(name, replicas, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<IResult> CreateAsync(string name, IReadOnlyList<string> replicas, CancellationToken cancellationToken)
    {
        var result = await management.CreateDatabaseAsync(
            ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), name, cancellationToken, replicas);
        return result.Value is { } value
            ? TypedResults.Created(ManagementRoute.CreatedDatabasePath(HttpContext, value), ManagedDatabaseBody.From(value, Listeners.RegionOf(HttpContext)))
            : ManagementRoute.Error(result.Error);
    }
}
