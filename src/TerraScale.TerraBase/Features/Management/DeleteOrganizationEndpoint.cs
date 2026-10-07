using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>DELETE /v1/management/organizations/{organization}</c>: deletes an organization without projects.</summary>
public sealed class DeleteOrganizationEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(ManagementRoute.Organization);
        Name("deleteOrganization");
        Summary("Deletes an organization");
        Description("Needs owner (403 otherwise). Fails with 409 while the organization still has projects. Also deletes its members, invitations, ownership transfer and audit log." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.NoContent(await management.DeleteOrganizationAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), cancellationToken));
}
