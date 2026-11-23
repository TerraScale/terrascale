using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class SetStorageOrganizationPlanEndpoint(CatalogStore catalog)
    : Endpoint<PlanInput, Result<OrganizationBody, Validation, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Put("/admin/organizations/{organization}/plan");
        Name("adminSetOrganizationPlan");
        Summary("Sets an organization's storage plan");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<Result<OrganizationBody, Validation, Forbidden, NotFound, Unavailable>> HandleAsync(PlanInput request, CancellationToken cancellationToken) =>
        await PlanFlow.SetOrganizationPlanAsync(HttpContext.Caller, catalog, request, cancellationToken);
}
