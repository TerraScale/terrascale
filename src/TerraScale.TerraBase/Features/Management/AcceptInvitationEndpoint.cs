using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

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
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(AcceptInvitationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Token))
        {
            return ErrorResponse.From(new Validation("token is required"));
        }

        // The email comes from the verified token, never from the body: the caller may be the user itself.
        if (HttpContext.User.VerifiedEmail() is not { } email)
        {
            return ErrorResponse.From(new Forbidden("the access token has no verified email"));
        }

        return ManagementRoute.CheckEmail(email) switch
        {
            Validation error => ErrorResponse.From(error),
            string valid => await access.AcceptInvitationAsync(ManagementRoute.UserId(HttpContext), valid, request.Token, cancellationToken) switch
            {
                OrganizationMember value => TypedResults.Ok(value),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
    }
}
