using TerraScale.Endpoints;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>PATCH /v1/management/organizations/{organization}</c>: renames an organization.</summary>
public sealed class RenameOrganizationEndpoint(IManagementService management) : Endpoint<NameRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Patch(ManagementRoute.Organization);
        Name("renameOrganization");
        Summary("Renames an organization");
        Description("Needs admin (403 otherwise). " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<Organization>();
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status403Forbidden);
        Produces<string>(StatusCodes.Status404NotFound);
        Produces<string>(StatusCodes.Status409Conflict);
        RequireAuthorization(DashboardAuthenticationHandler.PolicyName);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ManagementRoute.Ok(await management.RenameOrganizationAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.DisplayName(request), cancellationToken));
    }
}
