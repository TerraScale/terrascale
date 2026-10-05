using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Queries;

/// <summary><c>GET …/saved-queries/{query}</c>: reads a saved query.</summary>
public sealed class GetSavedQueryEndpoint(IManagementService management, IQueryService queries) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.SavedQuery);
        Name("getSavedQuery");
        Summary("Reads a saved query");
        Description(ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<SavedQuery>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.ProjectAsync(HttpContext, management, cancellationToken) is { } project
        && await queries.GetSavedAsync(project, ManagementRoute.Value(HttpContext, "query"), cancellationToken) is { } query
            ? TypedResults.Ok(query)
            : TypedResults.NotFound();
}
