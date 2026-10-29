using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>PATCH …/databases/{database}</c>: sets a database's replica regions (docs/cross-region.md).</summary>
public sealed class UpdateManagedDatabaseEndpoint(IManagementService management, TerraBaseOptions options) : Endpoint<DatabaseRegionsRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Patch(ProjectApiRoute.Database);
        Metadata(AnchoredRevocation.Instance);
        Name("updateDatabase");
        Summary("Sets a database's replica regions");
        Description("Body {\"replicaRegions\":[…]}, the complete new list of regions besides the home region that hold a full copy and serve the database, or {\"allRegions\":true}: a full copy in every serving region, including regions that join later (400 placement_conflict with both). A replicaRegions list turns allRegions off. homeRegion may be sent but cannot change; a name cannot change. Sent to the database's home region; any other answers 421 wrong_region with X-TerraBase-Database-Home. An added region takes the database once it registers the project under the same ids; a removed region stops serving it and drops its copy." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ManagedDatabaseBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(DatabaseRegionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await management.GetDatabaseAsync(
            ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"),
            ManagementRoute.Value(HttpContext, "database"), cancellationToken) switch
        {
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Unavailable error => ErrorResponse.From(error),
            ManagedDatabase database => await UpdateAsync(request, database, cancellationToken),
        };
    }

    private async ValueTask<IResult> UpdateAsync(DatabaseRegionsRequest request, ManagedDatabase database, CancellationToken cancellationToken)
    {
        var (user, organization, project, id) = (ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.Value(HttpContext, "database"));
        var home = database.Home(options.Region);
        if (home != options.Region)
        {
            return DatabaseRegions.HomeRegion(home, database.Regions(options.Region));
        }

        Valid<IReadOnlyList<string>> checkedRegions = request.Name is not null ? new Validation("a database cannot be renamed")
            : request.HomeRegion is { } requested && requested != home ? new Validation("homeRegion cannot change")
            : request.AllRegions == true ? request.ReplicaRegions is null ? Array.Empty<string>() : DatabaseRegions.AllOrListed
            : request.ReplicaRegions is not { } replicas ? new Validation("replicaRegions or allRegions is required")
            : DatabaseRegions.Check(home, replicas, created: false);
        return checkedRegions switch
        {
            Validation error => ErrorResponse.From(error),
            IReadOnlyList<string> accepted => await management.SetDatabaseRegionsAsync(user, organization, project, id, accepted, cancellationToken, request.AllRegions == true) switch
            {
                ManagedDatabase value => TypedResults.Ok(ManagedDatabaseBody.From(value, options.Region)),
                NotFound error => ManagementRoute.DatabaseNotFound(error),
                Forbidden error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
    }
}
