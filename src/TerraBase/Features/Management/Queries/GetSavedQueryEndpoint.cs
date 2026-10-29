using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Queries;

/// <summary><c>GET …/saved-queries/{query}</c>: reads a saved query.</summary>
internal sealed class GetSavedQueryEndpoint(IManagementService management, IQueryService queries) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.SavedQuery);
        Name("getSavedQuery");
        Summary("Reads a saved query");
        Description(ProjectApiRoute.IdentityDescription);
        Tags("Queries");
        Produces<SavedQuery>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.ProjectAsync(HttpContext, management, cancellationToken) switch
        {
            Project project => await queries.GetSavedAsync(project, ManagementRoute.Value(HttpContext, "query"), cancellationToken) switch
            {
                SavedQuery query => TypedResults.Ok(query),
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
            NotFound => ErrorResponse.From(new NotFound()),
            Unavailable error => ErrorResponse.From(error),
        };
}
