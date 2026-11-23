using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union OrganizationDatabaseCountResponse(CountBody, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal sealed class OrganizationDatabaseCountEndpoint(CatalogStore catalog)
    : Endpoint<AdminInput, OrganizationDatabaseCountResponse>
{
    public override void Configure()
    {
        Get("/v1/sync/organizations/{organization}/databases/count");
        Name("countOrganizationDatabases");
        Summary("Counts an organization's databases");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<OrganizationDatabaseCountResponse> HandleAsync(AdminInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "catalog.read"))
        {
            return new CallerForbidden("counting databases needs the catalog.read grant", "forbidden_caller");
        }

        return ManagementService.Id(request.Organization) is { } organizationId
            ? await catalog.CountOrganizationDatabasesAsync(organizationId, cancellationToken) switch
            {
                long count => new CountBody(count),
                BackendUnavailable unavailable => unavailable,
            }
            : new ResourceNotFound("the organization does not exist", "unknown_organization");
    }
}
