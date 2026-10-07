using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>GET …/organizations/{organization}/projects</c>: lists the projects of an organization.</summary>
public sealed class ListProjectsEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
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
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (after, limit) = ManagementRoute.Page(HttpContext);
        return await management.ListProjectsAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), after, limit, cancellationToken) is { } page
            ? TypedResults.Ok(page)
            : TypedResults.NotFound();
    }
}
