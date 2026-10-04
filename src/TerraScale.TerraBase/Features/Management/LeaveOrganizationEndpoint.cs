using TerraScale.Endpoints;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>POST …/organizations/{organization}/leave</c>: the acting user leaves the organization.</summary>
public sealed class LeaveOrganizationEndpoint(IAccessService access) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Leave);
        Name("leaveOrganization");
        Summary("Leaves an organization");
        Description($"Needs member. 409 for the last owner, who must make another member an owner first. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status403Forbidden);
        Produces<string>(StatusCodes.Status404NotFound);
        Produces<string>(StatusCodes.Status409Conflict);
        RequireAuthorization(DashboardAuthenticationHandler.PolicyName);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.NoContent(await access.LeaveAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), cancellationToken));
}
