using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union DeleteOrganizationResponse(Completed, CredentialRejected, CallerForbidden, ResourceNotFound, StateConflict, BackendUnavailable);

internal sealed class DeleteOrganizationEndpoint(ManagementService management, CatalogStore catalog)
    : Endpoint<RouteInput, DeleteOrganizationResponse>
{
    public override void Configure()
    {
        Delete(ManagementRoute.Organization);
        Name("deleteOrganization");
        Summary("Deletes an organization without projects");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<DeleteOrganizationResponse> HandleAsync(RouteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ManagementService.DirectUser(HttpContext.Caller) is { } forbidden)
        {
            return forbidden;
        }

        var outcome = await management.OwnedOrganizationAsync(HttpContext.Caller, request.Organization, cancellationToken);
        return outcome switch
        {
            OrganizationRecord organization => await catalog.DeleteOrganizationAsync(organization.Id, cancellationToken) switch
            {
                Completed done => done,
                StateConflict conflict => conflict,
                BackendUnavailable unavailable => unavailable,
            },
            CredentialRejected unauthorized => unauthorized,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
