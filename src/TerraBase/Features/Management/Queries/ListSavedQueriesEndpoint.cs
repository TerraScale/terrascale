using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Queries;

/// <summary><c>GET …/projects/{project}/saved-queries</c>: lists the saved queries of a project.</summary>
internal sealed class ListSavedQueriesEndpoint(IManagementService management, IQueryService queries) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.SavedQueries);
        Name("listSavedQueries");
        Summary("Lists the saved queries of a project");
        Description(ManagementRoute.PageDescription + " " + ProjectApiRoute.IdentityDescription);
        Tags("Queries");
        Produces<ManagementPage<SavedQuery>>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.CheckPage(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            PageQuery page => await ManagementRoute.ProjectAsync(HttpContext, management, cancellationToken) switch
            {
                Project project => await queries.ListSavedAsync(project, page.After, page.Limit, cancellationToken) switch
                {
                    ManagementPage<SavedQuery> saved => TypedResults.Ok(saved),
                    Unavailable error => ErrorResponse.From(error),
                },
                NotFound => ErrorResponse.From(new NotFound()),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}
