using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
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
        Description(ManagementRoute.PageDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagementPage<ManagedDatabase>>();
        Produces<string>(StatusCodes.Status404NotFound);
        RequireAuthorization(ApiKeyScopes.Management);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (after, limit) = ManagementRoute.Page(HttpContext);
        return await management.ListDatabasesAsync(ManagementRoute.Value(HttpContext, "organization"), ManagementRoute.Value(HttpContext, "project"), after, limit, cancellationToken) is { } page
            ? TypedResults.Ok(page)
            : TypedResults.NotFound();
    }
}
