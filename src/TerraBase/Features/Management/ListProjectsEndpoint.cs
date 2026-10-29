using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>GET …/organizations/{organization}/projects</c>: lists the projects of an organization.</summary>
internal sealed class ListProjectsEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Projects);
        Name("listProjects");
        Summary("Lists the projects of an organization");
        Description(ManagementRoute.PageDescription + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagementPage<Project>>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.CheckPage(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            PageQuery page => await management.ListProjectsAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), page.After, page.Limit, cancellationToken) switch
            {
                ManagementPage<Project> value => TypedResults.Ok(value),
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}
