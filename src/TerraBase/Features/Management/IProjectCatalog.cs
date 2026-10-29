using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>A verified project namespace, known absence, or an unreadable catalog.</summary>
public readonly union ResolveProjectResult(string, NotFound, Unavailable);

/// <summary>Resolves ts-auth project ids to the compatible on-disk catalog namespace.</summary>
public interface IProjectCatalog
{
    /// <summary>
    /// Finds a verified storage namespace, or registers a signed project's first database atomically under
    /// <paramref name="createIn"/>, the ts-auth organization the token names; null only resolves.
    /// </summary>
    ValueTask<ResolveProjectResult> ResolveProjectAsync(string projectId, string actor, string? createIn, CancellationToken cancellationToken);

    /// <summary>The organization that registered <paramref name="projectId"/>, if registered.</summary>
    ValueTask<ResolveProjectResult> ProjectOrganizationAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>Sets the plan of the storage organization a signed project enrolled; projects created through management routes keep their creator's plan.</summary>
    ValueTask<SetPlanResult> SetEnrolledPlanAsync(string projectId, PlanTier plan, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the plan of the operator-owned storage organization named by its ts-auth organization id, creating it for a paid plan
    /// before its first database enrolls; organizations created through management routes keep their creator's plan.
    /// </summary>
    ValueTask<SetPlanResult> SetStoragePlanAsync(string organizationId, PlanTier plan, CancellationToken cancellationToken);

    /// <summary>
    /// Fences a ts-auth project for closure, or reports the fence's status; idempotent for the same closure id and
    /// organization. Version 1 requires emptiness; version 2 authorizes account erasure. A conflicting binding writes nothing.
    /// </summary>
    ValueTask<CloseProjectResult> CloseProjectAsync(string projectId, string closureId, string authorityOrganizationId, int version, CancellationToken cancellationToken);

}
