using TerraScale.Endpoints;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>The role to give a member.</summary>
/// <param name="Role">owner, admin or member.</param>
public sealed record RoleRequest(OrganizationRole? Role);

/// <summary><c>PUT …/organizations/{organization}/members/{user}/role</c>: changes a member's role.</summary>
public sealed class ChangeMemberRoleEndpoint(IAccessService access) : Endpoint<RoleRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(ManagementRoute.Member + "/role");
        Name("changeMemberRole");
        Summary("Changes a member's role");
        Description($"role: owner, admin or member. Needs admin, and owner when the member is or becomes an owner. 409 when it would demote the last owner. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<OrganizationMember>();
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status403Forbidden);
        Produces<string>(StatusCodes.Status404NotFound);
        Produces<string>(StatusCodes.Status409Conflict);
        RequireAuthorization(DashboardAuthenticationHandler.PolicyName);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(RoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ManagementRoute.Ok(await access.ChangeRoleAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.MemberId(HttpContext), ManagementRoute.Role(request.Role), cancellationToken));
    }
}
