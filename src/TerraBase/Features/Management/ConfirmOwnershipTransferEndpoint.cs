using TerraBase.Errors;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

public sealed class ConfirmOwnershipTransferEndpoint(IAccessService access) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post(ManagementRoute.OwnershipTransfer + "/confirm");
        Metadata(AnchoredRevocation.Instance);
        Name("confirmOwnershipTransfer");
        Summary("Accepts a pending ownership transfer");
        Description($"Only the nominee may confirm. It becomes an owner and the owner who started the transfer becomes a member. 404 when none is pending, it expired, or its starter is no longer an owner. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<OrganizationMember>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await access.ConfirmTransferAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), cancellationToken) switch
        {
            OrganizationMember value => TypedResults.Ok(value),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
