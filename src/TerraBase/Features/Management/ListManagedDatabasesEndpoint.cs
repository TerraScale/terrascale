using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>GET …/projects/{project}/databases</c>: lists the databases of a project.</summary>
internal sealed class ListManagedDatabasesEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.Databases);
        Name("listDatabases");
        Summary("Lists the databases of a project");
        Description(ManagementRoute.PageDescription + " " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ManagementPage<ManagedDatabaseBody>>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.CheckPage(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            PageQuery page => await management.ListDatabasesAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), page.After, page.Limit, cancellationToken) switch
            {
                ManagementPage<ManagedDatabase> value => TypedResults.Ok(new ManagementPage<ManagedDatabaseBody>([.. value.Items.Select(database => ManagedDatabaseBody.From(database, Listeners.RegionOf(HttpContext)))], value.Next)),
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}
