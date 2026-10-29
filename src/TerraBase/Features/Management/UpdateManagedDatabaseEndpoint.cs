using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Regions;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

public sealed class UpdateManagedDatabaseEndpoint(IManagementService management, TerraBaseOptions options, IRegionCatalog regions) : Endpoint<DatabaseRegionsRequest, IResult>
{
    public override void Configure()
    {
        Patch(ProjectApiRoute.Database);
        Metadata(AnchoredRevocation.Instance);
        Name("updateDatabase");
        Summary("Renames a database or confirms its home-region placement");
        Description("Renames a database or confirms its unchanged home-region placement. Replica placement is unavailable. " + ProjectApiRoute.IdentityDescription);
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

        if (request.AllRegions == true || request.ReplicaRegions is { Count: > 0 })
        {
            return ErrorResponse.From(new Validation("databases serve only in their home region until replicas are available", "replicas_unavailable"));
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
            : request.ReplicaRegions is not { } replicas ? new Validation("replicaRegions or allRegions is required")
            : DatabaseRegions.Check(replicas) is Validation invalid ? invalid
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
