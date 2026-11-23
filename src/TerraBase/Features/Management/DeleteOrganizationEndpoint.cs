using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class DeleteOrganizationEndpoint(ManagementService management, CatalogStore catalog)
    : Endpoint<RouteInput, Result<Done, Unauthorized, NotFound, Conflict, Unavailable>>
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

    public override async ValueTask<Result<Done, Unauthorized, NotFound, Conflict, Unavailable>> HandleAsync(RouteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await management.OwnedOrganizationAsync(HttpContext.Caller, request.Organization, cancellationToken) switch
        {
            OrganizationRecord organization => await catalog.DeleteOrganizationAsync(organization.Id, cancellationToken) switch
            {
                Done done => done,
                Conflict conflict => conflict,
                Unavailable unavailable => unavailable,
            },
            Unauthorized unauthorized => unauthorized,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
