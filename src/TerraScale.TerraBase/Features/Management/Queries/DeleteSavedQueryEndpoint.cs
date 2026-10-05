using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Queries;

/// <summary><c>DELETE …/saved-queries/{query}</c>: deletes a saved query.</summary>
public sealed class DeleteSavedQueryEndpoint(IManagementService management, IQueryService queries) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(ManagementRoute.SavedQuery);
        Name("deleteSavedQuery");
        Summary("Deletes a saved query");
        Description(ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.ProjectAsync(HttpContext, management, cancellationToken) is { } project
        && await queries.DeleteSavedAsync(ManagementRoute.UserId(HttpContext), project, ManagementRoute.Value(HttpContext, "query"), cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
}
