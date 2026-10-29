using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Queries;

/// <summary><c>DELETE …/saved-queries/{query}</c>: deletes a saved query.</summary>
internal sealed class DeleteSavedQueryEndpoint(IManagementService management, IQueryService queries) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(ProjectApiRoute.SavedQuery);
        Metadata(AnchoredRevocation.Instance);
        Name("deleteSavedQuery");
        Summary("Deletes a saved query");
        Description(ProjectApiRoute.IdentityDescription);
        Tags("Queries");
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.CheckWritableProjectAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            Project project => await queries.DeleteSavedAsync(ManagementRoute.UserId(HttpContext), project, ManagementRoute.Value(HttpContext, "query"), cancellationToken) switch
            {
                Done => TypedResults.NoContent(),
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}
