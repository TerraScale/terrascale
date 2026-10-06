using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>GET …/projects/{project}/databases</c>: lists the databases of a project.</summary>
public sealed class ListManagedDatabasesEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Databases);
        Name("listManagedDatabases");
        Summary("Lists the databases of a project");
        Description(ManagementRoute.PageDescription + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagementPage<ManagedDatabaseBody>>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
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
