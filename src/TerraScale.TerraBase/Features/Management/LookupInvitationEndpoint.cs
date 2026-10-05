using TerraScale.Endpoints;
using TerraScale.TerraBase.Errors;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>A token hash to look up.</summary>
/// <param name="TokenHash">Lowercase hex SHA-256 of the invitation token.</param>
public sealed record LookupInvitationRequest(string? TokenHash);

/// <summary><c>POST /v1/management/invitations/lookup</c>: finds a pending invitation by its token hash.</summary>
public sealed class LookupInvitationEndpoint(IAccessService access) : Endpoint<LookupInvitationRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.LookupInvitation);
        Name("lookupInvitation");
        Summary("Finds a pending invitation by its token hash");
        Description($"tokenHash: lowercase hex SHA-256 of the token (64 characters). Answers a pending, unexpired invitation with only its organization's id and name; 404 otherwise. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<FoundInvitation>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(LookupInvitationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ManagementRoute.UserId(HttpContext);
        if (request.TokenHash is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigitLower))
        {
            return ErrorResponse.From(new Validation("tokenHash must be 64 lowercase hex characters"));
        }

        return await access.FindInvitationAsync(hash, cancellationToken) is { } found
            ? TypedResults.Ok(found)
            : ErrorResponse.From(new NotFound("no pending invitation has this token"));
    }
}
