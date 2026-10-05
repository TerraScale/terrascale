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
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(SavedQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (name, sql) = QueriesExtensions.Validate(request);
        if (await ManagementRoute.ProjectAsync(HttpContext, management, cancellationToken) is not { } project)
        {
            return TypedResults.NotFound();
        }

        return await queries.CreateSavedAsync(ManagementRoute.UserId(HttpContext), project, name, sql, cancellationToken) is { } query
            ? TypedResults.Created($"{ManagementRoute.ProjectPath(HttpContext, project)}/saved-queries/{query.Id}", query)
            : TypedResults.NotFound();
    }
}
