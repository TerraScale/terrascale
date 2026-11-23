using TerraBase.Features.Catalog;
using TerraBase.Features.Platform;
using TerraBase.Hosting;

namespace TerraBase.Features.Qos;

internal sealed record NoTenant
{
    public static NoTenant Instance { get; } = new();
}

internal readonly union TenantResolution(Tenant, NoTenant, BackendUnavailable);

internal sealed class TenantDirectory(CatalogStore catalog)
{
    public async ValueTask<TenantResolution> ForProjectAsync(Ulid projectId, CancellationToken cancellationToken)
    {
        var project = await catalog.GetProjectAsync(projectId, cancellationToken);
        return project switch
        {
            ProjectRecord record => await ForAsync(record, cancellationToken),
            ResourceNotFound => NoTenant.Instance,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<TenantResolution> ForAsync(ProjectRecord project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.OrganizationId == PlatformCatalog.OrganizationId)
        {
            return NoTenant.Instance;
        }

        var organization = await catalog.GetOrganizationAsync(project.OrganizationId, cancellationToken);
        return organization switch
        {
            OrganizationRecord record => new Tenant(record.Id, record.Plan),
            ResourceNotFound => new Tenant(project.OrganizationId, project.Plan ?? Plans.Hobby),
            BackendUnavailable unavailable => unavailable,
        };
    }
}
