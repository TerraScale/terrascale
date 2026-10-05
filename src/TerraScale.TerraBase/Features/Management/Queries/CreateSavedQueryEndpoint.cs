using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Queries;

/// <summary><c>POST …/projects/{project}/saved-queries</c>: saves a query in a project.</summary>
public sealed class CreateSavedQueryEndpoint(IManagementService management, IQueryService queries) : Endpoint<SavedQueryRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.SavedQueries);
        Name("createSavedQuery");
        Summary("Saves a query in a project");
        Description("name: 1 to 128 characters. sql: a statement of the SQL subset (see runSql)." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<SavedQuery>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(SavedQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (name, sql) = QueriesExtensions.Validate(request);
        var writable = await ManagementRoute.WritableProjectAsync(HttpContext, management, cancellationToken);
        if (writable.Value is not { } project)
        {
            return ManagementRoute.Error(writable.Error);
        }

        return await queries.CreateSavedAsync(ManagementRoute.UserId(HttpContext), project, name, sql, cancellationToken) is { } query
            ? TypedResults.Created($"{ManagementRoute.ProjectPath(HttpContext, project)}/saved-queries/{query.Id}", query)
            : TypedResults.NotFound();
    }
}
