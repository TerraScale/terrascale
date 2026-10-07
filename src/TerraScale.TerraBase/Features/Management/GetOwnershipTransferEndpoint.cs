using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>GET …/organizations/{organization}/ownership-transfer</c>: reads the pending ownership transfer.</summary>
public sealed class GetOwnershipTransferEndpoint(IAccessService access) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.OwnershipTransfer);
        Name("getOwnershipTransfer");
        Summary("Reads the pending ownership transfer");
        Description($"Needs member. 404 when none is pending or it expired. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<OwnershipTransfer>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.Ok(await access.GetTransferAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), cancellationToken));
}
