using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class GetDatabasePurgeEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(ProjectApiRoute.Project + "/deleted-databases/{deletedDatabase}");
        Name("getDatabasePurge");
        Summary("Reads the durable database purge job");
        Description("Requires a project token without a database restriction and databases:write. Call the region that accepted deletion. Returns 200 with the durable purge job, including after completion. The job remains destroying until files, keys on every holder and backup objects are verified absent, then becomes complete with no outstanding work. Returns 404 when no purge job exists, 409 wrong_region outside the home region, or retryable 503 when unavailable. Reading the job does not start deletion.");
        Tags("Databases");
        Produces<DatabasePurgeBody>();
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), new ManagementUserRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await management.GetDatabasePurgeAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext),
            HttpContext.RouteValue("project"), HttpContext.RouteValue("deletedDatabase"), cancellationToken).ConfigureAwait(false) switch
        {
            DatabasePurgeBody job => TypedResults.Json(job, ManagementJsonContext.Default.DatabasePurgeBody),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
