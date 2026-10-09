using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

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
        Description($"email: one address; role: owner, admin, member or viewer (default member). Needs admin, and owner to invite an owner. The token is returned only here and expires with the invitation after 7 days. 409 while an unexpired invitation for the email is pending; an expired one stays in the history and a new one is created. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<CreatedInvitation>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(InvitationRequest request, CancellationToken cancellationToken) =>
        (ManagementRoute.CheckEmail(request.Email), ManagementRoute.CheckRole(request.Role ?? OrganizationRole.Member)) switch
        {
            (Validation error, _) => ErrorResponse.From(error),
            (_, Validation error) => ErrorResponse.From(error),
            (string email, OrganizationRole role) => await access.InviteAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), email, role, cancellationToken) switch
            {
                CreatedInvitation value => TypedResults.Created($"{HttpContext.Request.Path}/{value.Invitation.Id}", value),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}
