using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class ListDeletedDatabasesEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(ProjectApiRoute.Project + "/deleted-databases");
        Name("listDeletedDatabases");
        Summary("Lists databases in the seven-day deleted state");
        Description("Requires a project token without a database restriction. Each deleted item includes deletedAt and purgeAt. Data, backups and keys are retained until purgeAt unless explicitly purged or the project or account is deleted. Retained storage counts against the organization quota until purge. Call the region that accepted deletion. " + ManagementRoute.PageDescription);
        Tags("Databases");
        Produces<ManagementPage<DeletedDatabaseBody>>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), new ManagementUserRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.CheckPage(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            PageQuery page => await management.ListDeletedDatabasesAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext),
                HttpContext.RouteValue("project"), page.After, page.Limit, cancellationToken).ConfigureAwait(false) switch
            {
                ManagementPage<DeletedDatabaseBody> result => TypedResults.Ok(result),
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}
