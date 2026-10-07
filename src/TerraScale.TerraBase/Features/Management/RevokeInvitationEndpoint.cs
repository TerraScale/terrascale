using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>DELETE …/organizations/{organization}/invitations/{invitation}</c>: revokes an invitation.</summary>
public sealed class RevokeInvitationEndpoint(IAccessService access) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(ManagementRoute.Invitations + "/{invitation}");
        Name("revokeInvitation");
        Summary("Revokes an invitation");
        Description($"Needs admin. The invitation becomes revoked and its token stops working; 409 when it is no longer pending. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.NoContent(await access.RevokeInvitationAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "invitation"), cancellationToken));
}
