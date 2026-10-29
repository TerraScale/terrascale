using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Queries;

/// <summary><c>PUT …/saved-queries/{query}</c>: replaces the name and SQL of a saved query.</summary>
internal sealed class UpdateSavedQueryEndpoint(IManagementService management, IQueryService queries) : Endpoint<SavedQueryRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(ProjectApiRoute.SavedQuery);
        Name("updateSavedQuery");
        Summary("Replaces a saved query");
        Description("Same rules as createSavedQuery; sets updatedAt." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Queries");
        Produces<SavedQuery>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(SavedQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return QueriesExtensions.Validate(request) switch
        {
            Validation error => ErrorResponse.From(error),
            SavedQueryRequest valid => await ManagementRoute.CheckWritableProjectAsync(HttpContext, management, cancellationToken) switch
            {
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
                Project project => await queries.UpdateSavedAsync(
                    ManagementRoute.UserId(HttpContext), project, ManagementRoute.Value(HttpContext, "query"), valid.Name!, valid.Sql!, cancellationToken) switch
                {
                    SavedQuery query => TypedResults.Ok(query),
                    NotFound error => ErrorResponse.From(error),
                    Unavailable error => ErrorResponse.From(error),
                },
            },
        };
    }
}
