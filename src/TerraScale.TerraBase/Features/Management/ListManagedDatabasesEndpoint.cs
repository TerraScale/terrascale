using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>GET …/projects/{project}/databases</c>: lists the databases of a project.</summary>
public sealed class ListManagedDatabasesEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
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
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (after, limit) = ManagementRoute.Page(HttpContext);
        return await management.ListDatabasesAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), after, limit, cancellationToken) is { } page
            ? TypedResults.Ok(new ManagementPage<ManagedDatabaseBody>(page.Items.Select(database => ManagedDatabaseBody.From(database, Listeners.RegionOf(HttpContext))).ToArray(), page.Next))
            : TypedResults.NotFound();
    }
}
