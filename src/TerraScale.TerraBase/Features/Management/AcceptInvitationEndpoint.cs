using TerraScale.Endpoints;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>An invitation acceptance.</summary>
/// <param name="Token">The token from the invitation link.</param>
/// <param name="Email">The accepting user's verified email address.</param>
public sealed record AcceptInvitationRequest(string? Token, string? Email);

/// <summary><c>POST /v1/management/invitations/accept</c>: the acting user joins through an invitation.</summary>
public sealed class AcceptInvitationEndpoint(IAccessService access) : Endpoint<AcceptInvitationRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.AcceptInvitation);
        Name("acceptInvitation");
        Summary("Accepts an invitation");
        Description($"Authenticated with the dashboard credential; {ManagementRoute.UserHeader} names the accepting user and the token names the organization. email is the user's verified address and must match the invitation (403). 404 for an unknown token, 409 when expired or already a member.");
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
    public override async ValueTask<IResult> HandleAsync(AcceptInvitationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Token))
        {
            return TypedResults.BadRequest("token is required");
        }

        return ManagementRoute.Ok(await access.AcceptInvitationAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.Email(request.Email), request.Token, cancellationToken));
    }
}
