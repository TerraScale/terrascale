using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>POST …/organizations/{organization}/ownership-transfer/confirm</c>: the nominee accepts ownership.</summary>
public sealed class ConfirmOwnershipTransferEndpoint(IAccessService access) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.OwnershipTransfer + "/confirm");
        Name("confirmOwnershipTransfer");
        Summary("Accepts a pending ownership transfer");
        Description($"Only the nominee may confirm. It becomes an owner and the owner who started the transfer becomes a member. 404 when none is pending, it expired, or its starter is no longer an owner. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<OrganizationMember>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.Ok(await access.ConfirmTransferAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), cancellationToken));
}
