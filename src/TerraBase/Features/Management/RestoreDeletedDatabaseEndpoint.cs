using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class RestoreDeletedDatabaseEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post(ProjectApiRoute.Project + "/deleted-databases/{deletedDatabase}/restore");
        Name("restoreDeletedDatabase");
        Summary("Restores a deleted database before its seven-day deadline");
        Description("Requires a project token without a database restriction. Deleted databases retain their data and backups until purgeAt. Restore by immutable ID in the region that accepted deletion; a reused name, exhausted database allowance, exceeded organization storage quota, or existing permanent purge returns 409. Retained storage counts against the quota until purge. Other regions are re-seeded after restore. Restoration clears the previous TTL.");
        Tags("Databases");
        Produces<ManagedDatabaseBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), new ManagementUserRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await management.RestoreDeletedDatabaseAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext),
            HttpContext.RouteValue("project"), HttpContext.RouteValue("deletedDatabase"), cancellationToken).ConfigureAwait(false) switch
        {
            ManagedDatabase result => TypedResults.Ok(ManagedDatabaseBody.From(result, Listeners.RegionOf(HttpContext))),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            Validation error => ErrorResponse.From(error),
        };
}
