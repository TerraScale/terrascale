using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Platform;
using TerraBase.Hosting;

namespace TerraBase.Features.Qos;

internal sealed record NoTenant
{
    public static NoTenant Instance { get; } = new();
}

internal sealed record ForeignProject
{
    public static ForeignProject Instance { get; } = new();
}

internal readonly union TenantResolution(Tenant, NoTenant, BackendUnavailable);

internal readonly union CallerTenantResolution(Tenant, NoTenant, ForeignProject, BackendUnavailable);

internal sealed class TenantDirectory(CatalogStore catalog)
{
    public bool TryForCallerProject(Caller caller, Ulid projectId, out CallerTenantResolution resolved)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (PinnedElsewhere(caller, projectId))
        {
            resolved = ForeignProject.Instance;
            return true;
        }

        if (catalog.TryGetProject(projectId, out var cachedProject)
            && catalog.TryGetOrganization(cachedProject.OrganizationId, out var cachedOrganization))
        {
            resolved = Owns(caller, cachedProject, cachedOrganization.CreatedBy)
                ? cachedProject.OrganizationId == PlatformCatalog.OrganizationId
                    ? NoTenant.Instance
                    : cachedOrganization.AsTenant()
                : ForeignProject.Instance;
            return true;
        }

        resolved = default;
        return false;
    }

    public ValueTask<CallerTenantResolution> ForCallerProjectAsync(Caller caller, Ulid projectId, CancellationToken cancellationToken) =>
        TryForCallerProject(caller, projectId, out var resolved)
            ? ValueTask.FromResult(resolved)
            : ForCallerProjectSlowAsync(caller, projectId, cancellationToken);

    private async ValueTask<CallerTenantResolution> ForCallerProjectSlowAsync(Caller caller, Ulid projectId, CancellationToken cancellationToken)
    {
        var project = await catalog.GetProjectAsync(projectId, cancellationToken);
        return project switch
        {
            ProjectRecord record when record.OrganizationId == PlatformCatalog.OrganizationId => await OwnedAsync(caller, record, cancellationToken) switch
            {
                Tenant => NoTenant.Instance,
                NoTenant none => none,
                ForeignProject foreign => foreign,
                BackendUnavailable unavailable => unavailable,
            },
            ProjectRecord record => await OwnedAsync(caller, record, cancellationToken),
            ResourceNotFound => ForeignProject.Instance,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public bool TryForOrganization(Ulid organizationId, out TenantResolution resolved)
    {
        if (organizationId == PlatformCatalog.OrganizationId)
        {
            resolved = NoTenant.Instance;
            return true;
        }

        if (catalog.TryGetOrganization(organizationId, out var cached))
        {
            resolved = cached.AsTenant();
            return true;
        }

        resolved = default;
        return false;
    }

    public ValueTask<TenantResolution> ForOrganizationAsync(Ulid organizationId, CancellationToken cancellationToken) =>
        TryForOrganization(organizationId, out var resolved)
            ? ValueTask.FromResult(resolved)
            : ForOrganizationSlowAsync(organizationId, cancellationToken);

    private async ValueTask<TenantResolution> ForOrganizationSlowAsync(Ulid organizationId, CancellationToken cancellationToken)
    {
        var organization = await catalog.GetOrganizationAsync(organizationId, cancellationToken);
        return organization switch
        {
            OrganizationRecord record => record.AsTenant(),
            ResourceNotFound => NoTenant.Instance,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public ValueTask<TenantResolution> ForAsync(ProjectRecord project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.OrganizationId == PlatformCatalog.OrganizationId)
        {
            return ValueTask.FromResult<TenantResolution>(NoTenant.Instance);
        }

        if (catalog.TryGetOrganization(project.OrganizationId, out var cached))
        {
            return ValueTask.FromResult<TenantResolution>(cached.AsTenant());
        }

        return ForSlowAsync(project, cancellationToken);
    }

    private async ValueTask<TenantResolution> ForSlowAsync(ProjectRecord project, CancellationToken cancellationToken)
    {
        var organization = await catalog.GetOrganizationAsync(project.OrganizationId, cancellationToken);
        return organization switch
        {
            OrganizationRecord record => record.AsTenant(),
            ResourceNotFound => new Tenant(project.OrganizationId, project.Plan ?? Plans.Hobby),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<CallerTenantResolution> OwnedAsync(Caller caller, ProjectRecord project, CancellationToken cancellationToken)
    {
        var organization = await catalog.GetOrganizationAsync(project.OrganizationId, cancellationToken);
        return organization switch
        {
            OrganizationRecord record when Owns(caller, project, record.CreatedBy) => record.AsTenant(),
            ResourceNotFound when Owns(caller, project, creator: null) => new Tenant(project.OrganizationId, project.Plan ?? Plans.Hobby),
            OrganizationRecord or ResourceNotFound => ForeignProject.Instance,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private static bool PinnedElsewhere(Caller caller, Ulid projectId) => caller switch
    {
        ApiKeyCaller { Key.ProjectId: { } scoped } => scoped != projectId,
        ProjectCaller token => token.ProjectId != projectId,
        EndUserCaller endUser => endUser.ProjectId != projectId,
        AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or UserCaller => false,
    };

    private static bool Owns(Caller caller, ProjectRecord project, string? creator) => caller switch
    {
        ApiKeyCaller key => key.Key.OrganizationId == project.OrganizationId && (key.Key.ProjectId is not { } scoped || scoped == project.Id),
        UserCaller user => creator is not null && string.Equals(creator, user.UserId, StringComparison.Ordinal),
        ProjectCaller token => token.ProjectId == project.Id && (token.OrganizationId is not { } organization || organization == project.OrganizationId),
        EndUserCaller endUser => endUser.ProjectId == project.Id,
        AnonymousCaller or PeerCaller or ServiceCaller => false,
    };
}
