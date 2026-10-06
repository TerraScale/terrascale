using TerraScale.Endpoints;
using TerraScale.TerraBase.Errors;
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
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.CheckWritableProjectAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Project project => await queries.DeleteSavedAsync(ManagementRoute.UserId(HttpContext), project, ManagementRoute.Value(HttpContext, "query"), cancellationToken) switch
            {
                Done => TypedResults.NoContent(),
                NotFound error => ErrorResponse.From(error),
            },
        };
}
