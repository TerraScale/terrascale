using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>An invitation acceptance.</summary>
/// <param name="Token">The token from the invitation link.</param>
public sealed record AcceptInvitationRequest(string? Token);

/// <summary><c>POST /v1/management/invitations/accept</c>: the acting user joins through an invitation.</summary>
public sealed class AcceptInvitationEndpoint(IAccessService access) : Endpoint<AcceptInvitationRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.AcceptInvitation);
        Name("acceptInvitation");
        Summary("Accepts an invitation");
        Description("Authorization: Bearer <ts-auth access token>; its sub is the accepting user and the invitation token names the organization. The access token's email, with email_verified true, must match the invitation (403 otherwise; the management service credential carries no verified email, so it cannot accept). 404 for an unknown token, 409 when expired or already a member.");
        Tags(ManagementRoute.AccessTag);
        Produces<OrganizationMember>();
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status403Forbidden);
        Produces<string>(StatusCodes.Status404NotFound);
        Produces<string>(StatusCodes.Status409Conflict);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(AcceptInvitationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Token))
        {
            return TypedResults.BadRequest("token is required");
        }

        // The email comes from the verified token, never from the body: the caller may be the user itself.
        if (HttpContext.User.VerifiedEmail() is not { } email)
        {
            return TypedResults.Json("the access token has no verified email", statusCode: StatusCodes.Status403Forbidden);
        }

        return ManagementRoute.Ok(await access.AcceptInvitationAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.Email(email), request.Token, cancellationToken));
    }
}
