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
        Description("Requires a project token without a database restriction and databases:write, the same scope as restore. Call the region that accepted deletion. Starts durable destruction, ending the restore window. 202 includes the job state until files, keys on every holder and backup objects are verified absent; 204 confirms verified completion. Repeating an incomplete request returns its current job; a completed job returns 404 gone on a later DELETE. GET on this path reads the job, including after completion. Storage is released at purge, not at soft deletion. A call outside the home region returns 409 wrong_region.");
        Tags("Databases");
        Produces(StatusCodes.Status204NoContent);
        Produces<DatabasePurgeBody>(StatusCodes.Status202Accepted);
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
            DatabasePurgeBody { State: "complete" } => TypedResults.NoContent(),
            DatabasePurgeBody job => TypedResults.Json(job, ManagementJsonContext.Default.DatabasePurgeBody, statusCode: StatusCodes.Status202Accepted),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
