using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

public readonly union ResolveProjectResult(string, NotFound, Unavailable);

internal interface IProjectCatalog
{
    ValueTask<ResolveProjectResult> ResolveProjectAsync(string projectId, string actor, string? createIn, CancellationToken cancellationToken);

    ValueTask<ResolveProjectResult> ProjectOrganizationAsync(string projectId, CancellationToken cancellationToken);

    ValueTask<SetPlanResult> SetEnrolledPlanAsync(string projectId, PlanTier plan, CancellationToken cancellationToken);

    ValueTask<SetPlanResult> SetStoragePlanAsync(string organizationId, PlanTier plan, CancellationToken cancellationToken);

    ValueTask<CloseProjectResult> CloseProjectAsync(string projectId, string closureId, string authorityOrganizationId, int version, bool forwarded, CancellationToken cancellationToken);

}
