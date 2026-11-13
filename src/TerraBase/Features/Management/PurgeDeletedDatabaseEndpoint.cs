using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class PurgeDeletedDatabaseEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Delete(ProjectApiRoute.Project + "/deleted-databases/{deletedDatabase}");
        Name("purgeDeletedDatabase");
        Summary("Permanently purges a deleted database and releases its storage");
        Description("Requires a project token without a database restriction and databases:write, the same scope as restore. Call the region that accepted deletion. Permanently deletes retained data and starts backup-key destruction immediately, ending the restore window. Storage is released at purge, not at soft deletion. A call outside the home region returns 409 wrong_region.");
        Tags("Databases");
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), new ManagementUserRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await management.PurgeDeletedDatabaseAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext),
            HttpContext.RouteValue("project"), HttpContext.RouteValue("deletedDatabase"), cancellationToken).ConfigureAwait(false) switch
        {
            Done => TypedResults.NoContent(),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
