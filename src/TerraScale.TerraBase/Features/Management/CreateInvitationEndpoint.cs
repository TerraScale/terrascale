using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>An invitation to create.</summary>
/// <param name="Email">The address to invite.</param>
/// <param name="Role">Role on accepting; member when omitted.</param>
public sealed record InvitationRequest(string? Email, OrganizationRole? Role);

/// <summary><c>POST …/organizations/{organization}/invitations</c>: invites an email address.</summary>
public sealed class CreateInvitationEndpoint(IAccessService access) : Endpoint<InvitationRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Invitations);
        Name("createInvitation");
        Summary("Invites an email address to an organization");
        Description($"email: one address; role: owner, admin or member (default member). Needs admin, and owner to invite an owner. The token is returned only here and expires with the invitation after 7 days. 409 while an unexpired invitation for the email is pending; an expired one is replaced. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<CreatedInvitation>(StatusCodes.Status201Created);
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status403Forbidden);
        Produces<string>(StatusCodes.Status404NotFound);
        Produces<string>(StatusCodes.Status409Conflict);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(InvitationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await access.InviteAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Email(request.Email), ManagementRoute.Role(request.Role ?? OrganizationRole.Member), cancellationToken);
        return result.Value is { } value
            ? TypedResults.Created($"{HttpContext.Request.Path}/{value.Invitation.Id}", value)
            : ManagementRoute.Error(result.Error);
    }
}
