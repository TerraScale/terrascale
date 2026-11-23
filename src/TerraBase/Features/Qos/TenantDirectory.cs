using TerraBase.Features.Catalog;
using TerraBase.Features.Platform;

namespace TerraBase.Features.Qos;

internal sealed record NoTenant
{
    public static NoTenant Instance { get; } = new();
}

internal readonly union TenantResolution(Tenant, NoTenant, Unavailable);

internal sealed class TenantDirectory(CatalogStore catalog)
{
    public async ValueTask<TenantResolution> ForProjectAsync(Ulid projectId, CancellationToken cancellationToken)
    {
        var project = await catalog.GetProjectAsync(projectId, cancellationToken);
        return project switch
        {
            ProjectRecord record => await ForAsync(record, cancellationToken),
            NotFound => NoTenant.Instance,
            Unavailable unavailable => unavailable,
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
            NotFound => new Tenant(project.OrganizationId, project.Plan ?? Plans.Hobby),
            Unavailable unavailable => unavailable,
        };
    }
}
