using TerraBase.Errors;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

public sealed class RevokeInvitationEndpoint(IAccessService access) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Delete(ManagementRoute.Invitations + "/{invitation}");
        Metadata(AnchoredRevocation.Instance);
        Name("revokeInvitation");
        Summary("Revokes an invitation");
        Description($"Needs admin. The invitation becomes revoked and its token stops working; 409 when it is no longer pending. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await access.RevokeInvitationAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "invitation"), cancellationToken) switch
        {
            Done => TypedResults.NoContent(),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
