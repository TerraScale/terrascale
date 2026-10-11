using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary>The role to give a member.</summary>
/// <param name="Role">owner, admin, member or viewer.</param>
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
        Description($"role: owner, admin, member or viewer. Needs admin, and owner when the member is or becomes an owner. 409 when it would demote the last owner. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<OrganizationMember>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(RoleRequest request, CancellationToken cancellationToken) =>
        (ManagementRoute.CheckUserId(ManagementRoute.Value(HttpContext, "user"), "user"), ManagementRoute.CheckRole(request.Role)) switch
        {
            (Validation error, _) => ErrorResponse.From(error),
            (_, Validation error) => ErrorResponse.From(error),
            (string member, OrganizationRole role) => await access.ChangeRoleAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), member, role, cancellationToken) switch
            {
                OrganizationMember value => TypedResults.Ok(value),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}
