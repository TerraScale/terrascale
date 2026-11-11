using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Regions;
using TerraBase.Features.Catalog;
using TerraBase.Features.HomeApply;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class UpdateManagedDatabaseEndpoint(IManagementService management, TerraBaseOptions options, IRegionCatalog regions, IHomeApply apply, RegionSetPlacement placement) : Endpoint<DatabaseRegionsRequest, IResult>
{
    public override void Configure()
    {
        Patch(ProjectApiRoute.Database);
        Metadata(AnchoredRevocation.Instance);
        Name("updateDatabase");
        Summary("Renames a database or sets its regions");
        Description("Sets regions to a nonempty set of distinct serving region ids. Response regions are sorted by id; their order carries no meaning. "
            + "If the set drops the current writer, TerraBase tries the remaining regions in region-id order through the existing writer move, "
            + "then removes the old writer after promotion. Repeat the same set to continue a change that answers an existing retryable refusal. "
            + "Transitional: homeRegion and replicaRegions, the existing response fields and move-home keep their behavior until category C23 of batch db4a5ba7 removes them once every consumer sends regions. "
            + "Send regions separately from homeRegion, replicaRegions and allRegions. A name change must be sent separately. " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ManagedDatabaseBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), new ManagementUserRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    public override async ValueTask<IResult> HandleAsync(DatabaseRegionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await management.GetDatabaseAsync(
            ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), HttpContext.RouteValue("project"),
            HttpContext.RouteValue("database"), cancellationToken) switch
        {
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Unavailable error => ErrorResponse.From(error),
            ManagedDatabase database => await UpdateAsync(request, database, cancellationToken),
        };
    }

    private async ValueTask<IResult> UpdateAsync(DatabaseRegionsRequest request, ManagedDatabase database, CancellationToken cancellationToken)
    {
        var (user, organization, project, id) = (ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), HttpContext.RouteValue("project"), HttpContext.RouteValue("database"));
        var home = database.Home(options.Region);
        if (home != options.Region)
        {
            return DatabaseRegions.HomeRegion(home, database.Regions(options.Region));
        }

        if (request.Name is not null)
        {
            return request.HomeRegion is not null || request.ReplicaRegions is not null || request.AllRegions is not null || request.HasRegions
                ? ErrorResponse.From(new Validation("name cannot be combined with placement fields"))
                : ManagementRoute.CheckDatabaseName(new NameRequest(request.Name)) switch
                {
                    Validation error => ErrorResponse.From(error),
                    string name => Answer(await management.RenameDatabaseAsync(user, organization, project, id, name, cancellationToken)),
                };
        }

        var serving = await regions.ServingAsync(cancellationToken).ConfigureAwait(false);
        if (request.HasRegions && DatabaseRegions.CheckSet(request, serving) is { } invalidSelection)
        {
            return ErrorResponse.From(invalidSelection);
        }

        if (request.Regions is { } selected)
        {
            return await placement.UpdateAsync(HttpContext, database, selected, cancellationToken).ConfigureAwait(false);
        }

        var all = request.AllRegions == true;
        Valid<IReadOnlyList<string>> checkedRegions = request.HomeRegion is { } requested && requested != home ? new Validation("homeRegion cannot change here: use move-home")
            : all && request.ReplicaRegions is not null ? new Validation("send replicaRegions or allRegions, not both")
            : all ? (Valid<IReadOnlyList<string>>)DatabaseRegions.AllBut(serving, home)
            : request.ReplicaRegions is not { } replicas ? new Validation("replicaRegions or allRegions is required")
            : DatabaseRegions.Check(replicas, home) is Validation invalid ? invalid
            : DatabaseRegions.Unserved("replicaRegions", replicas, [.. serving.Union(database.Regions(home), StringComparer.Ordinal)]) is { } unserved ? unserved
            : replicas;
        return checkedRegions switch
        {
            Validation error => ErrorResponse.From(error),
            IReadOnlyList<string> accepted => Answer(await apply.PrepareReplicatedAsync(organization, options.Region,
                await management.SetDatabaseRegionsAsync(user, organization, project, id, accepted, cancellationToken, all), cancellationToken)),
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
