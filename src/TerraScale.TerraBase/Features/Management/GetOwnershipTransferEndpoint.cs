using TerraScale.Endpoints;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

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
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status403Forbidden);
        Produces<string>(StatusCodes.Status404NotFound);
        RequireAuthorization(DashboardAuthenticationHandler.PolicyName);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.Ok(await access.GetTransferAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), cancellationToken));
}
