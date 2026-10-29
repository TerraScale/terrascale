using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>GET …/databases/{database}</c>: reads a database.</summary>
internal sealed class GetManagedDatabaseEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.Database);
        Name("getDatabase");
        Summary("Reads a database");
        Description(ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ManagedDatabaseBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await management.GetDatabaseAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.Value(HttpContext, "database"), cancellationToken) switch
        {
            ManagedDatabase value => TypedResults.Ok(ManagedDatabaseBody.From(value, Listeners.RegionOf(HttpContext))),
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
