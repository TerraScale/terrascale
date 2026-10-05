using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Queries;

/// <summary><c>GET …/projects/{project}/saved-queries</c>: lists the saved queries of a project.</summary>
public sealed class ListSavedQueriesEndpoint(IManagementService management, IQueryService queries) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.SavedQueries);
        Name("listSavedQueries");
        Summary("Lists the saved queries of a project");
        Description(ManagementRoute.PageDescription + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagementPage<SavedQuery>>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (after, limit) = ManagementRoute.Page(HttpContext);
        return await ManagementRoute.ProjectAsync(HttpContext, management, cancellationToken) is { } project
            ? TypedResults.Ok(await queries.ListSavedAsync(project, after, limit, cancellationToken))
            : TypedResults.NotFound();
    }
}
