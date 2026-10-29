using TerraBase.Errors;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

public sealed class CancelOwnershipTransferEndpoint(IAccessService access) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Delete(ManagementRoute.OwnershipTransfer);
        Metadata(AnchoredRevocation.Instance);
        Name("cancelOwnershipTransfer");
        Summary("Cancels the pending ownership transfer");
        Description($"Needs owner, or to be the nominee. 404 when none is pending. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await access.CancelTransferAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), cancellationToken) switch
        {
            Done => TypedResults.NoContent(),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
