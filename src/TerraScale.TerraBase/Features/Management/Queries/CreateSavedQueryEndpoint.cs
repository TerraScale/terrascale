using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Queries;

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
        return QueriesExtensions.Validate(request) switch
        {
            Validation error => ErrorResponse.From(error),
            SavedQueryRequest valid => await ManagementRoute.CheckWritableProjectAsync(HttpContext, management, cancellationToken) switch
            {
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Project project => await queries.CreateSavedAsync(ManagementRoute.UserId(HttpContext), project, valid.Name!, valid.Sql!, cancellationToken) switch
                {
                    SavedQuery query => TypedResults.Created($"{ManagementRoute.ProjectPath(HttpContext, project)}/saved-queries/{query.Id}", query),
                    NotFound error => ErrorResponse.From(error),
                },
            },
        };
    }
}
