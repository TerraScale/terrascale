using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Platform;
using TerraBase.Features.Regions;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>PATCH …/databases/{database}</c>: renames a database or sets its replica regions (docs/cross-region.md).</summary>
internal sealed class UpdateManagedDatabaseEndpoint(IManagementService management, TerraBaseOptions options, IRegionCatalog regions) : Endpoint<DatabaseRegionsRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Patch(ProjectApiRoute.Database);
        Metadata(AnchoredRevocation.Instance);
        Name("updateDatabase");
        Summary("Renames a database or sets its replica regions");
        Description("Body {\"replicaRegions\":[…]}, the complete new list of regions besides the home region that hold a full copy and serve the database, or {\"allRegions\":true}: a full copy in every serving region, including regions that join later (400 placement_conflict with both). A replicaRegions list turns allRegions off; it names regions of GET /v1/regions or regions the database is already placed in (400 invalid_request naming the serving regions otherwise), so a removed region can be added back. A platform database (ts-auth, ts-dashboard) stays in every region and keeps its name: anything but {\"allRegions\":true} answers 409 platform_placement_locked. homeRegion may be sent but cannot change. Alternatively {\"name\":\"new-name\"} alone renames the database (400 invalid_request with placement fields or an invalid name; 409 name_taken when this region knows another database of the project under the name); its ULID, data, placement, backups and keys stay, and requests by ULID keep working. Names are unique per project after convergence; concurrent cross-region claims resolve to one winner, the smallest ULID, and the other database keeps everything under a conflict name (docs/terrabase/database-rename.md). Sent to the database's home region; any other answers 421 wrong_region with X-TerraBase-Database-Home. An added region takes the database once it registers the project under the same ids; a removed region stops serving it and drops its copy." + " " + ProjectApiRoute.IdentityDescription);
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

        if (PlatformDatabases.IsPlatform(organization) && (request.AllRegions != true || request.ReplicaRegions is not null))
        {
            return ErrorResponse.From(ManagementErrors.PlatformPlacementLocked);
        }

        if (request.Name is not null)
        {
            return request.HomeRegion is not null || request.ReplicaRegions is not null || request.AllRegions is not null
                ? ErrorResponse.From(new Validation("name cannot be combined with placement fields"))
                : ManagementRoute.CheckDatabaseName(new NameRequest(request.Name)) switch
                {
                    Validation error => ErrorResponse.From(error),
                    string name => Answer(await management.RenameDatabaseAsync(user, organization, project, id, name, cancellationToken)),
                };
        }

        Valid<IReadOnlyList<string>> checkedRegions = request.HomeRegion is { } requested && requested != home ? new Validation("homeRegion cannot change")
            : request.AllRegions == true ? request.ReplicaRegions is null ? Array.Empty<string>() : DatabaseRegions.AllOrListed
            : request.ReplicaRegions is not { } replicas ? new Validation("replicaRegions or allRegions is required")
            : DatabaseRegions.Check(home, replicas, created: false) is Validation invalid ? invalid
            : DatabaseRegions.Unserved("replicaRegions", replicas, [.. (await regions.ServingAsync(cancellationToken).ConfigureAwait(false)).Union(database.Regions(home), StringComparer.Ordinal)]) is { } unserved ? unserved
            : replicas;
        return checkedRegions switch
        {
            Validation error => ErrorResponse.From(error),
            IReadOnlyList<string> accepted => Answer(await management.SetDatabaseRegionsAsync(user, organization, project, id, accepted, cancellationToken, request.AllRegions == true)),
        };
    }

    private IResult Answer(SetDatabaseRegionsResult result) => result switch
    {
        ManagedDatabase value => TypedResults.Ok(ManagedDatabaseBody.From(value, options.Region)),
        NotFound error => ManagementRoute.DatabaseNotFound(error),
        Forbidden error => ErrorResponse.From(error),
        Conflict error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
    };
}
