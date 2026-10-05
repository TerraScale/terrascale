using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>GET …/organizations/{organization}/invitations</c>: lists pending invitations.</summary>
public sealed class ListInvitationsEndpoint(IAccessService access) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Invitations);
        Name("listInvitations");
        Summary("Lists the pending invitations of an organization");
        Description($"Needs admin. Expired invitations are listed until replaced or revoked; compare expiresAt. Query: after (id cursor), limit (1 to 100, default 20); creation order. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<ManagementPage<Invitation>>();
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status403Forbidden);
        Produces<string>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (after, limit) = ManagementRoute.Page(HttpContext);
        return ManagementRoute.Ok(await access.ListInvitationsAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), after, limit, cancellationToken));
    }
}
