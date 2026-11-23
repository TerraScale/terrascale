using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class OrganizationDatabaseCountEndpoint(CatalogStore catalog)
    : Endpoint<AdminInput, Result<CountBody, Forbidden, NotFound, Unavailable>>
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

    public override async ValueTask<Result<CountBody, Forbidden, NotFound, Unavailable>> HandleAsync(AdminInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "catalog.read"))
        {
            return new Forbidden("counting databases needs the catalog.read grant", "forbidden_caller");
        }

        return ManagementService.Id(request.Organization) is { } organizationId
            ? await catalog.CountOrganizationDatabasesAsync(organizationId, cancellationToken) switch
            {
                long count => new CountBody(count),
                Unavailable unavailable => unavailable,
            }
            : new NotFound("the organization does not exist", "unknown_organization");
    }
}
