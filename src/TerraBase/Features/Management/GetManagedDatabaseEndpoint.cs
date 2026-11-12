using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class GetManagedDatabaseEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(ProjectApiRoute.Database);
        Name("getDatabase");
        Summary("Reads a database");
        Description("Answered by every region of the database's set; a region outside it answers 421 wrong_region with X-TerraBase-Database-Home. " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ManagedDatabaseBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), new ManagementUserRoute(), ListenerMetadata.Management, new DatabaseRegions.ReplicaReads());
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await management.GetDatabaseAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), HttpContext.RouteValue("project"), HttpContext.RouteValue("database"), cancellationToken) switch
        {
            ManagedDatabase value => TypedResults.Ok(ManagedDatabaseBody.From(value, Listeners.RegionOf(HttpContext))),
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
