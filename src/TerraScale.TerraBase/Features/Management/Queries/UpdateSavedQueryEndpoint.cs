using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Queries;

/// <summary><c>PUT …/saved-queries/{query}</c>: replaces the name and SQL of a saved query.</summary>
public sealed class UpdateSavedQueryEndpoint(IManagementService management, IQueryService queries) : Endpoint<SavedQueryRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(ManagementRoute.SavedQuery);
        Name("updateSavedQuery");
        Summary("Replaces a saved query");
        Description("Same rules as createSavedQuery; sets updatedAt." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<SavedQuery>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(SavedQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (name, sql) = QueriesExtensions.Validate(request);
        return await ManagementRoute.ProjectAsync(HttpContext, management, cancellationToken) is { } project
            && await queries.UpdateSavedAsync(ManagementRoute.UserId(HttpContext), project, ManagementRoute.Value(HttpContext, "query"), name, sql, cancellationToken) is { } query
                ? TypedResults.Ok(query)
                : TypedResults.NotFound();
    }
}
