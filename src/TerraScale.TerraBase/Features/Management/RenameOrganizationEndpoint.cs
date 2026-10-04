using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
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
        Tags(ManagementRoute.Tag);
        Produces<Organization>();
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status404NotFound);
        Produces<string>(StatusCodes.Status409Conflict);
        RequireAuthorization(ApiKeyScopes.Management);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ManagementRoute.Ok(await management.RenameOrganizationAsync(ManagementRoute.Value(HttpContext, "organization"), ManagementRoute.DisplayName(request), cancellationToken));
    }
}
